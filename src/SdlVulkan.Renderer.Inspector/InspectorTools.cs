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
