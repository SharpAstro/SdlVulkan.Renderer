namespace SdlVulkan.Renderer;

/// <summary>
/// How a transparent window's frames reach the screen with their alpha (<see cref="VulkanContext.AlphaPresentation"/>).
/// <see cref="VulkanContext.CreateTransparent"/> and <see cref="VulkanContext.CreateTransparentForSharedDevice"/>
/// take the first the machine can do, in the order listed here.
/// </summary>
public enum AlphaPresentation
{
    /// <summary>
    /// They do not: an ordinary window's context, or a transparent window's whose swapchain is <c>OPAQUE</c> because
    /// its device's surface offers no premultiplied alpha and the platform has no layered windows.
    /// <see cref="SdlVulkanWindow.SetShape"/> still cuts the window to a hard-edged silhouette.
    /// </summary>
    None,

    /// <summary>
    /// The window's own swapchain carries it, with <c>PRE_MULTIPLIED</c> composite alpha: on the native device where
    /// its surface offers that (Wayland, macOS and Android drivers usually do), otherwise on another device that does
    /// (on Windows, Mesa's Dozen; see <see cref="VulkanDevice.CreateForTransparentSurface"/>).
    /// </summary>
    Swapchain,

    /// <summary>
    /// Windows, when the window's device offers no premultiplied alpha: the frame is drawn as usual into an image of
    /// the context's own, read back after every frame and handed to the window as a layered window's bitmap
    /// (<c>UpdateLayeredWindow</c> with per-pixel alpha). The window manager blends it, and clicks pass through
    /// wherever alpha is zero, with or without a shape. The device stays the native one, so with
    /// <see cref="SdlVulkanApp.Device"/> the window shares the other windows' resources. The cost is the wait for the
    /// GPU, the readback and the hand-over, all inside <c>EndFrame</c> and growing with the window's size: a 240 by 240
    /// window took 0.3 ms a frame from <c>BeginFrame</c> to <c>EndFrame</c> on an Adreno X1-85, drawing included,
    /// against 0.09 ms for Dozen's swapchain on the same GPU.
    /// </summary>
    LayeredWindow,
}
