using DIR.Lib;
using Org.Libsdl.App;

namespace SdlVulkan.Renderer;

/// <summary>
/// Base Android activity that brings up SDL3 + Vulkan and hands a ready <see cref="VkRenderer"/> and
/// <see cref="SdlEventLoop"/> to a consumer — the Android counterpart of a desktop <c>Program.Main</c>.
///
/// SDL's Java <c>SDLActivity</c> owns the Android lifecycle and calls <see cref="Main"/> on SDL's
/// native thread; inside it the normal desktop bring-up applies (SDL is initialized here, not by the
/// activity). A consumer subclasses this, adds the <c>[Activity(MainLauncher = true, …)]</c>
/// attribute, and overrides <see cref="OnRendererReady"/> to wire the same event-loop callbacks it
/// already sets on desktop — nothing renderer-side is Android-specific.
/// </summary>
public abstract class SdlVulkanActivity : SDLActivity
{
    // SDL's Java bridge loads this native library before invoking Main(); it ships in
    // SDL3-CS.Android per ABI (arm64-v8a, …).
    protected override string[] GetLibraries() => ["SDL3"];

    /// <summary>The window title (unused visually on Android — the app is fullscreen — but SDL wants one).</summary>
    protected virtual string WindowTitle => "SDL Vulkan";

    /// <summary>
    /// Logical size handed to <see cref="SdlVulkanWindow.Create"/>. Android ignores it (the surface is
    /// fullscreen), so the real pixel size comes from <see cref="SdlVulkanWindow.GetSizeInPixels"/>
    /// right after; it only needs to be non-zero.
    /// </summary>
    protected virtual (int Width, int Height) DesignSize => (1080, 1920);

    /// <summary>Frame-clear colour; a consumer that draws full-surface chrome should match its own background.</summary>
    protected virtual RGBAColor32 BackgroundColor => new(0x1a, 0x1a, 0x2e, 0xff);

    /// <summary>
    /// Whether to create the window fullscreen (immersive, system bars hidden). Default true on
    /// Android: SDL draws edge-to-edge and ignores <c>decorFitsSystemWindows</c>, so with the bars
    /// visible the status/nav bars overlap the surface — cropping the top of the scene and covering
    /// bottom chrome. Either way, consumers should lay out within
    /// <see cref="SdlVulkanWindow.GetSafeAreaInsets"/> (via <see cref="SdlWindow"/>) — it covers the
    /// display cutout and rounded corners when fullscreen, and additionally the system bars when not.
    /// </summary>
    protected virtual bool Fullscreen => true;

    /// <summary>
    /// The SDL window, available from <see cref="OnRendererReady"/> onward. Consumers use it for
    /// safe-area queries (<see cref="SdlVulkanWindow.GetSafeAreaInsets"/>) on ready/resize.
    /// Named to avoid hiding Android's <c>Activity.Window</c>.
    /// </summary>
    protected SdlVulkanWindow SdlWindow { get; private set; } = null!;

    /// <summary>
    /// Runs on SDL's native thread. Mirrors the desktop entry point: create the window + Vulkan
    /// surface, build the context + renderer, let the consumer wire callbacks, then pump the loop.
    /// </summary>
    protected override void Main()
    {
        // Default the diagnostic sink to logcat before bring-up: a failure in
        // MobileHost's surface bring-up happens BEFORE OnRendererReady — where a consumer would
        // normally install its own sink — and would otherwise leave no trace. ??= so a sink the
        // consumer set earlier wins (and one set in OnRendererReady simply replaces this).
        SdlEventLoop.DiagnosticLog ??= static m => global::Android.Util.Log.Info("SdlVulkan", m);

        // Fullscreen (immersive) by default: the system bars would otherwise draw over the edge-to-edge
        // SDL surface, cropping the top of the scene and covering bottom chrome. Override Fullscreen to
        // keep the bars visible (then apply safe-area insets). The bring-up itself is MobileHost's, shared
        // with the iOS host.
        MobileHost.Run(WindowTitle, DesignSize, Fullscreen, BackgroundColor,
            window => SdlWindow = window, OnRendererReady);
    }

    /// <summary>
    /// Wire the event-loop callbacks here (<see cref="SdlEventLoop.OnRender"/>,
    /// <see cref="SdlEventLoop.OnResize"/>, <see cref="SdlEventLoop.OnMouseDown"/>,
    /// <see cref="SdlEventLoop.CheckNeedsRedraw"/>, …) — identical to the desktop wiring. Touch is
    /// already dispatched as finger/mouse events by <see cref="SdlEventLoop"/>. Returns once wired;
    /// the base then runs the loop until the activity stops.
    /// </summary>
    protected abstract void OnRendererReady(VkRenderer renderer, SdlEventLoop loop);
}
