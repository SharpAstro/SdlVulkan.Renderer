#if DEBUG
using System.Net;
using System.Text.Json;
using DIR.Lib;
using Layout = DIR.Lib.Layout;

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
    /// Returns the arranged layout nodes to serialize for <c>describeLayout</c> -- the FULL
    /// <see cref="Layout.ArrangedNode{T}"/> tree (every painted node with its depth, rect, kind,
    /// content, background, and any click binding), not just the clickable subset
    /// <see cref="GetRegions"/> surfaces. Aggregate the chrome's + active tab's
    /// <c>PixelWidgetBase.GetCapturedLayout()</c> here. Supplying this callback flips on
    /// <see cref="LayoutInspection"/> in <see cref="DebugInspector.Attach(SdlEventLoop, SdlWindowView, DebugInspectorOptions)"/>
    /// so widgets retain their arranged tree. Invoked on the render thread inside the command pump,
    /// so reading the per-frame capture is race-free.
    /// </summary>
    public Func<IReadOnlyList<Layout.ArrangedNode<float>>>? GetLayout { get; init; }

    /// <summary>
    /// Maps a signal name to an action that builds AND posts the signal from a JSON argument element.
    /// The consumer closes over its own SignalBus (so the signal is posted with its concrete compile-time
    /// type, which is what the bus dispatches on). Invoked on the render thread.
    /// Read the JSON args with the <see cref="DebugSignalArgs"/> optional readers so a missing key falls
    /// back to the signal's default, e.g.
    /// <c>["TakePreview"] = el =&gt; bus.Post(new TakePreviewSignal(el.OptInt("ota") ?? 0, el.OptDouble("exp") ?? 1.0))</c>.
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

/// <summary>
/// Optional-value readers for the JSON args passed to a <see cref="DebugInspectorOptions.SignalFactories"/>
/// entry (the <c>{}</c> object from <c>post_signal</c>). The read-side mirror of
/// <see cref="DebugStateWriter"/>: a factory lambda reads typed optionals instead of reimplementing
/// <see cref="JsonElement"/> parsing per app. Every reader returns null when the key is absent, JSON
/// null, or the wrong kind, so the signal can fall back to its compile-time default
/// (<c>el.OptInt("ota") ?? 0</c>).
/// </summary>
public static class DebugSignalArgs
{
    extension(JsonElement e)
    {
        /// <summary>The numeric value of <paramref name="name"/>, or null if absent / not a number.</summary>
        public double? OptDouble(string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

        /// <summary>The 32-bit integer value of <paramref name="name"/>, or null if absent / not a number.</summary>
        public int? OptInt(string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

        /// <summary>The boolean value of <paramref name="name"/>, or null if absent / not a boolean.</summary>
        public bool? OptBool(string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
               && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

        /// <summary>The string value of <paramref name="name"/>, or null if absent / not a string.</summary>
        public string? OptString(string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }
}
#endif
