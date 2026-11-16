using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// A retained frame: on a software rasteriser the window's frame is drawn into ONE image of the context's own, and each
// swapchain image is brought up to date from it with a copy.
//
// Why. Damage is kept per swapchain image (VulkanContext.Damage.cs), since an acquired image holds the frame of two or
// three presents ago. A frame the app repaints in full therefore leaves every OTHER image owing a full repaint on its
// turn, and the app's next partial frames (a clock's tick) are drawn in full until each image has been painted once.
// On a GPU that is invisible. On a software rasteriser a full frame is processor time on every core, and it multiplies
// the cost of each one by the image count: a consumer's sky atlas, which redraws for the sky's drift once in ten
// seconds, sat at 10 percent of a core where two full frames in ten seconds should cost under 3.
//
// How. The image stands where the swapchain images stood in the swapchain arrays, once for every index, as an HDR
// context's SDR layers and a layered window's image do, so the views, the framebuffers, the passes, the readback and
// the damage path build against it unchanged. It is the same image every frame, so it always holds the previous frame
// and what must be repainted into it is THIS frame's damage and nothing more. EndFrame then copies, into the swapchain
// image acquired, what that image is missing: its own accumulated damage, which is a rectangle copy however much of it
// there is, and the whole surface the first time or after a full frame.
//
// What it costs: one image of the window's size, and a copy per frame of what changed. That is why it is for software
// rasterisers, where a copy is a memcpy and a repaint is the whole frame; a GPU keeps the swapchain path it had.
// SDLVK_RETAINED_FRAME=1 asks for it on any device (how the path is run under a validation layer on a GPU), and =0
// refuses it everywhere.
//
// Not for a transparent window, an HDR target or a layered window: each already composes its frame another way.
public sealed unsafe partial class VulkanContext
{
    private bool _retainedActive;
    private VkImage _retainedImage;
    private VkDeviceMemory _retainedMemory;
    // The swapchain's own images, while _swapchainImages holds the retained image at every index.
    private VkImage[] _retainedPresentImages = [];
    // What each swapchain image is missing of the retained frame.
    private readonly SwapchainDamage _presentDamage = new SwapchainDamage();

    /// <summary>
    /// Whether this context draws its frame into one retained image and copies it to the swapchain: true on a software
    /// rasteriser whose surface lets a swapchain image be copied into (see <c>VulkanContext.Retained.cs</c>).
    /// </summary>
    public bool RetainsFrame => _retainedActive;

    /// <summary>
    /// Whether a context takes the retained frame: asked for (<paramref name="setting"/> "1"), or on a software
    /// rasteriser unless refused ("0"); and only for an opaque SDR swapchain whose images may be a copy's destination.
    /// </summary>
    internal static bool ChooseRetainedFrame(bool softwareRasterizer, bool opaqueSdrSwapchain, bool transferDstOffered, string? setting)
        => opaqueSdrSwapchain && transferDstOffered && setting != "0" && (softwareRasterizer || setting == "1");

    /// <summary>
    /// What the swapchain image <paramref name="imageIndex"/> must be given of the retained frame just painted, and
    /// marks it current: false for all of it. A frame painted in full changed everything whatever was declared, so
    /// every swapchain image then owes all of it.
    /// </summary>
    internal static bool TakePresentRegion(SwapchainDamage presentDamage, int imageIndex, bool frameWasPartial,
        out float x, out float y, out float width, out float height)
    {
        if (!frameWasPartial) presentDamage.MarkFull();
        return presentDamage.TryTake(imageIndex, out x, out y, out width, out height);
    }

    private bool WantsRetainedFrame(in VkSurfaceCapabilitiesKHR caps, bool hdr)
        => ChooseRetainedFrame(_dev.IsSoftwareRasterizer, opaqueSdrSwapchain: !_wantsAlpha && !hdr,
            (caps.supportedUsageFlags & VkImageUsageFlags.TransferDst) != 0,
            Environment.GetEnvironmentVariable("SDLVK_RETAINED_FRAME"));

    // After CreateSwapchainImages: the swapchain's images move aside and the retained image takes every place in the
    // swapchain array, so all that follows builds against it.
    private void AdoptRetainedFrame(VkExtent2D extent, uint imageCount)
    {
        _retainedPresentImages = _swapchainImages;
        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = _dev.ColorFormat,
            extent = new VkExtent3D(extent.width, extent.height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            // Drawn into (or resolved into) as a swapchain image is, and copied out of: to the swapchain every frame, and
            // for the DEBUG inspector's capture, which takes it as it takes a swapchain image.
            usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&imageCI, null, out _retainedImage).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_retainedImage, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _retainedMemory).CheckResult();
        DeviceApi.vkBindImageMemory(_retainedImage, _retainedMemory, 0).CheckResult();

        var images = new VkImage[imageCount];
        Array.Fill(images, _retainedImage);
        _swapchainImages = images;
        _presentDamage.Reset((int)imageCount);
        _retainedActive = true;
        SdlVulkanLog.Logger.FrameRetained(_dev.Name, imageCount);
    }

    // With the swapchain, after its views and framebuffers are gone. Not DeferDestroy, for the reason
    // CleanupHdrSwapchain gives: every caller has drained the queue.
    private void CleanupRetainedFrame()
    {
        if (!_retainedActive) return;
        DeviceApi.vkDestroyImage(_retainedImage);
        DeviceApi.vkFreeMemory(_retainedMemory);
        _retainedImage = VkImage.Null;
        _retainedMemory = VkDeviceMemory.Null;
        _retainedPresentImages = [];
        _retainedActive = false;
    }

    // In the frame's own command buffer, after the main pass and the capture: bring the acquired swapchain image up
    // to date from the retained frame and leave both as they were found (the retained image in the pass's final
    // layout, which the next frame's loading pass starts from; the swapchain image ready to present).
    private void RecordRetainedCopy(VkCommandBuffer cmd)
    {
        var idx = (int)_currentImageIndex;
        var present = _retainedPresentImages[idx];
        var finished = _dev.ColorFinalLayout;

        var whole = !TakePresentRegion(_presentDamage, idx, LastFrameWasPartial, out var dx, out var dy, out var dw, out var dh);
        var region = whole ? new VkRect2D(0, 0, SwapchainWidth, SwapchainHeight) : ClampToSwapchain(dx, dy, dw, dh);
        if (region.extent.width == 0 || region.extent.height == 0)
        {
            whole = true;
            region = new VkRect2D(0, 0, SwapchainWidth, SwapchainHeight);
        }
        // What this queue will copy (ImageTransferGranularity): a queue that reports (0,0,0), as Mesa's dzn does, takes
        // whole images only, and vkCmdCopyImage is held to that as the atlases' uploads are
        // (VUID-vkCmdCopyImage-srcOffset-01783). Widening is safe: the retained image is the whole frame.
        var granularity = MinImageTransferGranularity;
        var snapped = ImageTransferGranularity.Snap(
            new CopyRect(region.offset.x, region.offset.y, (int)region.extent.width, (int)region.extent.height),
            granularity.width, granularity.height, (int)SwapchainWidth, (int)SwapchainHeight);
        region = new VkRect2D(snapped.X, snapped.Y, (uint)snapped.Width, (uint)snapped.Height);
        whole |= region.extent.width == SwapchainWidth && region.extent.height == SwapchainHeight;

        TransitionImageLayout(cmd, _retainedImage, finished, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.TransferRead,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.Transfer);
        // From Undefined only when all of it is written: that layout throws the image's contents away, and a partial
        // copy keeps what is outside its rectangle. An image is whole the first time it is acquired (its damage starts
        // full), so a kept image has been presented and is in the present layout. The source stage is the one the
        // submit waits for the acquire at, which is what orders this transition after the image is ours.
        TransitionImageLayout(cmd, present, whole ? VkImageLayout.Undefined : VkImageLayout.PresentSrcKHR,
            VkImageLayout.TransferDstOptimal,
            VkAccessFlags.None, VkAccessFlags.TransferWrite,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.Transfer);

        VkImageSubresourceLayers layers = new(VkImageAspectFlags.Color, 0, 0, 1);
        VkImageCopy copy = new()
        {
            srcSubresource = layers,
            srcOffset = new VkOffset3D(region.offset.x, region.offset.y, 0),
            dstSubresource = layers,
            dstOffset = new VkOffset3D(region.offset.x, region.offset.y, 0),
            extent = new VkExtent3D(region.extent.width, region.extent.height, 1)
        };
        DeviceApi.vkCmdCopyImage(cmd, _retainedImage, VkImageLayout.TransferSrcOptimal,
            present, VkImageLayout.TransferDstOptimal, 1, &copy);

        TransitionImageLayout(cmd, present, VkImageLayout.TransferDstOptimal, VkImageLayout.PresentSrcKHR,
            VkAccessFlags.TransferWrite, VkAccessFlags.None,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ColorAttachmentOutput);
        TransitionImageLayout(cmd, _retainedImage, VkImageLayout.TransferSrcOptimal, finished,
            VkAccessFlags.TransferRead, VkAccessFlags.ColorAttachmentRead | VkAccessFlags.ColorAttachmentWrite,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ColorAttachmentOutput);
    }
}
