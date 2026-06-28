using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>Render-thread liveness verdict from <see cref="InspectorSocketClient.ProbeRenderAsync"/>.</summary>
public enum RenderLiveness
{
    /// <summary>A ping round-tripped: the render loop drained the command queue, so it is pumping frames.</summary>
    Alive,
    /// <summary>Connected, but the ping did not round-trip within the budget: the render thread is wedged.</summary>
    Blocked,
    /// <summary>The TCP port could not be reached: the process is gone / crashed.</summary>
    Dead,
}

/// <summary>Result of a render-thread liveness probe: the verdict, the ping round-trip, and a human detail.</summary>
public readonly record struct RenderProbeResult(RenderLiveness Status, double RttMs, string Detail);

/// <summary>
/// Sends a single newline-delimited JSON-RPC request to a chosen instance's TCP command port and
/// returns the parsed result element. One short-lived connection per call (debug tool; simplicity
/// over connection reuse).
/// </summary>
public sealed class InspectorSocketClient
{
    private int _nextId;

    /// <summary>
    /// Connects to <paramref name="target"/>, sends <c>{id,method,params}</c>, reads one response
    /// line, and returns the <c>result</c> element (cloned). Throws on an <c>error</c> response.
    /// </summary>
    public async Task<JsonElement> SendAsync(InspectorInstance target, string method, object? parameters = null,
        CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(target.Address, target.TcpPort, ct);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var id = Interlocked.Increment(ref _nextId);
        var request = JsonSerializer.Serialize(new RequestEnvelope(id, method, parameters));
        await writer.WriteLineAsync(request.AsMemory(), ct);

        var responseLine = await reader.ReadLineAsync(ct)
            ?? throw new InvalidOperationException($"instance {target.App} (pid {target.Pid}) closed the connection with no response");

        using var doc = JsonDocument.Parse(responseLine);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
            throw new InvalidOperationException($"inspector error: {err.GetString()}");
        return root.GetProperty("result").Clone();
    }

    /// <summary>
    /// Probes whether <paramref name="target"/>'s RENDER THREAD is alive, blocked, or dead. The app
    /// executes every inspector command -- including <c>ping</c> -- on the render thread (drained from
    /// the loop's OnPostFrame, which only fires on a rendered frame). So a ping that round-trips within
    /// <paramref name="budget"/> proves the render loop completed a frame and drained its queue; a TCP
    /// connection that succeeds but yields no ping reply within the budget means the render thread is
    /// wedged even though the process is alive; a refused connection means the process is gone. Uses a
    /// SHORT client-side budget so it does not wait out the app's own 10 s command timeout.
    /// </summary>
    public async Task<RenderProbeResult> ProbeRenderAsync(InspectorInstance target, TimeSpan budget, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();

        // 1) Connect. A refused/timed-out connect means the process is gone (DEAD), distinct from a
        //    connected-but-silent render thread (BLOCKED) below.
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(budget);
            await tcp.ConnectAsync(target.Address, target.TcpPort, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RenderProbeResult(RenderLiveness.Dead, budget.TotalMilliseconds,
                $"TCP connect to {target.Address}:{target.TcpPort} timed out in {budget.TotalMilliseconds:F0} ms");
        }
        catch (SocketException ex)
        {
            return new RenderProbeResult(RenderLiveness.Dead, 0, $"connection failed: {ex.SocketErrorCode}");
        }

        // 2) Ping. The reply has to come off the render thread, so the round-trip IS the heartbeat.
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var id = Interlocked.Increment(ref _nextId);
        var sw = Stopwatch.StartNew();
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(budget);
            await writer.WriteLineAsync($"{{\"id\":{id},\"method\":\"ping\"}}".AsMemory(), readCts.Token);
            var line = await reader.ReadLineAsync(readCts.Token);
            sw.Stop();

            if (line is null)
                return new RenderProbeResult(RenderLiveness.Dead, sw.Elapsed.TotalMilliseconds, "connection closed with no response");
            if (line.Contains("pong"))
                return new RenderProbeResult(RenderLiveness.Alive, sw.Elapsed.TotalMilliseconds, "pong");
            // An app-side error (e.g. its own command timed out) means the render thread is not draining.
            if (line.Contains("\"error\""))
                return new RenderProbeResult(RenderLiveness.Blocked, sw.Elapsed.TotalMilliseconds, $"app error (render thread not draining): {line}");
            return new RenderProbeResult(RenderLiveness.Alive, sw.Elapsed.TotalMilliseconds, line);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Connected, but no ping reply within the budget == the render thread is wedged.
            return new RenderProbeResult(RenderLiveness.Blocked, budget.TotalMilliseconds,
                $"connected but no ping reply within {budget.TotalMilliseconds:F0} ms -- render thread blocked");
        }
    }

    private sealed record RequestEnvelope(int id, string method, object? @params);
}
