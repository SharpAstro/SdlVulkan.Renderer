using DIR.Lib;
using Vortice.Vulkan;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// The bring-up a mobile host runs once SDL has handed it control: a fullscreen window, a renderer built once the
/// window's native surface has settled, the consumer's wiring, then the loop until the app ends. ONE body for
/// Android (<c>SdlVulkanActivity</c>, on SDL's native thread) and iOS (<see cref="SdlVulkanIosApp"/>, inside
/// <c>SDL_RunApp</c>), so a fix to either platform's startup reaches both.
/// </summary>
internal static class MobileHost
{
    internal static void Run(string title, (int Width, int Height) designSize, bool fullscreen, RGBAColor32 background,
        Action<SdlVulkanWindow> windowCreated, Action<VkRenderer, SdlEventLoop> rendererReady)
    {
        var (dw, dh) = designSize;
        using var window = SdlVulkanWindow.Create(title, dw, dh, fullscreen: fullscreen);
        windowCreated(window);

        var (ctx, renderer) = CreateRendererWhenSurfaceReady(window);
        try
        {
            var loop = new SdlEventLoop(window, renderer) { BackgroundColor = background };
            rendererReady(renderer, loop);
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
    // desktop (and, expected, on iOS, whose Metal layer exists from window creation) the first attempt
    // succeeds immediately.
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
                SdlEventLoop.DiagnosticLog?.Invoke($"[startup] attempt {attempt + 1}: {ex.Message}; retrying");
            }
        }
        SdlEventLoop.DiagnosticLog?.Invoke(
            $"[startup] giving up: Vulkan surface never became ready ({last?.Message ?? "surface never sized"})");
        throw last ?? new InvalidOperationException("Vulkan surface never became ready.");
    }

    // Pumps SDL events for roughly the given duration so the platform's surface/lifecycle events are drained.
    // Sleeps between pumps (SDL_Delay), never spins.
    private static void PumpFor(uint ms)
    {
        var deadline = GetTicks() + ms;
        do { PumpEvents(); Delay(16); } while (GetTicks() < deadline);
    }
}
