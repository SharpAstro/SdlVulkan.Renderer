using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>A discovered debuggable instance (one running Debug-build app).</summary>
public sealed record InspectorInstance(IPAddress Address, int TcpPort, string App, string? Title, int Pid, int Proto);

/// <summary>
/// Discovers debuggable instances by sending a UDP multicast query and collecting the unicast
/// descriptor replies. The reply's source address is the instance's reachable address (used for
/// the TCP command connection), so the descriptor itself carries only the port + metadata.
/// </summary>
public sealed class InspectorDiscoveryClient(IPAddress group, int port)
{
    private static readonly byte[] Query = Encoding.UTF8.GetBytes("{\"q\":\"sdlvk-inspect\",\"proto\":1}");

    /// <summary>
    /// Multicasts a discovery query and returns the instances that reply within the collection
    /// window (~400ms). De-duped by pid.
    /// </summary>
    public async Task<IReadOnlyList<InspectorInstance>> DiscoverAsync(CancellationToken ct = default)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        // Auto-binds to an ephemeral local port on first send; replies arrive there.
        await udp.SendAsync(Query, Query.Length, new IPEndPoint(group, port));

        var found = new List<InspectorInstance>();
        var seen = new HashSet<int>();

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            while (true)
            {
                var recv = await udp.ReceiveAsync(window.Token);
                if (TryParse(recv, out var instance) && seen.Add(instance!.Pid))
                    found.Add(instance);
            }
        }
        catch (OperationCanceledException) { /* collection window elapsed */ }
        catch (SocketException) { /* transient receive error; return what we have */ }

        return found;
    }

    private static bool TryParse(UdpReceiveResult recv, out InspectorInstance? instance)
    {
        instance = null;
        try
        {
            using var doc = JsonDocument.Parse(recv.Buffer);
            var r = doc.RootElement;
            var pid = r.GetProperty("pid").GetInt32();
            var tcpPort = r.GetProperty("tcpPort").GetInt32();
            var app = r.TryGetProperty("app", out var a) ? a.GetString() ?? "" : "";
            var title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var proto = r.TryGetProperty("proto", out var p) ? p.GetInt32() : 0;
            instance = new InspectorInstance(recv.RemoteEndPoint.Address, tcpPort, app, title, pid, proto);
            return true;
        }
        catch
        {
            return false; // ignore malformed datagrams
        }
    }
}
