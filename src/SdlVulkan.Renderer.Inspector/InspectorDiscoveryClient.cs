using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>A discovered debuggable instance (one running Debug-build app).</summary>
/// <param name="Address">Where the instance was found: <c>127.0.0.1</c> for a registry entry, otherwise where the
/// multicast reply came FROM. Not what the command connection uses; see <see cref="InspectorSocketClient"/>.</param>
/// <param name="Kind">The surface kind it claims. Only an <c>sdl</c> surface speaks these verbs.</param>
public sealed record InspectorInstance(
    IPAddress Address, int TcpPort, string App, string? Title, int Pid, int Proto, string Kind);

/// <summary>A discovery's instances, and what it skipped that a user should hear about.</summary>
internal sealed record DiscoveryResult(IReadOnlyList<InspectorInstance> Instances, IReadOnlyList<string> Notes);

/// <summary>
/// Discovers debuggable instances in two ways, merged. First DIR.Lib's per-user file registry
/// (<see cref="InspectorRegistry"/>), which is how a DEBUG app on DIR.Lib 11.21 or later is found on this
/// machine: that app answers no multicast unless asked to, so it never binds a socket Windows Firewall would
/// prompt about. Then a UDP multicast query, collecting the unicast descriptor replies, which still finds
/// instances on other machines and builds older than 11.21 (which do not register). The registry needs no
/// network at all; a multicast that cannot be sent costs only its own results.
/// </summary>
public sealed class InspectorDiscoveryClient
{
    private static readonly byte[] Query = Encoding.UTF8.GetBytes("{\"q\":\"dir-inspect\",\"proto\":1}");

    private readonly IPAddress _group;
    private readonly int _port;
    private readonly string? _registryDirectory;

    /// <summary>Discovers through the per-user registry and the multicast group <paramref name="group"/>:<paramref name="port"/>.</summary>
    public InspectorDiscoveryClient(IPAddress group, int port)
        : this(group, port, InspectorRegistry.DefaultDirectory)
    {
    }

    /// <summary>The registry folder overridden, so a test reads fixtures and never the real per-user folder.</summary>
    internal InspectorDiscoveryClient(IPAddress group, int port, string? registryDirectory)
    {
        _group = group;
        _port = port;
        _registryDirectory = registryDirectory;
    }

    /// <summary>
    /// Reads the registry, then multicasts a discovery query and collects replies for ~400ms, and returns both
    /// merged (<see cref="Merge"/>): registry entries first, de-duped by (address, pid).
    /// </summary>
    public async Task<IReadOnlyList<InspectorInstance>> DiscoverAsync(CancellationToken ct = default)
        => (await DiscoverWithNotesAsync(ct)).Instances;

    /// <summary>
    /// <see cref="DiscoverAsync"/>, plus a line for each registry descriptor skipped although its pid is
    /// running (a start-time mismatch). Each note also goes to stderr, which is the log here (stdout is the
    /// JSON-RPC channel), so a tool with nowhere to show it still leaves a trace.
    /// </summary>
    internal async Task<DiscoveryResult> DiscoverWithNotesAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        var registered = InspectorRegistry.Read(_registryDirectory, notes).Where(i => IsSdl(i.Kind)).ToList();
        foreach (var note in notes) Console.Error.WriteLine($"[inspector] {note}");

        var replied = await MulticastAsync(ct);
        return new DiscoveryResult(Merge(registered, replied, replied.Count == 0 ? _ => false : LocalAddressFilter()), notes);
    }

    private async Task<List<InspectorInstance>> MulticastAsync(CancellationToken ct)
    {
        var found = new List<InspectorInstance>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            // Auto-binds to an ephemeral local port on first send; replies arrive there. With no network the
            // send throws, which leaves the registry's results to stand alone.
            await udp.SendAsync(Query, Query.Length, new IPEndPoint(_group, _port));

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromMilliseconds(400));
            try
            {
                while (true)
                {
                    var recv = await udp.ReceiveAsync(window.Token);
                    if (TryParseDescriptor(recv.Buffer, recv.RemoteEndPoint.Address, out var instance, out _)
                        && IsSdl(instance!.Kind))
                        found.Add(instance);
                }
            }
            catch (OperationCanceledException) { /* collection window elapsed */ }
        }
        catch (SocketException) { /* no network, or a transient receive error: return what we have */ }

        return found;
    }

    /// <summary>
    /// Registry entries, then multicast replies, keeping the first of each (address, pid). A reply from one of
    /// this machine's own addresses counts as <c>127.0.0.1</c>: an app that registers AND answers multicast (LAN
    /// discovery opted in) replies from its interface address, and is still the instance the registry listed.
    /// The address stays in the key because an instance on another machine can share a pid with a local one.
    /// A process hosting several cores (one per window) lists once, as it always has, since a tool picks its
    /// target by pid.
    /// </summary>
    internal static IReadOnlyList<InspectorInstance> Merge(
        IEnumerable<InspectorInstance> registered, IEnumerable<InspectorInstance> replied, Func<IPAddress, bool> isLocal)
    {
        var merged = new List<InspectorInstance>();
        var seen = new HashSet<(IPAddress, int)>();
        foreach (var instance in registered.Concat(replied))
        {
            var machine = IPAddress.IsLoopback(instance.Address) || isLocal(instance.Address)
                ? IPAddress.Loopback
                : instance.Address;
            if (seen.Add((machine, instance.Pid))) merged.Add(instance);
        }
        return merged;
    }

    /// <summary>
    /// Whether an address is one of this machine's own. Built once per discovery, and only when something
    /// replied; a machine whose interfaces cannot be listed treats only loopback as local.
    /// </summary>
    private static Func<IPAddress, bool> LocalAddressFilter()
    {
        var local = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    local.Add(unicast.Address);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return local.Contains;
    }

    /// <summary>
    /// Whether a reply is a surface this sidecar can drive. Discovery is ONE shared group, so a terminal
    /// app on the same machine answers the same query, and offering it a screenshot verb would be
    /// nonsense. <c>unknown</c> is accepted because an app built before the kind field existed does not
    /// send one, and refusing it would make that app invisible rather than merely unlabelled. A registry
    /// entry is filtered by the same rule.
    /// </summary>
    internal static bool IsSdl(string kind) => kind is "sdl" or "unknown";

    /// <summary>
    /// Parses a descriptor: a multicast reply, or a registry file, which carries the same members plus
    /// <c>processStart</c>. False if it is not JSON, or lacks a usable <c>pid</c> (positive) or <c>tcpPort</c>
    /// (1 to 65535); members it does not know are ignored. <paramref name="processStart"/> is null when absent
    /// or unreadable, and the liveness check then goes by the pid alone.
    /// </summary>
    internal static bool TryParseDescriptor(ReadOnlyMemory<byte> json, IPAddress address,
        out InspectorInstance? instance, out DateTime? processStart)
    {
        instance = null;
        processStart = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var pid = r.GetProperty("pid").GetInt32();
            var tcpPort = r.GetProperty("tcpPort").GetInt32();
            // Pid 0 is Windows' Idle process, which the liveness check would find running and unreadable, and so
            // keep; no port outside this range can be connected to.
            if (pid <= 0 || tcpPort is <= 0 or > IPEndPoint.MaxPort) return false;
            var app = r.TryGetProperty("app", out var a) ? a.GetString() ?? "" : "";
            var title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var proto = r.TryGetProperty("proto", out var p) ? p.GetInt32() : 0;
            var kind = r.TryGetProperty("kind", out var k) ? k.GetString() ?? "unknown" : "unknown";
            if (r.TryGetProperty("processStart", out var s) && s.ValueKind == JsonValueKind.String
                && s.TryGetDateTimeOffset(out var started))
                processStart = started.UtcDateTime;
            instance = new InspectorInstance(address, tcpPort, app, title, pid, proto, kind);
            return true;
        }
        catch
        {
            return false; // malformed: a stray datagram, or a file not written by the registry's writer
        }
    }
}
