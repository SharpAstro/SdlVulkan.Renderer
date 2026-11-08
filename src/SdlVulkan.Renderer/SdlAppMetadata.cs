using System.Reflection;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Who the app is, as the operating system is told through <c>SDL_SetAppMetadata</c>. SDL uses it for the
/// taskbar / dock grouping and the process name, the Wayland <c>app_id</c> and X11 <c>WM_CLASS</c> of every window,
/// the macOS application menu, and the reason text of a power-inhibit request; without it the app is whatever
/// SDL's default ("SDL Application") makes of it. It must be set BEFORE <c>SDL_Init</c>, which is why the
/// creators (<see cref="SdlVulkanApp.Create"/>, <see cref="SdlVulkanWindow.Create"/> and
/// <see cref="SdlVulkanWindow.CreateTransparent"/>) apply it first and every one of them takes it.
/// </summary>
/// <param name="Name">Human-readable name, e.g. <c>"TianWen"</c>. Null: the entry assembly's name.</param>
/// <param name="Version">Version string, e.g. <c>"1.4.2"</c>. Null: the entry assembly's informational version
/// (the source-revision suffix after <c>+</c> is dropped), else its assembly version.</param>
/// <param name="Identifier">Reverse-DNS identifier, e.g. <c>"com.sharpastro.tianwen"</c>: stable, lower case,
/// no spaces. Desktop shells group windows and find the app's icon and launcher entry by it. Null: none is
/// made up, SDL's own stays.</param>
/// <param name="Creator">Optional author / publisher, e.g. <c>"SharpAstro"</c>.</param>
/// <param name="Url">Optional web page of the app.</param>
public sealed record SdlAppMetadata(
    string? Name = null,
    string? Version = null,
    string? Identifier = null,
    string? Creator = null,
    string? Url = null)
{
    /// <summary>
    /// The metadata to apply: what was given, with a missing <see cref="Name"/> and <see cref="Version"/> filled
    /// from the entry assembly so no app is anonymous. Never throws: with no entry assembly (a host that loads the
    /// app into its own process, a test runner's AppDomain without one) the fields stay null.
    /// </summary>
    internal static SdlAppMetadata Resolve(SdlAppMetadata? given)
    {
        var name = Clean(given?.Name);
        var version = Clean(given?.Version);
        if (name is null || version is null)
        {
            var entry = TryGetEntryAssembly();
            name ??= Clean(entry?.GetName().Name);
            version ??= entry is null ? null : InformationalVersionOf(entry);
        }
        return new SdlAppMetadata(name, version, Clean(given?.Identifier), Clean(given?.Creator), Clean(given?.Url));
    }

    /// <summary>
    /// Hands the resolved metadata to SDL. Callable without <c>SDL_Init</c> (SDL allows it, and it has to precede
    /// it). A field that is null is left alone, so SDL's own default stands. SDL's failure to set a property is not
    /// worth stopping an app for; it is reported as <c>false</c>.
    /// </summary>
    internal bool Apply()
    {
        var ok = true;
        // One property at a time: SDL_SetAppMetadata takes name, version and identifier together and clears the ones
        // passed as null, so it would wipe what a caller set earlier. (SDL3-CS binds the getter as returning bool,
        // so reading the old values back to merge them is not possible either.)
        if (Name is not null)
            ok &= SetAppMetadataProperty(Props.AppMetadataNameString, Name);
        if (Version is not null)
            ok &= SetAppMetadataProperty(Props.AppMetadataVersionString, Version);
        if (Identifier is not null)
            ok &= SetAppMetadataProperty(Props.AppMetadataIdentifierString, Identifier);
        if (Creator is not null)
            ok &= SetAppMetadataProperty(Props.AppMetadataCreatorString, Creator);
        if (Url is not null)
            ok &= SetAppMetadataProperty(Props.AppMetadataURLString, Url);
        return ok;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Assembly? TryGetEntryAssembly()
    {
        try { return Assembly.GetEntryAssembly(); }
        catch { return null; }
    }

    // AssemblyInformationalVersion is a plain attribute read, trim-safe, unlike scanning for types. The SDK appends
    // "+<commit>" to it; the version a person recognizes is the part before.
    private static string? InformationalVersionOf(Assembly assembly)
    {
        string? info = null;
        try { info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion; }
        catch { }
        var plus = info?.IndexOf('+') ?? -1;
        if (plus >= 0) info = info![..plus];
        info = Clean(info);
        if (info is not null) return info;
        try { return Clean(assembly.GetName().Version?.ToString()); }
        catch { return null; }
    }
}
