using System.IO.Compression;
using System.Text.Json;
using SharpAstro.Png;

namespace SdlVulkan.Renderer.Inspector.Client;

/// <summary>A captured window frame: RGBA8 rows, top to bottom, and its PNG encoding.</summary>
public sealed class InspectorScreenshot
{
    private byte[]? _png;

    internal InspectorScreenshot(int width, int height, byte[] rgba, bool unminimised)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        Unminimised = unminimised;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary><c>Width * Height * 4</c> bytes, R, G, B, A.</summary>
    public byte[] Rgba { get; }

    /// <summary>The window was minimized and the capture restored it (it stays restored unless <c>reminimize</c> was asked).</summary>
    public bool Unminimised { get; }

    /// <summary>The frame encoded as a PNG, encoded on first use.</summary>
    public byte[] Png => _png ??= PngWriter.Encode(Rgba, Width, Height);
}

/// <summary>The app's rolling-average frame time and the slow-frame floor, both in milliseconds.</summary>
public readonly record struct InspectorFrameStats(double AvgFrameMs, double SlowFrameFloorMs);

/// <summary>The size a resize settled at; <paramref name="Clamped"/> when the display work area cut the request.</summary>
public readonly record struct InspectorResizeResult(int Width, int Height, bool Clamped);

/// <summary>
/// A typed, async client for one inspected app. Connect by pid (resolved through DIR.Lib's per-user
/// registry) and the per-run token the app was started with, then call the verbs. Every failure the app
/// reports surfaces as an <see cref="InspectorException"/> carrying the server's own message. One short-lived
/// TCP connection per call, so a client is cheap to keep and safe to share.
/// </summary>
public sealed class InspectorClient
{
    private readonly InspectorInstance _target;
    private readonly InspectorSocketClient _socket;

    /// <summary>A client for a known instance (as discovery or a test supplies it).</summary>
    public InspectorClient(InspectorInstance target, string? token = null)
    {
        _target = target;
        _socket = new InspectorSocketClient(token);
    }

    /// <summary>The instance this client talks to.</summary>
    public InspectorInstance Instance => _target;

    /// <summary>
    /// Finds the app with process id <paramref name="pid"/> in the registry and returns a client for it.
    /// <paramref name="pid"/> 0 means the one running instance; with none or several it throws, naming what was
    /// found. <paramref name="token"/> defaults to <c>DIR_INSPECTOR_TOKEN</c>.
    /// </summary>
    public static Task<InspectorClient> ConnectAsync(int pid, string? token = null, CancellationToken ct = default)
        => ConnectAsync(pid, token, InspectorRegistry.DefaultDirectory, ct);

    internal static Task<InspectorClient> ConnectAsync(int pid, string? token, string? registryDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var all = InspectorRegistry.Read(registryDirectory)
            .Where(i => InspectorDiscoveryClient.IsSdl(i.Kind)).ToList();
        if (all.Count == 0)
            throw new InspectorException("connect", "No inspectable instances found. Is an app running with DIR_INSPECTOR=1 and DIR_INSPECTOR_TOKEN set?");
        InspectorInstance target;
        if (pid != 0)
        {
            target = all.FirstOrDefault(i => i.Pid == pid)
                ?? throw new InspectorException("connect", $"No instance with pid {pid}. Found: {Summarize(all)}");
        }
        else if (all.Count == 1)
        {
            target = all[0];
        }
        else
        {
            throw new InspectorException("connect", $"Multiple instances running; pass a pid. Found: {Summarize(all)}");
        }
        return Task.FromResult(new InspectorClient(target, token));
    }

    private static string Summarize(IReadOnlyList<InspectorInstance> all)
        => string.Join(", ", all.Select(i => $"{i.App}(pid {i.Pid})"));

    /// <summary>The app's ping reply (an object naming the app and protocol).</summary>
    public Task<JsonElement> PingAsync(CancellationToken ct = default) => _socket.SendAsync(_target, "ping", null, ct);

    /// <summary>The live clickable-region tree plus the app's optional state.</summary>
    public Task<JsonElement> DescribeUiAsync(CancellationToken ct = default) => _socket.SendAsync(_target, "describe", null, ct);

    /// <summary>The full arranged layout tree the app painted this frame.</summary>
    public Task<JsonElement> DescribeLayoutAsync(CancellationToken ct = default) => _socket.SendAsync(_target, "describeLayout", null, ct);

    /// <summary>
    /// Captures the window. With <paramref name="width"/> and <paramref name="height"/> (window pixels) the
    /// window is resized, captured and put back. A minimized window is restored for the capture and stays so
    /// unless <paramref name="reminimize"/>.
    /// </summary>
    public async Task<InspectorScreenshot> ScreenshotAsync(bool reminimize = false, int width = 0, int height = 0,
        CancellationToken ct = default)
    {
        var args = (width > 0 || height > 0)
            ? Json.Obj(("reminimize", reminimize), ("width", width), ("height", height))
            : reminimize ? Json.Obj(("reminimize", true)) : null;
        var r = await _socket.SendAsync(_target, "screenshot", args, ct);
        var w = r.GetProperty("width").GetInt32();
        var h = r.GetProperty("height").GetInt32();
        var format = r.TryGetProperty("format", out var f) ? f.GetString() : null;
        var payload = Convert.FromBase64String(r.GetProperty("base64").GetString() ?? "");
        var rgba = format == "rgba+gzip" ? Gunzip(payload) : payload;
        var unmin = r.TryGetProperty("unminimised", out var u) && u.ValueKind == JsonValueKind.True;
        return new InspectorScreenshot(w, h, rgba, unmin);
    }

    /// <summary>A left click at (x, y); <paramref name="clicks"/> 2 is a double click.</summary>
    public Task ClickAsync(float x, float y, string mods = "None", int clicks = 1, CancellationToken ct = default)
        => _socket.SendAsync(_target, "click", Json.Obj(("x", x), ("y", y), ("mods", mods), ("clicks", clicks)), ct);

    /// <summary>A left-button press, drag and release.</summary>
    public Task DragAsync(float x1, float y1, float x2, float y2, string mods = "None", int steps = 8, CancellationToken ct = default)
        => _socket.SendAsync(_target, "drag",
            Json.Obj(("x1", x1), ("y1", y1), ("x2", x2), ("y2", y2), ("mods", mods), ("steps", steps)), ct);

    /// <summary>Pointer motion with no button held (hover).</summary>
    public Task MoveAsync(float x1, float y1, float x2, float y2, int steps = 8, string mods = "None", CancellationToken ct = default)
        => _socket.SendAsync(_target, "move",
            Json.Obj(("x1", x1), ("y1", y1), ("x2", x2), ("y2", y2), ("steps", steps), ("mods", mods)), ct);

    /// <summary>A key press by DIR.Lib <c>InputKey</c> name.</summary>
    public Task PressKeyAsync(string key, string mods = "None", CancellationToken ct = default)
        => _socket.SendAsync(_target, "key", Json.Obj(("key", key), ("mods", mods)), ct);

    /// <summary>Text input, as if typed.</summary>
    public Task TypeTextAsync(string text, CancellationToken ct = default)
        => _socket.SendAsync(_target, "text", Json.Obj(("s", text)), ct);

    /// <summary>Resizes the window so its client area is width x height window pixels.</summary>
    public async Task<InspectorResizeResult> ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        var r = await _socket.SendAsync(_target, "resize", Json.Obj(("width", width), ("height", height)), ct);
        return new InspectorResizeResult(r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(),
            r.TryGetProperty("clamped", out var c) && c.ValueKind == JsonValueKind.True);
    }

    /// <summary>Restores the window to its floating size and position.</summary>
    public Task RestoreAsync(CancellationToken ct = default) => _socket.SendAsync(_target, "restore", null, ct);

    /// <summary>The rolling-average frame time and the slow-frame floor.</summary>
    public async Task<InspectorFrameStats> FrameStatsAsync(CancellationToken ct = default)
    {
        var r = await _socket.SendAsync(_target, "frameStats", null, ct);
        return new InspectorFrameStats(r.GetProperty("avgFrameMs").GetDouble(), r.GetProperty("slowFrameFloorMs").GetDouble());
    }

    /// <summary>The Vulkan validation-layer report ({enabled, syncValidation, totalMessages, syncHazards, recent}).</summary>
    public Task<JsonElement> ValidationReportAsync(CancellationToken ct = default)
        => _socket.SendAsync(_target, "validationReport", null, ct);

    /// <summary>
    /// Is the app's render thread pumping? A ping that round-trips within <paramref name="budget"/> (default
    /// 1.5 s) says Alive; connected but silent says Blocked; unreachable says Dead. Never throws for those.
    /// </summary>
    public Task<RenderProbeResult> RenderLivenessAsync(TimeSpan? budget = null, CancellationToken ct = default)
        => _socket.ProbeRenderAsync(_target, budget ?? TimeSpan.FromMilliseconds(1500), ct);

    /// <summary>The named signals the app accepts.</summary>
    public Task<JsonElement> SignalsAsync(CancellationToken ct = default) => _socket.SendAsync(_target, "signals", null, ct);

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
