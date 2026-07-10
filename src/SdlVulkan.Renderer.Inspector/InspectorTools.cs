using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SharpAstro.Png;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>
/// MCP tools that bridge an AI agent to a running Debug-build SDL3+Vulkan app. Discovery resolves
/// which instance to talk to; action tools take an optional <c>instance</c> pid (defaulting to the
/// sole running instance, erroring if ambiguous).
/// </summary>
[McpServerToolType]
public sealed class InspectorTools
{
    [McpServerTool, Description("Discover running Debug-build SDL/Vulkan app instances on the local machine and LAN. Returns one line per instance: pid, app, title, address:port.")]
    public static async Task<string> list_instances(InspectorDiscoveryClient discovery, CancellationToken ct = default)
    {
        var all = await discovery.DiscoverAsync(ct);
        if (all.Count == 0) return "No debuggable instances found. Is a Debug-build app running with DebugInspector.Attach?";
        var sb = new StringBuilder();
        foreach (var i in all)
            sb.AppendLine($"pid={i.Pid}  app={i.App}  title={i.Title ?? "<none>"}  {i.Address}:{i.TcpPort}  proto={i.Proto}");
        return sb.ToString();
    }

    [McpServerTool, Description("Ping an instance to confirm the inspector is alive. Returns 'pong'.")]
    public static async Task<string> ping(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "ping", null, ct);
        return result.GetString() ?? "pong";
    }

    [McpServerTool, Description("Returns the live clickable-region tree (each region's bounds + role + label) plus the app's optional state JSON. The 'label' of a button is the action string used by click_label.")]
    public static async Task<string> describe_ui(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "describe", null, ct);
        return result.GetRawText();
    }

    [McpServerTool, Description(
        "Returns the live ARRANGED LAYOUT tree: the FULL DIR.Lib.Layout node tree the app painted this "
        + "frame, not just the clickable subset describe_ui shows. Each node has depth (root=0, pre-order "
        + "so a parent precedes its children), kind (Stack/Dock/Grid/Overlay/Split/Leaf), rect (x/y/w/h), "
        + "axis (Stack/Split), columns (Grid), text+fontSize (Text leaves), fillKey (custom-widget Fill "
        + "leaves), bg (#RRGGBBAA), and hitRole/hitLabel when the node is clickable. Use this to debug "
        + "layout/placement -- clipping, gaps, why a panel is the size it is, nesting -- which describe_ui "
        + "cannot show. Empty if the app draws without the layout DSL.")]
    public static async Task<string> describe_layout(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "describeLayout", null, ct);
        return result.GetRawText();
    }

    [McpServerTool, Description("Capture a PNG screenshot of the instance's current window frame.")]
    public static async Task<ImageContentBlock> screenshot(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "screenshot", null, ct);
        var width = result.GetProperty("width").GetInt32();
        var height = result.GetProperty("height").GetInt32();
        var format = result.GetProperty("format").GetString();
        var payload = Convert.FromBase64String(result.GetProperty("base64").GetString() ?? "");
        var rgba = format == "rgba+gzip" ? Gunzip(payload) : payload;
        var png = PngWriter.Encode(rgba, width, height);
        // ImageContentBlock.Data holds the base64-encoded UTF-8 bytes that go on the wire verbatim
        // as the `data` string (DecodedData, the raw-bytes view, is get-only). So encode the PNG to
        // base64 text ourselves and store its UTF-8 bytes; assigning raw PNG bytes here makes the SDK
        // emit them as the `data` string directly, which fails the client's base64 validation.
        var base64 = Convert.ToBase64String(png);
        return new ImageContentBlock { Data = Encoding.UTF8.GetBytes(base64), MimeType = "image/png" };
    }

    [McpServerTool, Description("Synthesize a left mouse click at pixel coordinates (routes through the same input path as a real SDL click). Pass mods to hold a keyboard modifier during the click, e.g. Ctrl for a Ctrl+click.")]
    public static async Task<string> click(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("X pixel coordinate.")] float x,
        [Description("Y pixel coordinate.")] float y,
        [Description("InputModifier name held during the click (None, Ctrl, Shift, Alt, or combos like CtrlShift). Default None.")] string mods = "None",
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "click", new { x, y, mods }, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Click the button whose label (ButtonHit action, e.g. 'Tab:Planner') matches. Use describe_ui to see labels.")]
    public static async Task<string> click_label(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("The button label / action string to click.")] string label,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "clickLabel", new { label }, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Inject a key press through the SAME path as a real SDL keypress, so it reaches a focused text field / search box (e.g. Enter commits an open search). Key is a DIR.Lib InputKey name (see the key param). Mods is None/Ctrl/Shift/Alt or a combo like CtrlShift / 'Ctrl+Alt'.")]
    public static async Task<string> press_key(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("InputKey name: Enter, Escape, Tab, Space, Backspace, Delete, Up/Down/Left/Right, Home/End, F1-F12, A-Z, D0-D9, Plus/Minus/Period/Comma/etc. Aliases accepted: Return=Enter, Esc=Escape, ArrowUp/Down/Left/Right, Spacebar=Space, 0-9=D0-D9.")] string key,
        [Description("Modifier(s) held: None, Ctrl, Shift, Alt, or a combo like CtrlShift / 'Ctrl+Alt'. Default None.")] string mods = "None",
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "key", new { key, mods }, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Inject a text-input string (as if typed). Goes to the focused text field, if any.")]
    public static async Task<string> type_text(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Text to inject.")] string text,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "text", new { s = text }, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Synthesize a mouse-wheel scroll at pixel (x, y), routed through the same path as a real SDL wheel event. scrollY > 0 is wheel-up (zoom IN in most views, e.g. the sky map / FITS viewer); negative zooms out. The view zooms around (x, y). Magnitude ~1 per notch.")]
    public static async Task<string> scroll(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("X pixel coordinate (the view zooms around this point).")] float x,
        [Description("Y pixel coordinate.")] float y,
        [Description("Wheel delta: positive = up / zoom-in, negative = down / zoom-out. ~1 per notch.")] float scrollY,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "scroll", new { x, y, scrollY }, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Synthesize a left-button press-drag-release from (x1,y1) to (x2,y2) -- e.g. to pan the sky map or FITS viewer. Emits interpolated motion events between the endpoints so integrate-per-move pan handlers see a smooth path, not a teleport. Pass mods to hold a modifier during the drag.")]
    public static async Task<string> drag(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Start X pixel.")] float x1,
        [Description("Start Y pixel.")] float y1,
        [Description("End X pixel.")] float x2,
        [Description("End Y pixel.")] float y2,
        [Description("InputModifier held during the drag (None, Ctrl, Shift, Alt, or combos like CtrlShift). Default None.")] string mods = "None",
        [Description("Interpolated motion events between start and end (1-64). Default 8.")] int steps = 8,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "drag", new { x1, y1, x2, y2, mods, steps }, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Minimize (iconify) the instance's window. While minimized the app idles its render loop (~0% CPU/GPU, no frames), so this is the way to verify idle-on-minimize behaviour unattended. Other inspector commands (ping, describe, restore) still work while minimized; only screenshot is unavailable until restored (there is no swapchain to read). Use restore to bring it back.")]
    public static async Task<string> minimize(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "minimize", null, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Maximize the instance's window to fill the screen (work area).")]
    public static async Task<string> maximize(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "maximize", null, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Restore the instance's window to its floating (un-minimized / un-maximized) size and position. Un-minimizes a window that minimize iconified, and rendering resumes immediately.")]
    public static async Task<string> restore(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "restore", null, ct);
        return result.GetString() ?? "ok";
    }

    [McpServerTool, Description("Read the app's rolling-average frame time in milliseconds (the EWMA that drives the frame.slow diagnostic) plus the slow-frame floor. Measure jank numerically: sample this, drive a pan/zoom (ideally via batch so frames render between steps), sample again. Returns {avgFrameMs, slowFrameFloorMs}.")]
    public static async Task<string> frame_stats(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "frameStats", null, ct);
        return result.GetRawText();
    }

    [McpServerTool, Description(
        "Read the Vulkan validation-layer report: whether validation and synchronization validation are "
        + "enabled, running counts of total validation messages and SYNC-HAZARD messages, and the most "
        + "recent messages (capped). Use it to check for GPU correctness / synchronization hazards -- the "
        + "class of bug behind GPU wedges -- after driving the app (open a document, pan/zoom). syncHazards "
        + "> 0 means a read/write ordering hazard was recorded. Returns {enabled, syncValidation, "
        + "totalMessages, syncHazards, recent[]}. Everything is zero/false unless the app runs with the "
        + "validation layer installed AND enabled (a DEBUG build or SDLVK_VALIDATION=1; sync validation also "
        + "needs SDLVK_SYNC_VALIDATION=1).")]
    public static async Task<string> validation_report(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "validationReport", null, ct);
        return result.GetRawText();
    }

    [McpServerTool, Description(
        "Render-thread watchdog: is the app's RENDER THREAD pumping or wedged? The inspector executes "
        + "EVERY command (including ping) ON the render thread, so a ping that round-trips proves the "
        + "render loop completed a frame and drained its queue; a ping that does NOT return within the "
        + "short budget means the render thread is blocked (a hang / Not-Responding) even though the "
        + "process is still alive. Returns ALIVE (with round-trip ms) / BLOCKED / DEAD -- and on BLOCKED "
        + "the exact dotnet-stack command to capture the frozen frame. Set watchSeconds>0 to poll until "
        + "it wedges (or the window elapses still-alive) in a single call -- the lightweight watchdog. "
        + "Use this, not screenshot/describe, to decide IF the render thread is stuck (those also block "
        + "when it is).")]
    public static async Task<string> render_liveness(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Per-ping budget in milliseconds before declaring the render thread blocked (default 1500).")] int timeoutMs = 1500,
        [Description("If > 0, poll repeatedly for up to this many seconds, returning as soon as the render thread blocks (or the window elapses still-alive). 0 = a single probe.")] double watchSeconds = 0,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var budget = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 100, 10_000));

        if (watchSeconds <= 0)
            return FormatLiveness(await socket.ProbeRenderAsync(target, budget, ct), target);

        // Watch mode: poll until it wedges/dies or the window elapses. One round-trip per budget, so the
        // poll cadence naturally backs off to the budget length; the call returns the moment it flips.
        var end = DateTime.UtcNow.AddSeconds(Math.Clamp(watchSeconds, 1, 600));
        var probes = 0;
        while (DateTime.UtcNow < end)
        {
            var r = await socket.ProbeRenderAsync(target, budget, ct);
            probes++;
            if (r.Status != RenderLiveness.Alive)
                return $"{FormatLiveness(r, target)} (after {probes} probe(s))";
            await Task.Delay(budget, ct);
        }
        return $"ALIVE: render thread stayed responsive for ~{watchSeconds:F0}s ({probes} probes, pid {target.Pid})";
    }

    private static string FormatLiveness(RenderProbeResult r, InspectorInstance target) => r.Status switch
    {
        RenderLiveness.Alive => $"ALIVE: {r.Detail} in {r.RttMs:F0} ms (pid {target.Pid})",
        RenderLiveness.Blocked => $"BLOCKED: {r.Detail} (pid {target.Pid}). Capture the frozen frame: dotnet-stack report -p {target.Pid}",
        RenderLiveness.Dead => $"DEAD: {r.Detail} (pid {target.Pid})",
        _ => $"{r.Status}: {r.Detail} (pid {target.Pid})",
    };

    [McpServerTool, Description(
        "Run a SEQUENCE of inspector actions in ONE round-trip, one per rendered frame -- a real "
        + "frame renders between steps, so e.g. a zoom takes effect before the next step reads "
        + "state. Returns a JSON array of per-step result fragments (same shape each tool returns "
        + "individually). Prefer this over many separate calls: it avoids per-call latency and "
        + "drives deterministic measurement sequences. Each step is {\"method\":\"...\",\"params\":{...}} "
        + "where method is one of key, click, clickLabel, text, scroll, drag, frameStats, describe, "
        + "screenshot, postSignal, signals, ping, or 'wait' with params {\"frames\":N} to idle N rendered "
        + "frames (e.g. to let async work settle). Nested batch is not allowed. NOTE: a 'screenshot' step returns raw "
        + "rgba+gzip JSON, not an image -- use the standalone screenshot tool when you want the picture.")]
    public static async Task<string> batch(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("JSON array of steps. Example: [{\"method\":\"key\",\"params\":{\"key\":\"Minus\"}},{\"method\":\"wait\",\"params\":{\"frames\":3}},{\"method\":\"describe\"}]")] string stepsJson,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        using var doc = JsonDocument.Parse(stepsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("stepsJson must be a JSON array of {method, params} steps");
        var result = await socket.SendAsync(target, "batch", new { steps = doc.RootElement }, ct);
        return result.GetRawText();
    }

    [McpServerTool, Description("List the named signals this instance accepts via post_signal.")]
    public static async Task<string> list_signals(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        var result = await socket.SendAsync(target, "signals", null, ct);
        return result.GetRawText();
    }

    [McpServerTool, Description("Post a named signal to the instance's app bus. Name must be one of list_signals. Args is a JSON object passed to the signal factory.")]
    public static async Task<string> post_signal(InspectorDiscoveryClient discovery, InspectorSocketClient socket,
        [Description("Signal name (see list_signals).")] string name,
        [Description("Signal arguments as a JSON object, e.g. {\"includeFake\":true}. Default {}.")] string argsJson = "{}",
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await ResolveAsync(discovery, instance, ct);
        using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var result = await socket.SendAsync(target, "postSignal", new { name, args = argsDoc.RootElement }, ct);
        return result.GetString() ?? "queued";
    }

    // ---------------- helpers ----------------

    private static async Task<InspectorInstance> ResolveAsync(InspectorDiscoveryClient discovery, int pid, CancellationToken ct)
    {
        var all = await discovery.DiscoverAsync(ct);
        if (all.Count == 0)
            throw new InvalidOperationException("No debuggable instances found. Is a Debug-build app running with DebugInspector.Attach?");
        if (pid != 0)
            return all.FirstOrDefault(i => i.Pid == pid)
                ?? throw new InvalidOperationException($"No instance with pid {pid}. Found: {Summarize(all)}");
        if (all.Count == 1)
            return all[0];
        throw new InvalidOperationException($"Multiple instances running; pass instance=<pid>. Found: {Summarize(all)}");
    }

    private static string Summarize(IReadOnlyList<InspectorInstance> all)
        => string.Join(", ", all.Select(i => $"{i.App}(pid {i.Pid})"));

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
