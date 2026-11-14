using System.Runtime.InteropServices;

namespace SdlVulkan.Renderer;

/// <summary>
/// Where Vulkan and SDL come from on Apple platforms, where neither is a system library.
///
/// <para>On iOS there is no Vulkan loader at all: the app bundles MoltenVK (Vulkan on Metal) and SDL3 as
/// DYNAMIC frameworks, which the build copies into <c>&lt;App&gt;.app/Frameworks/</c>. Both are found there by an
/// absolute path, never by a bare name: a relative <c>dlopen</c> of <c>MoltenVK.framework/MoltenVK</c> is resolved
/// against the working directory, not the bundle. SDL and Vortice are pointed at the SAME MoltenVK file, so the
/// instance SDL builds a surface against is the one Vortice created.</para>
///
/// <para>On macOS nothing is redirected: SDL and Vortice each find <c>libvulkan</c> (the LunarG loader) or
/// <c>libMoltenVK</c> as they always have. Only the portability enumeration differs there (see
/// <see cref="IsApple"/>).</para>
/// </summary>
internal static class AppleVulkan
{
    /// <summary>
    /// Whether this is an Apple platform, where the Vulkan device is MoltenVK, a portability implementation: the
    /// instance then asks for <c>VK_KHR_portability_enumeration</c>, which a loader needs before it lists one.
    /// </summary>
    internal static bool IsApple => OperatingSystem.IsIOS() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
        || OperatingSystem.IsTvOS();

    /// <summary>The MoltenVK an iOS app bundles, relative to the bundle (a dynamic xcframework's device slice lands here).</summary>
    internal const string BundledMoltenVk = "Frameworks/MoltenVK.framework/MoltenVK";

    /// <summary>The SDL3 an iOS app bundles, relative to the bundle.</summary>
    internal const string BundledSdl3 = "Frameworks/SDL3.framework/SDL3";

    /// <summary>
    /// The Vulkan library to hand SDL and Vortice: the bundled MoltenVK on iOS, when the app bundles it, else
    /// <c>null</c> (each takes its own default, which is the system loader everywhere a system loader exists).
    /// </summary>
    internal static string? BundledLibraryPath()
        => OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() ? InBundle(BundledMoltenVk) : null;

    /// <summary>
    /// On iOS, resolves SDL3-CS's <c>[DllImport("SDL3")]</c> to the bundled <c>SDL3.framework</c>. The runtime would
    /// probe <c>libSDL3.dylib</c> and <c>SDL3</c>, neither of which a framework is named. Does nothing elsewhere, when
    /// the app bundles no framework, or when the app already installed a resolver of its own on that assembly (a
    /// resolver can be set once per assembly, and the app's wins).
    /// </summary>
    internal static void ResolveBundledSdl()
    {
        if (!(OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()) || InBundle(BundledSdl3) is not { } sdlPath)
            return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SDL3.SDL).Assembly, (name, _, _) =>
                name == "SDL3" && NativeLibrary.TryLoad(sdlPath, out var handle) ? handle : nint.Zero);
        }
        catch (InvalidOperationException)
        {
            // The app set a resolver on SDL3-CS first; it knows where its SDL is.
        }
    }

    private static string? InBundle(string relative)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relative);
        return File.Exists(path) ? path : null;
    }
}
