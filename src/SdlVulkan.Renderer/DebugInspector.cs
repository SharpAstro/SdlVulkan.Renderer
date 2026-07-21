#if DEBUG
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DIR.Lib;
using Layout = DIR.Lib.Layout;

namespace SdlVulkan.Renderer;

/// <summary>
/// DEBUG-only live UI debug inspector. Hosts a TCP command server (ephemeral port) plus a UDP
/// multicast discovery responder so a sidecar (the published SdlVulkan.Renderer.Inspector MCP
/// server) can discover this running app and drive it: read the clickable region tree, capture a
/// screenshot, inject input, and post named signals.
/// <para>
/// Threading: the socket/UDP servers run on background tasks and only ENQUEUE commands. Every
/// command executes on the RENDER THREAD inside <see cref="DrainCommands"/>, which is chained onto
/// <see cref="SdlEventLoop.OnLoopIteration"/> by <see cref="Attach(SdlEventLoop, SdlWindowView, DebugInspectorOptions)"/>.
/// That per-iteration hook fires EVERY loop iteration -- including while a window is minimized and
/// nothing renders -- so commands keep draining on a minimized window (a per-frame hook would not).
/// Enqueueing still calls <see cref="SdlWindowView.RequestRedraw"/> to wake the loop promptly
/// (latency bounded by the loop's ~16ms wait).
/// </para>
/// The entire type is compiled only in DEBUG builds, so no release artifact carries it.
/// </summary>
public sealed class DebugInspector : IDisposable
{
    private const int ProtocolVersion = 1;

    // --- Commands: enqueued by the socket thread, executed on the render thread. Each carries a
    // TaskCompletionSource whose result is the JSON value fragment for the "result" field. ---
    private abstract record InspectorCommand
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How long the socket side waits for this command to drain on the render
        /// thread before giving up. A single command drains within a frame or two; a batch
        /// runs one step per rendered frame, so it overrides this to scale with its length.</summary>
        public virtual TimeSpan Timeout => TimeSpan.FromSeconds(10);
    }
    private sealed record PingCommand : InspectorCommand;
    private sealed record DescribeCommand : InspectorCommand;
    private sealed record DescribeLayoutCommand : InspectorCommand;
    private sealed record ScreenshotCommand : InspectorCommand;
    private sealed record ListSignalsCommand : InspectorCommand;
    /// <summary>Window state changes (iconify / maximize / restore). These call SDL window ops, which
    /// must run on the render thread -- which the pump guarantees. They keep working while minimized
    /// because the pump drains every loop iteration (see <see cref="SdlEventLoop.OnLoopIteration"/>),
    /// so <c>restore</c> can un-minimize a window the loop is idling.</summary>
    private sealed record MinimizeCommand : InspectorCommand;
    private sealed record MaximizeCommand : InspectorCommand;
    private sealed record RestoreCommand : InspectorCommand;
    private sealed record ClickCommand(float X, float Y, InputModifier Mods = InputModifier.None) : InspectorCommand;
    private sealed record ClickLabelCommand(string Label) : InspectorCommand;
    private sealed record KeyCommand(InputKey Key, InputModifier Mods) : InspectorCommand;
    private sealed record TextCommand(string Text) : InspectorCommand;
    private sealed record PostSignalCommand(string Name, JsonElement Args) : InspectorCommand;
    /// <summary>Synthesize a mouse-wheel scroll at (X, Y). ScrollY &gt; 0 is wheel-up (zoom in in most views).</summary>
    private sealed record ScrollCommand(float X, float Y, float ScrollY) : InspectorCommand;
    /// <summary>Synthesize a press-drag-release from (X1,Y1) to (X2,Y2) with <see cref="Steps"/> interpolated
    /// motion events between, so integrate-per-move pan handlers see a smooth path rather than a teleport.</summary>
    private sealed record DragCommand(float X1, float Y1, float X2, float Y2, InputModifier Mods, int Steps) : InspectorCommand;
    /// <summary>Synthesize a left-button PRESS-AND-HOLD at (X,Y): button down, held for <see cref="DurationMs"/>
    /// ms while the loop keeps pumping (so a long-press / hold-to-repeat / charge-up timer ticks THROUGH the
    /// hold), then button up. Advanced by DrainCommands one non-blocking step per loop iteration, like a batch --
    /// it never sleeps the render thread.</summary>
    private sealed record HoldCommand(float X, float Y, InputModifier Mods, int DurationMs) : InspectorCommand
    {
        // The socket waits out the hold plus slack; the render loop stays responsive throughout.
        public override TimeSpan Timeout => TimeSpan.FromSeconds(Math.Min(300, DurationMs / 1000.0 + 15));
    }
    /// <summary>Read back the loop's rolling average frame time (the same EWMA that drives the
    /// [rdiag] frame.slow log), so jank can be measured numerically instead of by eye.</summary>
    private sealed record FrameStatsCommand : InspectorCommand;
    /// <summary>Reads the Vulkan validation-layer report (enabled flags, message/hazard counts, recent
    /// messages). Pure read of process-wide state, but runs on the render thread via the pump like
    /// every other command.</summary>
    private sealed record ValidationReportCommand : InspectorCommand;
    /// <summary>Idle for <see cref="Frames"/> rendered frames (e.g. to let async work settle).
    /// Only meaningful as a step inside a <see cref="BatchCommand"/>.</summary>
    private sealed record WaitCommand(int Frames) : InspectorCommand;
    /// <summary>A sequence of commands executed one-per-rendered-frame, so a real frame renders
    /// between each step (a zoom/pan takes effect before the next step reads state). Returns a
    /// JSON array of the per-step result fragments.</summary>
    private sealed record BatchCommand(IReadOnlyList<InspectorCommand> Steps) : InspectorCommand
    {
        // ~1 frame per step plus the wait frames; pad generously and cap. This is just the
        // socket's patience -- the event loop stays responsive throughout regardless.
        public override TimeSpan Timeout => TimeSpan.FromSeconds(Math.Min(300,
            15 + Steps.Count + Steps.OfType<WaitCommand>().Sum(w => w.Frames) / 30.0));
    }

    // In-progress batch, advanced one step per frame by DrainCommands (null when idle).
    private BatchState? _activeBatch;
    private sealed class BatchState(IReadOnlyList<InspectorCommand> steps, TaskCompletionSource<string> result)
    {
        public readonly IReadOnlyList<InspectorCommand> Steps = steps;
        public readonly TaskCompletionSource<string> Result = result;
        public readonly List<string> Results = [];
        public int Index;
        public int WaitFrames;
    }

    // In-progress press-and-hold, advanced one non-blocking step per loop iteration by DrainCommands
    // (null when idle). Mirrors _activeBatch: the button stays held across frames so the app ticks THROUGH
    // the hold (a long-press timer advances) instead of the render thread blocking for the duration.
    private HoldState? _activeHold;
    private sealed class HoldState(TaskCompletionSource<string> result, long startTick, long durationMs, float x, float y)
    {
        public readonly TaskCompletionSource<string> Result = result;
        public readonly long StartTick = startTick;
        public readonly long DurationMs = durationMs;
        // Press position, so the deferred release can carry real coordinates (position-dependent
        // release logic -- tap-vs-drag slop, tap-to-select -- reads them off the MouseUp event).
        public readonly float X = x;
        public readonly float Y = y;
    }

    private readonly DebugInspectorOptions _opts;
    private readonly SdlWindowView _view;
    private readonly SdlEventLoop _loop; // for frame-timing readback (frameStats)
    private readonly ConcurrentQueue<InspectorCommand> _queue = new();
    private readonly TcpListener _listener;
    private readonly string _startedAtUtc;
    private readonly int _tcpPort;

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _discoveryTask;

    private DebugInspector(SdlEventLoop loop, SdlWindowView view, DebugInspectorOptions opts)
    {
        _opts = opts;
        _view = view;
        _loop = loop;
        _startedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        _listener = new TcpListener(opts.BindAddress, opts.Port);
        _listener.Start();
        _tcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Attaches the inspector to the given loop + window view and starts the background servers.
    /// Chains the command pump onto the loop's <see cref="SdlEventLoop.OnLoopIteration"/>. Call once,
    /// under <c>#if DEBUG</c>, after the loop's callbacks are wired but before <see cref="SdlEventLoop.Run"/>.
    /// The returned <see cref="IDisposable"/> stops the servers when disposed.
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

        inspector.Start();

        // Wire the pump onto the loop's per-iteration hook (OnLoopIteration), NOT OnPostFrame: the
        // drain must run every iteration -- including when nothing rendered because the window is
        // minimized -- so commands (notably `restore`) are still serviced on a minimized window. The
        // hook fires inside Run on the render thread, so all Vulkan/widget/input access stays
        // render-thread-safe. Lambda-compose (the framework's wiring style) so any prior hook runs first.
        var prev = loop.OnLoopIteration;
        loop.OnLoopIteration = () =>
        {
            prev?.Invoke();
            inspector.DrainCommands();
        };
        return inspector;
    }

    /// <summary>Single-window convenience overload that uses the loop's primary view.</summary>
    public static DebugInspector Attach(SdlEventLoop loop, DebugInspectorOptions opts)
        => Attach(loop, loop.DebugPrimaryView
            ?? throw new InvalidOperationException("DebugInspector.Attach(loop, opts) requires the single-window SdlEventLoop constructor; pass an explicit SdlWindowView otherwise."),
            opts);

    private void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var appName = string.IsNullOrEmpty(_opts.AppName)
            ? System.Diagnostics.Process.GetCurrentProcess().ProcessName
            : _opts.AppName;
        Console.Error.WriteLine($"[inspector] '{appName}' command server on {_opts.BindAddress}:{_tcpPort}" +
            (_opts.EnableDiscovery ? $", discovery on {_opts.DiscoveryGroup}:{_opts.DiscoveryPort}" : " (discovery off)"));

        _acceptTask = Task.Run(() => AcceptLoopAsync(ct), ct);
        if (_opts.EnableDiscovery)
            _discoveryTask = Task.Run(() => DiscoveryLoopAsync(ct), ct);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* best-effort */ }
        try { _listener.Stop(); } catch { /* best-effort */ }
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        try { _discoveryTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _cts?.Dispose();
    }

    // ---------------- TCP command server (background thread) ----------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    var response = await DispatchRequestAsync(line, ct);
                    await writer.WriteLineAsync(response.AsMemory(), ct);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* client dropped */ }
        catch (Exception ex) { Console.Error.WriteLine($"[inspector] client error: {ex.Message}"); }
    }

    // Request:  {"id":1,"method":"describe","params":{...}}
    // Response: {"id":1,"result":<value>} or {"id":1,"error":"<msg>"}
    private async Task<string> DispatchRequestAsync(string requestJson, CancellationToken ct)
    {
        var id = 0;
        try
        {
            InspectorCommand cmd;
            using (var doc = JsonDocument.Parse(requestJson))
            {
                var root = doc.RootElement;
                id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                var method = root.GetProperty("method").GetString() ?? "";
                root.TryGetProperty("params", out var p);
                cmd = BuildCommand(method, p);
            }

            _queue.Enqueue(cmd);
            _view.RequestRedraw(); // wake the loop promptly so the per-iteration pump drains this command

            var fragment = await cmd.Result.Task.WaitAsync(cmd.Timeout, ct);
            return $"{{\"id\":{id},\"result\":{fragment}}}";
        }
        catch (Exception ex)
        {
            return ToJson(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteString("error", ex.Message);
                w.WriteEndObject();
            });
        }
    }

    private static InspectorCommand BuildCommand(string method, JsonElement p) => method switch
    {
        "ping" => new PingCommand(),
        "describe" => new DescribeCommand(),
        "describeLayout" => new DescribeLayoutCommand(),
        "screenshot" => new ScreenshotCommand(),
        "signals" => new ListSignalsCommand(),
        "minimize" => new MinimizeCommand(),
        "maximize" => new MaximizeCommand(),
        "restore" => new RestoreCommand(),
        "validationReport" => new ValidationReportCommand(),
        "click" => new ClickCommand(
            p.GetProperty("x").GetSingle(),
            p.GetProperty("y").GetSingle(),
            ResolveModifier(p.TryGetProperty("mods", out var cm) && cm.ValueKind == JsonValueKind.String ? cm.GetString() : null)),
        "clickLabel" => new ClickLabelCommand(p.GetProperty("label").GetString() ?? ""),
        "key" => new KeyCommand(
            ResolveInputKey(p.GetProperty("key").GetString() ?? ""),
            ResolveModifier(p.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)),
        "text" => new TextCommand(p.GetProperty("s").GetString() ?? ""),
        "scroll" => new ScrollCommand(
            p.GetProperty("x").GetSingle(),
            p.GetProperty("y").GetSingle(),
            p.GetProperty("scrollY").GetSingle()),
        "drag" => new DragCommand(
            p.GetProperty("x1").GetSingle(),
            p.GetProperty("y1").GetSingle(),
            p.GetProperty("x2").GetSingle(),
            p.GetProperty("y2").GetSingle(),
            ResolveModifier(p.TryGetProperty("mods", out var dm) && dm.ValueKind == JsonValueKind.String ? dm.GetString() : null),
            p.TryGetProperty("steps", out var ds) && ds.ValueKind == JsonValueKind.Number ? Math.Clamp(ds.GetInt32(), 1, 64) : 8),
        "pressHold" => new HoldCommand(
            p.GetProperty("x").GetSingle(),
            p.GetProperty("y").GetSingle(),
            ResolveModifier(p.TryGetProperty("mods", out var hm) && hm.ValueKind == JsonValueKind.String ? hm.GetString() : null),
            (int)Math.Clamp((p.TryGetProperty("seconds", out var hs) && hs.ValueKind == JsonValueKind.Number ? hs.GetDouble() : 1.0) * 1000.0, 50, 300000)),
        "frameStats" => new FrameStatsCommand(),
        "postSignal" => new PostSignalCommand(
            p.GetProperty("name").GetString() ?? "",
            p.TryGetProperty("args", out var a) ? a.Clone() : default),
        "wait" => new WaitCommand(p.TryGetProperty("frames", out var wf) && wf.ValueKind == JsonValueKind.Number
            ? Math.Clamp(wf.GetInt32(), 1, 600) : 1),
        "batch" => BuildBatch(p),
        _ => throw new ArgumentException($"unknown method: {method}")
    };

    // Parses {steps:[{method,params}, ...]} into a BatchCommand. Each step is built through the
    // same BuildCommand path as a standalone request; nesting a batch inside a batch is rejected
    // (the one-step-per-frame pump only tracks a single active batch).
    private static BatchCommand BuildBatch(JsonElement p)
    {
        if (!p.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("batch requires a 'steps' array of {method, params}");
        var list = new List<InspectorCommand>(steps.GetArrayLength());
        foreach (var step in steps.EnumerateArray())
        {
            var m = step.GetProperty("method").GetString() ?? "";
            if (m == "batch")
                throw new ArgumentException("nested batch is not supported");
            step.TryGetProperty("params", out var sp);
            list.Add(BuildCommand(m, sp));
        }
        if (list.Count == 0)
            throw new ArgumentException("batch 'steps' must be non-empty");
        return new BatchCommand(list);
    }

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
    private static InputKey ResolveInputKey(string raw)
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
    private static InputModifier ResolveModifier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return InputModifier.None;
        var s = raw.ToLowerInvariant();
        if (s is "none" or "0") return InputModifier.None;

        var mod = InputModifier.None;
        if (s.Contains("ctrl") || s.Contains("control")) mod |= InputModifier.Ctrl;
        if (s.Contains("shift")) mod |= InputModifier.Shift;
        if (s.Contains("alt") || s.Contains("option")) mod |= InputModifier.Alt;
        return mod;
    }

    // ---------------- UDP multicast discovery responder (background thread) ----------------

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { ExclusiveAddressUse = false };
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _opts.DiscoveryPort));
            udp.JoinMulticastGroup(_opts.DiscoveryGroup);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[inspector] discovery disabled (bind failed): {ex.Message}");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult recv;
            try { recv = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }

            if (!IsDiscoveryQuery(recv.Buffer)) continue;

            var descriptor = BuildDescriptor();
            var bytes = Encoding.UTF8.GetBytes(descriptor);
            try { await udp.SendAsync(bytes, bytes.Length, recv.RemoteEndPoint); }
            catch (SocketException) { /* reply best-effort */ }
        }
    }

    private static bool IsDiscoveryQuery(byte[] buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer);
            return doc.RootElement.TryGetProperty("q", out var q)
                && q.ValueKind == JsonValueKind.String
                && q.GetString() == "sdlvk-inspect";
        }
        catch { return false; }
    }

    private string BuildDescriptor() => ToJson(w =>
    {
        w.WriteStartObject();
        w.WriteString("app", string.IsNullOrEmpty(_opts.AppName)
            ? System.Diagnostics.Process.GetCurrentProcess().ProcessName
            : _opts.AppName);
        var title = SafeInvoke(_opts.WindowTitle);
        if (title is null) w.WriteNull("title"); else w.WriteString("title", title);
        w.WriteNumber("tcpPort", _tcpPort);
        w.WriteNumber("pid", Environment.ProcessId);
        w.WriteNumber("proto", ProtocolVersion);
        w.WriteString("startedAt", _startedAtUtc);
        w.WriteEndObject();
    });

    // ---------------- Render-thread command pump ----------------

    /// <summary>
    /// Drains and executes queued commands on the render thread. Wired into the loop's OnLoopIteration
    /// so all Vulkan/widget/input access is on the render thread. Never throws (per-command failures
    /// are routed to the command's TaskCompletionSource as an error response).
    /// </summary>
    private void DrainCommands()
    {
        // A batch owns its frames: advance one step (or burn one wait-frame) and return, so a
        // real frame renders before the next step. New single commands queued meanwhile drain
        // once the batch completes.
        if (_activeBatch is not null)
        {
            AdvanceBatch();
            return;
        }
        // A press-and-hold advances every iteration but, UNLIKE a batch, does not own the queue: it
        // releases the button once its duration elapses, then we fall through to drain other commands
        // below. So observe commands (ping/describe/screenshot) sent mid-hold are serviced WHILE the
        // button stays held -- the point of a hold test is to inspect the hold-triggered UI in progress.
        if (_activeHold is not null)
        {
            AdvanceHold();
        }

        while (_queue.TryDequeue(out var cmd))
        {
            if (cmd is BatchCommand b)
            {
                if (_activeHold is not null)
                {
                    cmd.Result.TrySetException(new InvalidOperationException("cannot start a batch while a press-and-hold is in progress"));
                    continue;
                }
                _activeBatch = new BatchState(b.Steps, b.Result);
                AdvanceBatch();
                return;
            }
            if (cmd is HoldCommand h)
            {
                if (_activeHold is not null)
                {
                    cmd.Result.TrySetException(new InvalidOperationException("a press-and-hold is already in progress"));
                    continue;
                }
                StartHold(h); // hold now active; keep draining simple commands queued behind it this iteration
                continue;
            }
            try { cmd.Result.TrySetResult(ExecuteCommand(cmd)); }
            catch (Exception ex) { cmd.Result.TrySetException(ex); }
        }
    }

    /// <summary>
    /// Begins a press-and-hold: press the button now and arm <see cref="_activeHold"/>, which
    /// <see cref="AdvanceHold"/> releases once the duration elapses. Non-blocking -- the loop keeps
    /// rendering during the hold.
    /// </summary>
    private void StartHold(HoldCommand h)
    {
        _view.DispatchPointerMove(h.X, h.Y); // update cached pointer position (consumers may read it on MouseUp)
        _view.DispatchPointerDown(1, h.X, h.Y, 1, h.Mods);
        _activeHold = new HoldState(h.Result, Environment.TickCount64, h.DurationMs, h.X, h.Y);
        _view.RequestRedraw();
    }

    /// <summary>
    /// Releases the active press-and-hold once its duration has elapsed (monotonic
    /// <see cref="Environment.TickCount64"/>); until then it just keeps the loop awake so the app renders
    /// frames while the button is held. One non-blocking step per call, like <see cref="AdvanceBatch"/>.
    /// </summary>
    private void AdvanceHold()
    {
        var h = _activeHold!;
        if (Environment.TickCount64 - h.StartTick < h.DurationMs)
        {
            _view.RequestRedraw(); // still holding -- keep the loop pumping so the app ticks through the hold
            return;
        }
        _view.DispatchPointerUp(1, h.X, h.Y);
        _view.RequestRedraw();
        h.Result.TrySetResult(ToJson(w => w.WriteStringValue($"held {h.DurationMs}ms")));
        _activeHold = null;
    }

    /// <summary>
    /// Executes the next step of <see cref="_activeBatch"/> (one per call = one per rendered
    /// frame), or burns a pending wait-frame. Completes the batch's result with a JSON array of
    /// per-step fragments once all steps are done. A failing step records an error fragment and
    /// the batch continues -- one bad step doesn't abort the sequence.
    /// </summary>
    private void AdvanceBatch()
    {
        var b = _activeBatch!;
        if (b.WaitFrames > 0)
        {
            b.WaitFrames--;
            _view.RequestRedraw();
            return;
        }

        if (b.Index < b.Steps.Count)
        {
            var step = b.Steps[b.Index++];
            if (step is WaitCommand w)
            {
                b.WaitFrames = Math.Max(0, w.Frames - 1);
                b.Results.Add("\"waited\"");
            }
            else
            {
                try { b.Results.Add(ExecuteCommand(step)); }
                // Encode the error fragment via Utf8JsonWriter (ToJson) like every other result,
                // not JsonSerializer.Serialize -- the reflection-based serializer trips IL2026/IL3050.
                catch (Exception ex) { b.Results.Add(ToJson(w => w.WriteStringValue($"error: {ex.Message}"))); }
            }
        }

        if (b.Index >= b.Steps.Count && b.WaitFrames == 0)
        {
            b.Result.TrySetResult("[" + string.Join(",", b.Results) + "]");
            _activeBatch = null;
            return;
        }
        _view.RequestRedraw(); // more steps / wait-frames remain -- keep the loop awake
    }

    private string ExecuteCommand(InspectorCommand cmd) => cmd switch
    {
        PingCommand => "\"pong\"",
        DescribeCommand => ExecuteDescribe(),
        DescribeLayoutCommand => ExecuteDescribeLayout(),
        ScreenshotCommand => ExecuteScreenshot(),
        ListSignalsCommand => ExecuteListSignals(),
        MinimizeCommand => ExecuteWindowState(static w => w.Minimize()),
        MaximizeCommand => ExecuteWindowState(static w => w.Maximize()),
        RestoreCommand => ExecuteWindowState(static w => w.Restore()),
        ClickCommand c => ExecuteClickAt(c.X, c.Y, c.Mods),
        ClickLabelCommand c => ExecuteClickLabel(c.Label),
        KeyCommand c => ExecuteKey(c.Key, c.Mods),
        TextCommand c => ExecuteText(c.Text),
        ScrollCommand c => ExecuteScroll(c.X, c.Y, c.ScrollY),
        DragCommand c => ExecuteDrag(c.X1, c.Y1, c.X2, c.Y2, c.Mods, c.Steps),
        FrameStatsCommand => ExecuteFrameStats(),
        ValidationReportCommand => ExecuteValidationReport(),
        PostSignalCommand c => ExecutePostSignal(c.Name, c.Args),
        WaitCommand => "\"waited\"", // only reached if used outside a batch; harmless no-op
        _ => throw new ArgumentException($"unknown command: {cmd.GetType().Name}")
    };

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

    private string ExecuteScreenshot()
    {
        var ctx = _view.Renderer.Context;
        var rgba = ctx.ReadbackSwapchainRgba();
        if (rgba is null)
        {
            // Readback was skipped/aborted (GPU wedged or the bounded readback wait timed out).
            // Surface a structured error instead of crashing or blocking — the inspector caller
            // can retry once the GPU recovers.
            return ToJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("error", "screenshot unavailable: GPU stalled or readback timed out");
                w.WriteEndObject();
            });
        }
        var width = (int)ctx.SwapchainWidth;
        var height = (int)ctx.SwapchainHeight;
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

    private string ExecuteClickAt(float x, float y, InputModifier mods = InputModifier.None)
    {
        _view.DispatchPointerMove(x, y); // update cached pointer position (some consumers read it on MouseUp)
        _view.DispatchPointerDown(1, x, y, 1, mods);
        _view.DispatchPointerUp(1, x, y);
        _view.RequestRedraw();
        return "\"ok\"";
    }

    private string ExecuteClickLabel(string label)
    {
        var regions = _opts.GetRegions?.Invoke() ?? [];
        // Walk in reverse so the topmost (last-registered) match wins, mirroring HitTest.
        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var r = regions[i];
            if (r.Result is HitResult.ButtonHit b && b.Action == label)
                return ExecuteClickAt(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);
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

    private string ExecuteScroll(float x, float y, float scrollY)
    {
        _view.DispatchPointerMove(x, y); // position the pointer first -- wheel handlers zoom around it
        _view.DispatchPointerWheel(scrollY, x, y, InputModifier.None);
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
        w.WriteBoolean("enabled", VulkanValidation.Enabled);
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
