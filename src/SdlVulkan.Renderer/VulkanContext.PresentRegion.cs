using System;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

// Incremental present (VK_KHR_incremental_present): each present says which rectangle of the image changed since the
// last present, so the presentation engine moves that and not the whole image.
//
// Why. On a software rasteriser under X11 (llvmpipe and lavapipe; a Raspberry Pi has nothing else) Mesa's present
// sends the image to the X server with XPutImage, and given no rectangles it sends ALL of it: a clock's tick on an
// idle window put eight megabytes of a 1080p window through the X socket and the server's own copy, five times a
// second, to change one run of text (Mesa 25.2, wsi_common_x11.c, x11_present_to_x11_sw). Given the rectangles, it
// sends their rows and nothing else. Elsewhere the same rectangles are the surface damage a Wayland compositor and a
// DRI3 X server are handed, which are hints they may use.
//
// WHAT changed is not what the frame repainted. Damage is kept per swapchain image (VulkanContext.Damage.cs) because
// an acquired image holds the frame of two or three presents ago, and the retained frame copies each swapchain image
// what IT is missing (VulkanContext.Retained.cs). The presentation engine holds the LAST present, so what a present
// must name is everything declared since that present: this frame's damage, plus that of any frame that never reached
// the screen. Hence a third record, of one slot, filled by the same declarations and taken only by a present that is
// made. Its bounding box is one rectangle, as the per-image ones are.
//
// Whole, by passing nothing, whenever the rectangle cannot be trusted: before the first present of a swapchain, after a
// frame declared full or declared nothing, on an HDR frame (composited whole), and on a swapchain whose transform is
// not the identity (a rectangle is in the image's own coordinates there, which this does not rotate).
public sealed unsafe partial class VulkanContext
{
    // What changed since the last present, one slot (SwapchainDamage's image 0). Whole until a swapchain's first present.
    private readonly SwapchainDamage _changedSincePresent = ChangedSincePresentWhole();

    // Whether the swapchain was made with the identity transform, the one in which a present rectangle is simply the
    // rectangle that was drawn (VulkanContext.cs, CreateSwapchain: identity wherever the surface supports it).
    internal bool _swapchainTransformIsIdentity;

    /// <summary>
    /// The rectangle the last present named as changed, in swapchain pixels; null when it presented the whole image
    /// (incremental present unavailable, or nothing narrower could be said).
    /// </summary>
    public VkRect2D? LastPresentRegion { get; internal set; }

    private static SwapchainDamage ChangedSincePresentWhole()
    {
        var changed = new SwapchainDamage();
        changed.Reset(1);
        return changed;
    }

    /// <summary>
    /// What a present names as changed: takes everything declared since the last present (and so clears it, which only a
    /// present that is made may do) and answers the rectangle, rounded out and clamped to the image, as the frame's
    /// region is. False for the whole image: <paramref name="allowed"/> is false, the declarations were full or there
    /// were none, or the rectangle falls outside the image.
    /// </summary>
    internal static bool TakeChangedRegion(SwapchainDamage changed, bool allowed, uint swapchainWidth, uint swapchainHeight,
        out VkRectLayerKHR rect)
    {
        rect = default;
        var partial = changed.TryTake(0, out var x, out var y, out var width, out var height);
        if (!partial || !allowed)
        {
            return false;
        }

        var region = RoundOutClamped(x, y, width, height, swapchainWidth, swapchainHeight);
        if (region.extent.width == 0 || region.extent.height == 0)
        {
            return false;
        }

        rect = new VkRectLayerKHR { offset = region.offset, extent = region.extent, layer = 0 };
        return true;
    }

    // The wiring the present uses, with the device's offer passed in so a test on an offscreen device can drive it.
    internal bool TakeChangedRegionForPresent(bool deviceOffersIt, out VkRectLayerKHR rect)
        => TakeChangedRegion(_changedSincePresent, deviceOffersIt && _swapchainTransformIsIdentity && !_hdrActive,
            SwapchainWidth, SwapchainHeight, out rect);

    // A present that failed (anything but success or suboptimal) may not have shown what it was given, so the rectangle
    // it took is owed again: the next present is whole.
    internal void NotePresentResult(VkResult presentResult)
    {
        if (presentResult != VkResult.Success && presentResult != VkResult.SuboptimalKHR) _changedSincePresent.MarkFull();
    }

    // Chained onto the present it names. The three structs live on the caller's stack, so they outlive the call.
    internal void NamePresentRegion(ref VkPresentInfoKHR presentInfo, VkRectLayerKHR* rect, VkPresentRegionKHR* region,
        VkPresentRegionsKHR* regions, bool deviceOffersIt)
    {
        if (!TakeChangedRegionForPresent(deviceOffersIt, out *rect))
        {
            LastPresentRegion = null;
            return;
        }

        *region = new VkPresentRegionKHR { rectangleCount = 1, pRectangles = rect };
        *regions = new VkPresentRegionsKHR { swapchainCount = 1, pRegions = region };
        presentInfo.pNext = regions;
        LastPresentRegion = new VkRect2D(rect->offset, rect->extent);
    }
}
