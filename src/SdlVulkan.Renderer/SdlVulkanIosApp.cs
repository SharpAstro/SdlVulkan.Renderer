using System.Runtime.Versioning;
using DIR.Lib;
using static SDL3.SDL;

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
    // SDL calls back through a native function pointer, so the delegate must outlive the call (a static field) and,
    // under Mono's ahead-of-time compilation on iOS, its target must carry [MonoPInvokeCallback].
    private static readonly MainFunc SdlMainCallback = SdlMain;
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
    public int Run()
    {
        if (Interlocked.CompareExchange(ref _running, this, null) is not null)
            throw new InvalidOperationException("An SdlVulkanIosApp is already running; Run is called once per process.");

        // Before any SDL call: SDL3-CS imports "SDL3", which on iOS is a framework inside the bundle.
        AppleVulkan.ResolveBundledSdl();
        return RunApp(0, [], SdlMainCallback, nint.Zero);
    }

    [MonoPInvokeCallback(typeof(MainFunc))]
    private static int SdlMain(int argc, string[]? argv)
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

/// <summary>
/// Marks a static method that native code calls through a delegate. Mono's ahead-of-time compiler (iOS) recognises
/// the attribute by NAME and generates the native-to-managed wrapper the call needs; without it the callback fails at
/// run time on a device. Declared here because the iOS assembly that carries Microsoft's copy is not referenced by a
/// <c>net10.0</c> library.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class MonoPInvokeCallbackAttribute(Type delegateType) : Attribute
{
    public Type DelegateType { get; } = delegateType;
}
