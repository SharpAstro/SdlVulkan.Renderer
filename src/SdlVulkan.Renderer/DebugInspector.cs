#if DEBUG
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using DIR.Lib;
using DIR.Lib.Diagnostics;
using Layout = DIR.Lib.Layout;

namespace SdlVulkan.Renderer;

/// <summary>
/// DEBUG-only live UI debug inspector. Hosts a TCP command server (ephemeral port) plus a UDP
/// multicast discovery responder so a sidecar (the published SdlVulkan.Renderer.Inspector MCP
/// server) can discover this running app and drive it: read the clickable region tree, capture a
/// screenshot, inject input, and post named signals.
/// <para>
/// Threading: the socket/UDP servers run on background tasks and only ENQUEUE commands. Every
/// command executes on the RENDER THREAD inside the core's <c>Pump</c>, which is chained onto
/// <see cref="SdlEventLoop.OnLoopIteration"/> by <see cref="Attach(SdlEventLoop, SdlWindowView, DebugInspectorOptions)"/>.
/// That per-iteration hook fires EVERY loop iteration -- including while a window is minimized and
/// nothing renders -- so commands keep draining on a minimized window (a per-frame hook would not).
/// Enqueueing still calls <see cref="SdlWindowView.RequestRedraw"/> to wake the loop promptly
/// (latency bounded by the loop's ~16ms wait).
/// </para>
/// The entire type is compiled only in DEBUG builds, so no release artifact carries it.
/// </summary>
public sealed class DebugInspector : IDisposable, IDebugInspectorHost, IDebugInspectorSteppedHost
{

    private readonly DebugInspectorOptions _opts;
    private readonly SdlWindowView _view;
    private readonly SdlEventLoop _loop; // for frame-timing readback (frameStats)
    private readonly string _startedAtUtc;
    private DebugInspectorCore? _core;

    private DebugInspector(SdlEventLoop loop, SdlWindowView view, DebugInspectorOptions opts)
    {
        _opts = opts;
        _view = view;
        _loop = loop;
        _startedAtUtc = DateTimeOffset.UtcNow.ToString("o");
    }

    /// <summary>The ephemeral loopback port the command server bound to; 0 before
    /// <see cref="Attach(SdlEventLoop, DebugInspectorOptions)"/>.</summary>
    public int Port => _core?.Port ?? 0;

    // ---------------- IDebugInspectorHost: the parts that are actually SDL ----------------

    /// <inheritdoc />
    public string AppName => string.IsNullOrEmpty(_opts.AppName)
        ? System.Diagnostics.Process.GetCurrentProcess().ProcessName
        : _opts.AppName;

    /// <summary>
    /// An SDL-hosted pixel window. Lets a sidecar drop replies from surfaces whose verbs it does not speak:
    /// discovery is one shared group, so a terminal app on the same machine answers the same query.
    /// </summary>
    public string SurfaceKind => "sdl";

    /// <summary>
    /// Wakes the loop so a queued command is serviced promptly. The loop parks in WaitEventTimeout when
    /// idle, so without this a command waits on unrelated input.
    /// </summary>
    public void Poke() => _view.RequestRedraw();

    /// <summary>
    /// Window title and start time, added to the discovery reply so a driver can tell two windows of the
    /// same app apart. Read per reply, which is what keeps a title that changes at runtime current.
    /// </summary>
    public string? DiscoveryExtras =>
        $"\"title\":{DebugInspectorCore.Quote(SafeInvoke(_opts.WindowTitle))}," +
        $"\"startedAt\":{DebugInspectorCore.Quote(_startedAtUtc)}";

    /// <summary>
    /// Attaches the inspector to the given loop + window view and starts the command server. Chains the
    /// core's pump onto the loop's <see cref="SdlEventLoop.OnLoopIteration"/>. Call once, under
    /// <c>#if DEBUG</c>, after the loop's callbacks are wired but before <see cref="SdlEventLoop.Run"/>.
    /// The returned <see cref="IDisposable"/> stops the server when disposed.
    /// </summary>
    public static DebugInspector Attach(SdlEventLoop loop, SdlWindowView view, DebugInspectorOptions opts)
    {
        var inspector = new DebugInspector(loop, view, opts);

        // Supplying a layout callback opts this process into PixelWidgetBase layout capture, so widgets
        // retain their arranged tree for describeLayout. Off by default (zero paint overhead otherwise).
        if (opts.GetLayout is not null)
        {
            LayoutInspection.Enabled = true;
        }

        inspector._core = DebugInspectorCore.Start(inspector, opts.EnableDiscovery);

        // The pump goes on the loop's per-iteration hook (OnLoopIteration), NOT OnPostFrame: it must run
        // every iteration -- including when nothing rendered because the window is minimized -- so commands
        // (notably `restore`) are still serviced on a minimized window. The hook fires inside Run on the
        // render thread, so all Vulkan/widget/input access stays render-thread-safe. Lambda-compose (the
        // framework's wiring style) so any prior hook runs first.
        var prev = loop.OnLoopIteration;
        loop.OnLoopIteration = () =>
        {
            prev?.Invoke();
            inspector._core!.Pump();
        };
        return inspector;
    }

    /// <summary>Single-window convenience overload that uses the loop's primary view.</summary>
    public static DebugInspector Attach(SdlEventLoop loop, DebugInspectorOptions opts)
        => Attach(loop, loop.DebugPrimaryView
            ?? throw new InvalidOperationException("DebugInspector.Attach(loop, opts) requires the single-window SdlEventLoop constructor; pass an explicit SdlWindowView otherwise."),
            opts);

    public void Dispose() => _core?.Dispose();

    // Resolve a "key" command string to an InputKey.
    //
    // IMPORTANT (so the next person doesn't re-debug this): a synthesized key
    // travels the EXACT SAME path as a hardware keypress. ExecuteKey calls
    // _view.OnKeyDown(key, mods); SdlEventLoop invokes that very delegate for a
    // real SDL KeyDown (Scancode.ToInputKey). So an injected key reaches the
    // focused text field / search box identically to a human keypress -- there
    // is NO separate text-field routing to special-case. e.g. pressing Enter
    // while the sky-map search box is focused commits it, exactly like a user.
    //
    // The only footgun is the NAME: keys are DIR.Lib.InputKey values --
    // Enter (NOT "Return"), Escape, Tab, Space, Up/Down/Left/Right, F1-F12,
    // A-Z, D0-D9, Plus/Minus/... We accept the common natural aliases below so
    // "Return"/"Esc"/"ArrowUp"/"1" just work, and an unknown name returns a
    // clear error listing the valid set rather than an opaque parse failure.
    internal static InputKey ResolveInputKey(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0)
            throw new ArgumentException("key is required (an InputKey name, e.g. Enter, Escape, Tab, A, F3)");

        var canonical = name.ToLowerInvariant() switch
        {
            "return" or "ret" or "cr" => "Enter",
            "esc" => "Escape",
            "spacebar" or "spc" => "Space",
            "del" => "Delete",
            "bksp" or "bs" => "Backspace",
            "pgup" => "PageUp",
            "pgdn" or "pgdown" => "PageDown",
            "arrowup" => "Up",
            "arrowdown" => "Down",
            "arrowleft" => "Left",
            "arrowright" => "Right",
            "0" or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9" => "D" + name,
            _ => name,
        };

        if (Enum.TryParse<InputKey>(canonical, ignoreCase: true, out var key))
            return key;

        throw new ArgumentException(
            $"unknown key '{raw}'. Valid InputKey names: {string.Join(", ", Enum.GetNames<InputKey>())}. " +
            "Aliases accepted: Return=Enter, Esc=Escape, Spacebar=Space, Del=Delete, " +
            "PgUp/PgDn=PageUp/PageDown, ArrowUp/Down/Left/Right, 0-9=D0-D9.");
    }

    // Resolve a modifier string to InputModifier flags. Tolerant of how a caller
    // spells a combo: "Ctrl", "ctrl+shift", "Ctrl, Shift", "CtrlShift" and
    // "Control" all work (Enum.Parse only accepts the comma-separated [Flags]
    // form, so "CtrlShift" -- which our tool docs advertise -- would otherwise
    // throw). Matches known tokens as substrings, so order/separator/case-free.
    /// <summary>
    /// Resolves a driver's modifier string. Substring-matched and case-insensitive, so "Ctrl",
    /// "ctrl+shift" and "CtrlShift" all work.
    /// </summary>
    /// <remarks>
    /// <b>Unrecognised text throws, like <see cref="ResolveInputKey"/> does.</b> It used to resolve to
    /// <see cref="InputModifier.None"/>, which delivered a BARE key or click — and a bare key is frequently a
    /// different valid binding rather than a no-op, so the mistake surfaced as the app ignoring a correct
    /// chord rather than as a bad request. Chess is the worked example: it flips the board on Ctrl+F while
    /// bare <c>f</c> selects file f, so a dropped modifier silently did something else. Returning None for
    /// unknown input also made this the one resolver in this file that did not reject what it could not
    /// understand.
    /// </remarks>
    internal static InputModifier ResolveModifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return InputModifier.None;
        var s = raw.ToLowerInvariant();
        if (s is "none" or "0") return InputModifier.None;

        var mod = InputModifier.None;
        if (s.Contains("ctrl") || s.Contains("control")) mod |= InputModifier.Ctrl;
        if (s.Contains("shift")) mod |= InputModifier.Shift;
        if (s.Contains("alt") || s.Contains("option")) mod |= InputModifier.Alt;

        if (mod == InputModifier.None)
        {
            throw new ArgumentException(
                $"unknown modifiers '{raw}'. Accepted: Ctrl (or Control), Shift, Alt (or Option), combined in "
                + "any spelling — \"Ctrl+Shift\", \"CtrlShift\", \"ctrl-alt\". Omit the field, or pass "
                + "\"none\"/\"0\", for no modifiers. Refused rather than treated as none, because a dropped "
                + "modifier delivers a bare key, which is often a different binding rather than a no-op.");
        }

        return mod;
    }

    // ---------------- UDP multicast discovery responder (background thread) ----------------

    // ---------------- The verbs ----------------

    /// <summary>
    /// Every instantaneous verb, run on the render thread by the core's pump. Parameters are read straight
    /// off the request: there is no intermediate command object any more, because the only reason one
    /// existed was to carry a parsed request across the queue the core now owns.
    /// </summary>
    public string? Invoke(string method, JsonElement p) => method switch
    {
        "describe" => ExecuteDescribe(),
        "describeLayout" => ExecuteDescribeLayout(),
        "signals" => ExecuteListSignals(),
        "minimize" => ExecuteWindowState(static w => w.Minimize()),
        "maximize" => ExecuteWindowState(static w => w.Maximize()),
        "restore" => ExecuteWindowState(static w => w.Restore()),
        "validationReport" => ExecuteValidationReport(),
        "frameStats" => ExecuteFrameStats(),
        "click" => ExecuteClickAt(Coord(p, "x"), Coord(p, "y"), Mods(p), Clicks(p)),
        "clickLabel" => ExecuteClickLabel(RequiredString(p, "label"), Clicks(p)),
        "key" => ExecuteKey(ResolveInputKey(RequiredString(p, "key")), Mods(p)),
        // "text" is the name the batch contract advertises; "s" is what the direct verb has always sent.
        "text" => ExecuteText(RequiredString(p, "text", "s")),
        "scroll" => ExecuteScroll(Coord(p, "x"), Coord(p, "y"), Coord(p, "scrollY"), Mods(p)),
        "drag" => ExecuteDrag(Coord(p, "x1"), Coord(p, "y1"), Coord(p, "x2"), Coord(p, "y2"), Mods(p), DragSteps(p)),
        "postSignal" => ExecutePostSignal(RequiredString(p, "name"), SignalArgs(p)),
        // Reachable only OUTSIDE a batch, where there are no frames to wait for -- inside one the core
        // handles it. A no-op rather than an error, so a driver can send a uniform step list either way.
        "wait" => "\"waited\"",
        // Null rather than a throw: the core owns the "unknown method" wording, and it should read the same
        // whichever surface refused it.
        _ => null,
    };

    // Every reader below NAMES what it wanted when it is not there. GetProperty throws
    // "The given key was not present in the dictionary", which reaches the caller as the whole
    // explanation of a refused step: it says nothing about which step, which parameter, or what would
    // have worked. That cost real time to diagnose from the other end of the wire.
    private static float Coord(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetSingle()
            : throw new ArgumentException($"missing or non-numeric parameter '{name}'");

    /// <summary>
    /// First present string among <paramref name="names"/>. Several verbs are reachable both directly
    /// and as a batch step, and the two spellings drifted apart; accepting either makes a step list and
    /// a direct call interchangeable, which is what a caller assumes to begin with.
    /// </summary>
    internal static string RequiredString(JsonElement p, params string[] names)
    {
        foreach (var name in names)
        {
            if (p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? "";
            }
        }

        throw new ArgumentException(names.Length == 1
            ? $"missing string parameter '{names[0]}'"
            : $"missing string parameter, expected one of: {string.Join(", ", names)}");
    }

    /// <summary>
    /// Signal arguments as either an object (<c>args</c>) or a JSON string (<c>argsJson</c>, which is
    /// what the direct postSignal verb carries). Absent or blank yields a default element, so the
    /// signal is built from its own declared defaults.
    /// <para>
    /// Reading only <c>args</c> is why a batch step that passed <c>argsJson</c> reported success and
    /// then did nothing: every field fell back to its default, and for a set-view signal whose fields
    /// all default to "leave unchanged", doing nothing is indistinguishable from working.
    /// </para>
    /// </summary>
    internal static JsonElement SignalArgs(JsonElement p)
    {
        if (p.TryGetProperty("args", out var args)
            && args.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return args.Clone();
        }

        if (p.TryGetProperty("argsJson", out var json)
            && json.ValueKind == JsonValueKind.String
            && json.GetString() is { Length: > 0 } text
            && !string.IsNullOrWhiteSpace(text))
        {
            using var parsed = JsonDocument.Parse(text);
            return parsed.RootElement.Clone();
        }

        return default;
    }

    private static InputModifier Mods(JsonElement p) => ResolveModifier(
        p.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);

    /// <summary>
    /// How many clicks the press is: 1 a single click, 2 a double, 3 a triple. Clamped to what SDL can
    /// report in a byte and to what any real pointer produces.
    /// </summary>
    private static int Clicks(JsonElement p)
        => p.TryGetProperty("clicks", out var c) && c.ValueKind == JsonValueKind.Number
            ? Math.Clamp(c.GetInt32(), 1, 7) : 1;

    private static int DragSteps(JsonElement p)
        => p.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Number
            ? Math.Clamp(s.GetInt32(), 1, 64) : 8;

    // ---------------- IDebugInspectorSteppedHost: the one verb that spans frames ----------------

    /// <summary>
    /// The frame-spanning verbs. <c>batch</c> is absent because the core owns it now: stepping one command
    /// per iteration is pure scheduling with no SDL in it, and it only ever lived here because the core
    /// could not express it. <c>screenshot</c> spans frames because the capture is recorded into the next
    /// presented frame and its readback rides that frame's fence -- the one legal way to read a swapchain
    /// image (see VulkanContext.SwapchainReadback.cs).
    /// </summary>
    private static readonly string[] FrameSpanningVerbs = ["pressHold", "screenshot"];

    /// <inheritdoc />
    public IReadOnlyCollection<string> SteppedMethods => FrameSpanningVerbs;

    /// <inheritdoc />
    public IDebugInspectorOperation Begin(string method, JsonElement p)
    {
        if (method == "screenshot")
        {
            // Begin runs on the render thread, so asking the context directly is safe. The request marks
            // the NEXT presented frame to capture itself pre-present; the redraw makes that frame happen
            // even when the app is idle.
            _view.Renderer.Context.RequestPresentCapture();
            _view.RequestRedraw();
            return new ScreenshotOperation(_view);
        }

        var x = Coord(p, "x");
        var y = Coord(p, "y");
        var mods = Mods(p);
        var durationMs = (int)Math.Clamp(
            (p.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 1.0)
            * 1000.0, 50, 300000);

        // Pressed HERE and released in Advance, so the button stays down across frames and the app ticks
        // THROUGH the hold -- a long-press or hold-to-repeat timer advances -- instead of the render thread
        // blocking for the duration. Begin runs on the render thread, so touching the view is safe.
        _view.DispatchPointerMove(x, y); // cache the position; consumers may read it on MouseUp
        _view.DispatchPointerDown(1, x, y, 1, mods);
        return new HoldOperation(_view, x, y, durationMs);
    }

    /// <summary>The left button held down for a wall-clock duration, then released.</summary>
    private sealed class HoldOperation(SdlWindowView view, float x, float y, int durationMs)
        : IDebugInspectorOperation
    {
        private readonly long _startTick = Environment.TickCount64;

        /// <summary>
        /// NOT exclusive, deliberately. A hold that owned the pump would make the UI it puts on screen
        /// unobservable, and inspecting exactly that is the reason to hold a button down.
        /// </summary>
        public bool Exclusive => false;

        public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Min(300, durationMs / 1000.0 + 15));

        public string? Advance()
        {
            if (Environment.TickCount64 - _startTick < durationMs)
            {
                return null; // the core pokes the loop, so the app keeps ticking while held
            }

            view.DispatchPointerUp(1, x, y);
            view.RequestRedraw();
            return $"\"held {durationMs}ms\"";
        }
    }

    /// <summary>
    /// A screenshot spans frames: the capture rides the next presented frame's command buffer and its
    /// readback that frame's fence, so this steps until the snapshot lands (normally two frames).
    /// This replaced a post-present readback of an image the process no longer owned -- the validation
    /// layer flagged every such screenshot, and an illegal barrier on a presented image can park the
    /// GPU queue, so the observer verb itself was a wedge candidate.
    /// </summary>
    private sealed class ScreenshotOperation(SdlWindowView view) : IDebugInspectorOperation
    {
        /// <summary>Not exclusive: the window must keep rendering, because a frame IS the capture vehicle.</summary>
        public bool Exclusive => false;

        /// <summary>Generous: the capture normally lands within two frames; a busy GPU only has to
        /// finish the capture frame, not be quick about it.</summary>
        public TimeSpan Timeout => TimeSpan.FromSeconds(15);

        public string? Advance()
        {
            var ctx = view.Renderer.Context;
            if (ctx.TryTakePresentCapture(out var rgba, out var w, out var h))
            {
                return EncodeScreenshot(rgba, (int)w, (int)h);
            }

            if (ctx.GpuFenceStuck)
            {
                // Structured error rather than more waiting: never queue work behind a fence that is
                // not signalling. The caller can retry once the GPU recovers.
                return ToJson(static jw =>
                {
                    jw.WriteStartObject();
                    jw.WriteString("error", "screenshot unavailable: GPU stalled");
                    jw.WriteEndObject();
                });
            }

            view.RequestRedraw(); // keep frames coming until the capture frame's fence is waited
            return null;
        }
    }

    private string ExecuteDescribe()
    {
        var regions = _opts.GetRegions?.Invoke() ?? [];
        return ToJson(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("regions");
            foreach (var r in regions)
            {
                var (role, label) = RoleLabel(r.Result);
                w.WriteStartObject();
                w.WriteNumber("x", r.X);
                w.WriteNumber("y", r.Y);
                w.WriteNumber("w", r.Width);
                w.WriteNumber("h", r.Height);
                w.WriteString("role", role);
                if (label is null) w.WriteNull("label"); else w.WriteString("label", label);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("appState");
            if (_opts.AppState is null)
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteStartObject();
                _opts.AppState(new DebugStateWriter(w));
                w.WriteEndObject();
            }
            w.WriteEndObject();
        });
    }

    private static (string Role, string? Label) RoleLabel(HitResult hit) => hit switch
    {
        HitResult.ButtonHit b => ("button", b.Action),
        HitResult.TextInputHit => ("textinput", null),
        HitResult.ListItemHit li => ("listitem", $"{li.ListId}[{li.Index}]"),
        HitResult.SliderHit s => ("slider", s.SliderIndex.ToString()),
        _ => (hit.GetType().Name, null) // covers SlotHit<T> and any app-specific HitResult subtype
    };

    // Serializes the FULL arranged layout tree (not just the clickable subset). Each node carries its
    // tree depth (so the flat pre-order list reconstructs the nesting), kind, rect, plus the content /
    // background / hit chrome the painter drew. Empty when GetLayout is unset or the app draws without
    // the layout DSL.
    private string ExecuteDescribeLayout()
    {
        var nodes = _opts.GetLayout?.Invoke() ?? [];
        return ToJson(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("count", nodes.Count);
            w.WriteStartArray("nodes");
            foreach (var an in nodes)
            {
                var node = an.Node;
                var r = an.Bounds;
                w.WriteStartObject();
                w.WriteNumber("depth", an.Depth);
                w.WriteString("kind", LayoutKind(node));
                w.WriteNumber("x", r.X);
                w.WriteNumber("y", r.Y);
                w.WriteNumber("w", r.Width);
                w.WriteNumber("h", r.Height);

                switch (node)
                {
                    case Layout.Node.Stack stack: w.WriteString("axis", stack.Axis.ToString()); break;
                    case Layout.Node.Split split: w.WriteString("axis", split.Axis.ToString()); break;
                    case Layout.Node.Grid grid: w.WriteNumber("columns", grid.Columns); break;
                }

                if (node is Layout.Node.Leaf leaf)
                {
                    switch (leaf.Content)
                    {
                        case Layout.Content.Text text:
                            w.WriteString("text", text.Value);
                            w.WriteNumber("fontSize", text.FontSize);
                            break;
                        case Layout.Content.TextInput field:
                            // A field reports what it HOLDS and whether it has the keyboard, because
                            // "which box is focused" is the question every text-input bug starts from and
                            // is otherwise unanswerable from a layout dump. The placeholder rides along so
                            // an empty field is still identifiable -- with no text and no label of its own,
                            // it would be an anonymous rect.
                            w.WriteString("textInput", field.State.Text);
                            w.WriteBoolean("focused", field.State.IsActive);
                            if (field.State.Placeholder is { Length: > 0 } placeholder)
                            {
                                w.WriteString("placeholder", placeholder);
                            }
                            break;
                        case Layout.Content.Fill fill when fill.Key is { } key:
                            w.WriteString("fillKey", key);
                            break;
                    }
                }

                if (node.Background is { } bg)
                {
                    w.WriteString("bg", $"#{bg.Red:X2}{bg.Green:X2}{bg.Blue:X2}{bg.Alpha:X2}");
                }

                if (node.Hit is { } hit)
                {
                    var (role, label) = RoleLabel(hit);
                    w.WriteString("hitRole", role);
                    if (label is not null) w.WriteString("hitLabel", label);
                }

                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static string LayoutKind(Layout.Node node) => node switch
    {
        Layout.Node.Stack => "Stack",
        Layout.Node.Dock => "Dock",
        Layout.Node.Grid => "Grid",
        Layout.Node.Overlay => "Overlay",
        Layout.Node.Split => "Split",
        Layout.Node.Leaf => "Leaf",
        _ => node.GetType().Name,
    };

    private static string EncodeScreenshot(byte[] rgba, int width, int height)
    {
        // RGBA of a UI is mostly flat color, so gzip shrinks the wire payload ~10-50x before base64.
        var gz = Gzip(rgba);
        var b64 = Convert.ToBase64String(gz);
        return ToJson(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteString("format", "rgba+gzip"); // raw RGBA, gzip-compressed; the bridge encodes the PNG
            w.WriteString("base64", b64);
            w.WriteEndObject();
        });
    }

    private string ExecuteListSignals() => ToJson(w =>
    {
        w.WriteStartArray();
        if (_opts.SignalFactories is not null)
            foreach (var name in _opts.SignalFactories.Keys)
                w.WriteStringValue(name);
        w.WriteEndArray();
    });

    // Applies an SDL window-state op on the render thread, then requests a redraw. Harmless for
    // minimize (the loop idles it anyway); for maximize/restore the redraw repaints the new size.
    private string ExecuteWindowState(Action<SdlVulkanWindow> op)
    {
        op(_view.Window);
        _view.RequestRedraw();
        return "\"ok\"";
    }

    /// <summary>
    /// A click, or a double/triple click.
    ///
    /// <para>The whole run is delivered, not just its last press: SDL reports a double click as TWO
    /// button-down events, the first with a count of 1 and the second with 2, and an app is entitled to
    /// act on both — a control that opens a menu on the single click and an editor on the double sees
    /// the menu open first, exactly as it would under a real pointer. Sending only the count-2 press
    /// would drive a path no mouse can produce, which is worse than not testing it.</para>
    /// </summary>
    private string ExecuteClickAt(float x, float y, InputModifier mods = InputModifier.None, int clicks = 1)
    {
        _view.DispatchPointerMove(x, y); // update cached pointer position (some consumers read it on MouseUp)
        for (var n = 1; n <= clicks; n++)
        {
            _view.DispatchPointerDown(1, x, y, (byte)n, mods);
            _view.DispatchPointerUp(1, x, y);
        }
        _view.RequestRedraw();
        return "\"ok\"";
    }

    private string ExecuteClickLabel(string label, int clicks = 1)
    {
        var regions = _opts.GetRegions?.Invoke() ?? [];
        // Walk in reverse so the topmost (last-registered) match wins, mirroring HitTest.
        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var r = regions[i];
            if (r.Result is HitResult.ButtonHit b && b.Action == label)
                return ExecuteClickAt(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f, InputModifier.None, clicks);
        }
        throw new ArgumentException($"no button region with label: {label}");
    }

    private string ExecuteKey(InputKey key, InputModifier mods)
    {
        _view.OnKeyDown?.Invoke(key, mods);
        _view.RequestRedraw();
        return "\"ok\"";
    }

    private string ExecuteText(string text)
    {
        _view.OnTextInput?.Invoke(text);
        _view.RequestRedraw();
        return "\"ok\"";
    }

    /// <summary>
    /// A wheel tick, optionally with a modifier held. The modifier is not decoration: a wheel gesture
    /// commonly means something else entirely with one down -- Ctrl zooms, Shift scrolls sideways --
    /// and an app that reads them off the global keyboard state instead of off the event cannot be
    /// driven into those readings at all, because nothing but a real key press moves that state.
    /// </summary>
    private string ExecuteScroll(float x, float y, float scrollY, InputModifier mods = InputModifier.None)
    {
        _view.DispatchPointerMove(x, y); // position the pointer first -- wheel handlers zoom around it
        _view.DispatchPointerWheel(scrollY, x, y, mods);
        _view.RequestRedraw();
        return "\"ok\"";
    }

    private string ExecuteDrag(float x1, float y1, float x2, float y2, InputModifier mods, int steps)
    {
        // Same path as a real drag: move-to-start, button-down, interpolated motion, button-up.
        // Pan handlers that integrate per motion event (e.g. the sky map's unproject-based pan)
        // need the intermediate steps -- a single jump start->end would under-pan or misbehave.
        _view.DispatchPointerMove(x1, y1);
        _view.DispatchPointerDown(1, x1, y1, 1, mods);
        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            _view.DispatchPointerMove(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t);
        }
        _view.DispatchPointerUp(1, x2, y2);
        _view.RequestRedraw();
        return "\"ok\"";
    }

    private string ExecuteFrameStats() => ToJson(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("avgFrameMs", _loop.DebugFrameAvgMs);
        w.WriteNumber("slowFrameFloorMs", SdlEventLoop.DebugSlowFrameFloorMs);
        w.WriteEndObject();
    });

    private static string ExecuteValidationReport() => ToJson(w =>
    {
        var snap = VulkanValidation.Snapshot();
        w.WriteStartObject();
        // Report whether the layer is actually installed, not just whether the gate is on. "enabled"
        // alone is a trap: with SDLVK_VALIDATION=1 on a host that has no Khronos validation layer
        // (no Vulkan SDK), nothing validates, yet the counts below still read 0 and look like a clean
        // bill of health. That misreading turned "no hazards found" into a false all-clear during a
        // real device-loss investigation. "active" is the only field that means a zero count is
        // evidence of anything.
        var layerAvailable = VulkanValidation.LayerAvailable();
        w.WriteBoolean("enabled", VulkanValidation.Enabled);
        w.WriteBoolean("layerAvailable", layerAvailable);
        w.WriteBoolean("active", VulkanValidation.Enabled && layerAvailable);
        w.WriteBoolean("syncValidation", VulkanValidation.SyncEnabled);
        w.WriteNumber("totalMessages", snap.TotalMessages);
        w.WriteNumber("syncHazards", snap.SyncHazards);
        w.WriteStartArray("recent");
        foreach (var m in snap.Recent)
            w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private string ExecutePostSignal(string name, JsonElement args)
    {
        if (_opts.SignalFactories is null)
            throw new ArgumentException("this instance exposes no signals (SignalFactories is null)");
        if (!_opts.SignalFactories.TryGetValue(name, out var post))
            // List the valid names so a misspelled/unknown signal is self-correcting instead of a dead end.
            throw new ArgumentException($"unknown signal: '{name}'. Known signals: {string.Join(", ", _opts.SignalFactories.Keys)}");
        post(args);
        _view.RequestRedraw();
        return "\"queued\"";
    }

    // ---------------- helpers ----------------

    private static string? SafeInvoke(Func<string?>? f)
    {
        if (f is null) return null;
        try { return f(); } catch { return null; }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static string ToJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
            write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
#endif
