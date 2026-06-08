#if DEBUG
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// DEBUG-only live UI debug inspector. Hosts a TCP command server (ephemeral port) plus a UDP
/// multicast discovery responder so a sidecar (the published SdlVulkan.Renderer.Inspector MCP
/// server) can discover this running app and drive it: read the clickable region tree, capture a
/// screenshot, inject input, and post named signals.
/// <para>
/// Threading: the socket/UDP servers run on background tasks and only ENQUEUE commands. Every
/// command executes on the RENDER THREAD inside <see cref="DrainCommands"/>, which is chained onto
/// <see cref="SdlEventLoop.OnPostFrame"/> by <see cref="Attach(SdlEventLoop, SdlWindowView, DebugInspectorOptions)"/>.
/// Because OnPostFrame only fires on a rendered frame, enqueueing also wakes the loop via
/// <see cref="SdlWindowView.RequestRedraw"/> (latency bounded by the loop's ~16ms wait).
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
    }
    private sealed record PingCommand : InspectorCommand;
    private sealed record DescribeCommand : InspectorCommand;
    private sealed record ScreenshotCommand : InspectorCommand;
    private sealed record ListSignalsCommand : InspectorCommand;
    private sealed record ClickCommand(float X, float Y, InputModifier Mods = InputModifier.None) : InspectorCommand;
    private sealed record ClickLabelCommand(string Label) : InspectorCommand;
    private sealed record KeyCommand(InputKey Key, InputModifier Mods) : InspectorCommand;
    private sealed record TextCommand(string Text) : InspectorCommand;
    private sealed record PostSignalCommand(string Name, JsonElement Args) : InspectorCommand;

    private readonly DebugInspectorOptions _opts;
    private readonly SdlWindowView _view;
    private readonly ConcurrentQueue<InspectorCommand> _queue = new();
    private readonly TcpListener _listener;
    private readonly string _startedAtUtc;
    private readonly int _tcpPort;

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _discoveryTask;

    private DebugInspector(SdlWindowView view, DebugInspectorOptions opts)
    {
        _opts = opts;
        _view = view;
        _startedAtUtc = DateTimeOffset.UtcNow.ToString("o");
        _listener = new TcpListener(opts.BindAddress, opts.Port);
        _listener.Start();
        _tcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Attaches the inspector to the given loop + window view and starts the background servers.
    /// Chains the command pump onto the loop's <see cref="SdlEventLoop.OnPostFrame"/>. Call once,
    /// under <c>#if DEBUG</c>, after the loop's callbacks are wired but before <see cref="SdlEventLoop.Run"/>.
    /// The returned <see cref="IDisposable"/> stops the servers when disposed.
    /// </summary>
    public static DebugInspector Attach(SdlEventLoop loop, SdlWindowView view, DebugInspectorOptions opts)
    {
        var inspector = new DebugInspector(view, opts);
        inspector.Start();

        // Lambda-compose the pump onto OnPostFrame (the framework's own wiring style) so the
        // render-thread drain runs after the consumer's existing post-frame work.
        var prev = loop.OnPostFrame;
        loop.OnPostFrame = () =>
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
            _view.RequestRedraw(); // wake the loop so DrainCommands runs (OnPostFrame only fires on a rendered frame)

            var fragment = await cmd.Result.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
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
        "screenshot" => new ScreenshotCommand(),
        "signals" => new ListSignalsCommand(),
        "click" => new ClickCommand(
            p.GetProperty("x").GetSingle(),
            p.GetProperty("y").GetSingle(),
            p.TryGetProperty("mods", out var cm) && cm.ValueKind == JsonValueKind.String
                ? Enum.Parse<InputModifier>(cm.GetString() ?? "None", ignoreCase: true)
                : InputModifier.None),
        "clickLabel" => new ClickLabelCommand(p.GetProperty("label").GetString() ?? ""),
        "key" => new KeyCommand(
            Enum.Parse<InputKey>(p.GetProperty("key").GetString() ?? "", ignoreCase: true),
            p.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.String
                ? Enum.Parse<InputModifier>(m.GetString() ?? "None", ignoreCase: true)
                : InputModifier.None),
        "text" => new TextCommand(p.GetProperty("s").GetString() ?? ""),
        "postSignal" => new PostSignalCommand(
            p.GetProperty("name").GetString() ?? "",
            p.TryGetProperty("args", out var a) ? a.Clone() : default),
        _ => throw new ArgumentException($"unknown method: {method}")
    };

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
    /// Drains and executes queued commands on the render thread. Wired into the loop's OnPostFrame
    /// so all Vulkan/widget/input access is on the render thread. Never throws (per-command failures
    /// are routed to the command's TaskCompletionSource as an error response).
    /// </summary>
    private void DrainCommands()
    {
        while (_queue.TryDequeue(out var cmd))
        {
            try { cmd.Result.TrySetResult(ExecuteCommand(cmd)); }
            catch (Exception ex) { cmd.Result.TrySetException(ex); }
        }
    }

    private string ExecuteCommand(InspectorCommand cmd) => cmd switch
    {
        PingCommand => "\"pong\"",
        DescribeCommand => ExecuteDescribe(),
        ScreenshotCommand => ExecuteScreenshot(),
        ListSignalsCommand => ExecuteListSignals(),
        ClickCommand c => ExecuteClickAt(c.X, c.Y, c.Mods),
        ClickLabelCommand c => ExecuteClickLabel(c.Label),
        KeyCommand c => ExecuteKey(c.Key, c.Mods),
        TextCommand c => ExecuteText(c.Text),
        PostSignalCommand c => ExecutePostSignal(c.Name, c.Args),
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

    private string ExecuteScreenshot()
    {
        var ctx = _view.Renderer.Context;
        var rgba = ctx.ReadbackSwapchainRgba();
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

    private string ExecuteClickAt(float x, float y, InputModifier mods = InputModifier.None)
    {
        _view.OnMouseMove?.Invoke(x, y); // update cached pointer position (some consumers read it on MouseUp)
        _view.OnMouseDown?.Invoke(1, x, y, 1, mods);
        _view.OnMouseUp?.Invoke(1);
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

    private string ExecutePostSignal(string name, JsonElement args)
    {
        if (_opts.SignalFactories is null || !_opts.SignalFactories.TryGetValue(name, out var post))
            throw new ArgumentException($"unknown signal: {name}");
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
