using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SdlVulkan.Renderer.Inspector;

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

    private sealed record RequestEnvelope(int id, string method, object? @params);
}
