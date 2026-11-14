using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// Base iOS host that brings up SDL3 + Vulkan (MoltenVK) and hands a ready <see cref="VkRenderer"/> and
/// <see cref="SdlEventLoop"/> to a consumer: the iOS counterpart of the Android <c>SdlVulkanActivity</c>, with the
/// same members, so one subclass body serves both.
///
/// <para>A consumer's <c>net10.0-ios</c> head calls <see cref="Run"/> from its <c>Main</c> INSTEAD of
/// <c>UIApplication.Main</c>: SDL owns the UIKit application (<c>SDL_RunApp</c> runs <c>UIApplicationMain</c> with
/// SDL's own delegate) and calls back into the shared bring-up once the app has launched. The head bundles
/// <c>SDL3.xcframework</c> and <c>MoltenVK.xcframework</c> as dynamic frameworks (<c>NativeReference</c>); see
/// <see cref="AppleVulkan"/> for how they are found.</para>
///
/// <para>Compiled into the plain <c>net10.0</c> assembly on purpose: it needs no iOS API, only SDL's P/Invokes, so
/// the package needs no <c>net10.0-ios</c> target (whose workload does not exist on the Linux runner that packs
/// it), and an iOS head consumes the <c>net10.0</c> asset.</para>
/// </summary>
[SupportedOSPlatform("ios")]
public abstract class SdlVulkanIosApp
{
    private static SdlVulkanIosApp? _running;

    /// <summary>The window title (unused visually on iOS, where the app is fullscreen, but SDL wants one).</summary>
    protected virtual string WindowTitle => "SDL Vulkan";

    /// <summary>
    /// Logical size handed to <see cref="SdlVulkanWindow.Create"/>. iOS ignores it (the window fills the screen),
    /// so the real pixel size comes from <see cref="SdlVulkanWindow.GetSizeInPixels"/>; it only needs to be non-zero.
    /// </summary>
    protected virtual (int Width, int Height) DesignSize => (1024, 1366);

    /// <summary>Frame-clear colour; a consumer that draws full-surface chrome should match its own background.</summary>
    protected virtual RGBAColor32 BackgroundColor => new(0x1a, 0x1a, 0x2e, 0xff);

    /// <summary>
    /// Whether to create the window fullscreen (status bar hidden). Default true, as on Android. Either way,
    /// consumers should lay out within <see cref="SdlVulkanWindow.GetSafeAreaInsets"/> (via <see cref="SdlWindow"/>),
    /// which covers the rounded corners and the home indicator.
    /// </summary>
    protected virtual bool Fullscreen => true;

    /// <summary>The SDL window, available from <see cref="OnRendererReady"/> onward.</summary>
    protected SdlVulkanWindow SdlWindow { get; private set; } = null!;

    /// <summary>
    /// Hands the process to SDL and does not return while the app runs (<c>UIApplicationMain</c> never returns on
    /// iOS). Call it from the head's <c>Main</c>, once.
    /// </summary>
    public unsafe int Run()
    {
        if (Interlocked.CompareExchange(ref _running, this, null) is not null)
            throw new InvalidOperationException("An SdlVulkanIosApp is already running; Run is called once per process.");

        // Before any SDL call: SDL3-CS imports "SDL3", which on iOS is a framework inside the bundle.
        AppleVulkan.ResolveBundledSdl();

        // NOT SDL3-CS's RunApp. Its MainFunc callback declares `string[] argv` with no [MarshalAs] size, and Mono (the
        // iOS runtime) cannot build a native-to-managed wrapper for an unsized array: the first iPad run of chess ended
        // 0.17 s after launch with "MarshalDirectiveException: [MarshalAs] attribute required to marshal arrays to
        // managed code" at SDL.RunApp, before a line of the bring-up ran (7.92). So SDL_RunApp is called through a
        // function pointer, back into an [UnmanagedCallersOnly] SdlMain whose argv is a raw pointer: nothing to marshal.
        // The library is loaded through SDL3-CS's assembly, so the resolver above (or an app's own) finds it.
        var sdl = NativeLibrary.Load("SDL3", typeof(SDL3.SDL).Assembly, null);
        var runApp = (delegate* unmanaged[Cdecl]<int, nint, delegate* unmanaged[Cdecl]<int, nint, int>, nint, int>)
            NativeLibrary.GetExport(sdl, "SDL_RunApp");
        return runApp(0, nint.Zero, &SdlMain, nint.Zero);
    }

    // argv is SDL's char** (unused: an iOS app is not started with arguments), kept a raw pointer so that the callback
    // needs no marshalling at all. [UnmanagedCallersOnly] also gives Mono's ahead-of-time compiler the native entry
    // point it needs, which [MonoPInvokeCallback] used to.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int SdlMain(int argc, nint argv)
    {
        // An exception must not cross back into SDL's native frame: it would end the process with no trace.
        try
        {
            // Default the diagnostic sink before bring-up, as the Android host does: a failure in the surface
            // bring-up happens BEFORE OnRendererReady, where a consumer would install its own. On iOS stderr goes
            // to the device log. ??= so a sink the consumer set earlier wins.
            SdlEventLoop.DiagnosticLog ??= static m => Console.Error.WriteLine($"SdlVulkan: {m}");

            var app = _running ?? throw new InvalidOperationException("SDL called back with no SdlVulkanIosApp running.");
            MobileHost.Run(app.WindowTitle, app.DesignSize, app.Fullscreen, app.BackgroundColor,
                window => app.SdlWindow = window, app.OnRendererReady);
            return 0;
        }
        catch (Exception ex)
        {
            SdlEventLoop.DiagnosticLog?.Invoke($"[ios] the app ended with an exception: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Wire the event-loop callbacks here (<see cref="SdlEventLoop.OnRender"/>, <see cref="SdlEventLoop.OnResize"/>,
    /// <see cref="SdlEventLoop.OnMouseDown"/>, <see cref="SdlEventLoop.CheckNeedsRedraw"/>, …), identical to the
    /// desktop and Android wiring. Touch is already dispatched as finger/mouse events by <see cref="SdlEventLoop"/>.
    /// Returns once wired; the base then runs the loop until the app ends.
    /// </summary>
    protected abstract void OnRendererReady(VkRenderer renderer, SdlEventLoop loop);
}
