using DIR.Lib;
using Org.Libsdl.App;
using Vortice.Vulkan;
using static SDL3.SDL;

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
    /// bottom chrome. A consumer that wants the bars visible can override this to false and apply its
    /// own safe-area insets.
    /// </summary>
    protected virtual bool Fullscreen => true;

    /// <summary>
    /// Runs on SDL's native thread. Mirrors the desktop entry point: create the window + Vulkan
    /// surface, build the context + renderer, let the consumer wire callbacks, then pump the loop.
    /// </summary>
    protected override void Main()
    {
        var (dw, dh) = DesignSize;
        // Fullscreen (immersive) by default: the system bars would otherwise draw over the edge-to-edge
        // SDL surface, cropping the top of the scene and covering bottom chrome. Override Fullscreen to
        // keep the bars visible (then apply safe-area insets).
        using var window = SdlVulkanWindow.Create(WindowTitle, dw, dh, fullscreen: Fullscreen);

        var (ctx, renderer) = CreateRendererWhenSurfaceReady(window);
        try
        {
            var loop = new SdlEventLoop(window, renderer) { BackgroundColor = BackgroundColor };
            OnRendererReady(renderer, loop);
            loop.Run();
        }
        finally
        {
            renderer.Dispose();
            ctx.Dispose();
        }
    }

    // Android recreates the window's native surface during the splash->app handoff, so the surface SDL
    // created at window construction is already stale by the time the swapchain is built — vkCreate*
    // returns VK_ERROR_SURFACE_LOST_KHR (VulkanContext.CreateSwapchain). Pump events to let the window
    // settle, recreate the surface against the current native window, and retry until it holds. On
    // desktop the first attempt succeeds immediately.
    private static (VulkanContext, VkRenderer) CreateRendererWhenSurfaceReady(SdlVulkanWindow window)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            PumpFor(100);
            window.GetSizeInPixels(out var w, out var h);
            if (w <= 0 || h <= 0) continue; // surface not sized yet — keep settling
            try
            {
                window.RecreateSurface();
                var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)w, (uint)h);
                var renderer = new VkRenderer(ctx, (uint)w, (uint)h);
                return (ctx, renderer);
            }
            catch (VkException ex)
            {
                last = ex; // transient surface during startup — settle and retry
            }
        }
        throw last ?? new InvalidOperationException("Vulkan surface never became ready.");
    }

    // Pumps SDL events for roughly the given duration so Android surface/lifecycle events are drained.
    private static void PumpFor(uint ms)
    {
        var deadline = GetTicks() + ms;
        do { PumpEvents(); Delay(16); } while (GetTicks() < deadline);
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
