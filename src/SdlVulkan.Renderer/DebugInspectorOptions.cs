#if DEBUG
using System.Net;
using System.Text.Json;
using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// DEBUG-only configuration for <see cref="DebugInspector"/>. All members are optional; a null
/// delegate means that capability is unavailable to the inspector. The consuming app supplies the
/// app-specific glue (which regions to surface, an optional state JSON, and the named-signal map)
/// while the framework owns the transport, threading, and frame capture.
/// </summary>
public sealed class DebugInspectorOptions
{
    /// <summary>Human-readable app name shown in the sidecar's instance list. Defaults to the process name.</summary>
    public string AppName { get; init; } = "";

    /// <summary>Optional live window title for the discovery descriptor (called off the render thread).</summary>
    public Func<string?>? WindowTitle { get; init; }

    /// <summary>
    /// Returns the clickable regions to serialize for <c>describe</c> (chrome + active tab).
    /// Invoked on the render thread inside the command pump, so reading the per-frame
    /// <see cref="ClickableRegion"/> snapshots is race-free.
    /// </summary>
    public Func<IReadOnlyList<ClickableRegion>>? GetRegions { get; init; }

    /// <summary>
    /// Optional callback that writes a curated app-state object alongside the region tree. The
    /// framework owns the JSON serialization -- the consumer just sets named values via
    /// <see cref="DebugStateWriter"/> (no JSON plumbing, no string round-trip). Render thread.
    /// </summary>
    public Action<DebugStateWriter>? AppState { get; init; }

    /// <summary>
    /// Maps a signal name to an action that builds AND posts the signal from a JSON argument element.
    /// The consumer closes over its own SignalBus (so the signal is posted with its concrete compile-time
    /// type, which is what the bus dispatches on). Invoked on the render thread.
    /// Example: <c>["TakePreview"] = el =&gt; bus.Post(new TakePreviewSignal(el.GetProperty("ota").GetInt32(), ...))</c>.
    /// </summary>
    public IReadOnlyDictionary<string, Action<JsonElement>>? SignalFactories { get; init; }

    // --- Transport ---

    /// <summary>
    /// Bind address for the TCP command server. <see cref="IPAddress.Any"/> (default) lets a sidecar on
    /// another LAN host connect; set <see cref="IPAddress.Loopback"/> to restrict to same-machine.
    /// </summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Any;

    /// <summary>TCP command port. 0 (default) lets the OS pick a free port so multiple instances coexist.</summary>
    public int Port { get; init; }

    /// <summary>Discovery multicast group. Site-local 239.x by default; NOT 5353 / not DNS-SD.</summary>
    public IPAddress DiscoveryGroup { get; init; } = IPAddress.Parse("239.255.77.90");

    /// <summary>Discovery multicast port shared by all instances (co-listened via ReuseAddress).</summary>
    public int DiscoveryPort { get; init; } = 47891;

    /// <summary>Whether to run the UDP multicast discovery responder. Default true.</summary>
    public bool EnableDiscovery { get; init; } = true;
}

/// <summary>
/// Thin, allocation-light wrapper the inspector hands to <see cref="DebugInspectorOptions.AppState"/>
/// so a consumer can declare a flat curated state object as named values without touching
/// <see cref="Utf8JsonWriter"/>. The framework constructs it around the live writer; the consumer
/// only calls <see cref="Set(string, string?)"/> and its overloads.
/// </summary>
public sealed class DebugStateWriter
{
    private readonly Utf8JsonWriter _writer;

    internal DebugStateWriter(Utf8JsonWriter writer) => _writer = writer;

    /// <summary>Writes a string property (or JSON null when <paramref name="value"/> is null).</summary>
    public void Set(string name, string? value)
    {
        if (value is null) _writer.WriteNull(name); else _writer.WriteString(name, value);
    }

    /// <summary>Writes a boolean property.</summary>
    public void Set(string name, bool value) => _writer.WriteBoolean(name, value);

    /// <summary>Writes a numeric property (integral values render without a decimal point).</summary>
    public void Set(string name, double value) => _writer.WriteNumber(name, value);
}
#endif
