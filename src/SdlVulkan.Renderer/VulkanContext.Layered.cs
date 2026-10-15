using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// A transparent window whose device offers no premultiplied alpha on its surface, on Windows: the third way a
// transparent window gets its alpha (AlphaPresentation.LayeredWindow). The frame is drawn as usual, between BeginFrame
// and EndFrame, into an image of the context's own where a swapchain image would be. SubmitFrame copies it into a
// host-visible buffer in the same command buffer, waits for the frame's fence and hands the pixels to the window
// (LayeredWindow: UpdateLayeredWindow with per-pixel alpha). There is no acquire, no present and no semaphore; the
// rest (the fence ledger, dropped frames, damage, recovery, resize) is the swapchain path's, unchanged, which is why
// the image stands in the swapchain's arrays rather than going through the offscreen path.
//
// Synchronous on purpose. The event loop draws only when something changed, so a frame shown at the next frame would
// leave the last change off screen until another came along. The wait is the frame's own GPU time, short for the
// small windows this is for, and bounded like every other wait on the render thread.
public sealed unsafe partial class VulkanContext
{
    private readonly ILayeredTarget? _layered;
    private VkImage _layeredImage;
    private VkDeviceMemory _layeredImageMemory;
    // Host-visible, mapped for the context's life, one frame's size. One is enough: each frame's copy has been read
    // before the next frame is recorded.
    private VkBuffer _layeredReadback;
    private VkDeviceMemory _layeredReadbackMemory;
    private byte* _layeredReadbackMapped;
    private ulong _layeredReadbackSize;

    /// <summary>
    /// How this context's frames reach the screen with their alpha: <see cref="AlphaPresentation.None"/> for an
    /// ordinary window, and for a transparent one the way it got (see <see cref="CreateTransparent"/>). Fixed when the
    /// context is made; a resize keeps it.
    /// </summary>
    public AlphaPresentation AlphaPresentation { get; private set; }

    /// <summary>
    /// The way a transparent window's context takes, given whether its device's surface offers premultiplied alpha
    /// and whether the platform has layered windows (Windows does). The order is the preference: a swapchain carries
    /// alpha at no cost per frame; a layered window costs a readback per frame, growing with the window; an opaque
    /// swapchain has no alpha at all.
    /// </summary>
    internal static AlphaPresentation ChooseAlphaPresentation(bool deviceOffersPremultipliedAlpha, bool layeredWindows)
        => deviceOffersPremultipliedAlpha ? AlphaPresentation.Swapchain
            : layeredWindows ? AlphaPresentation.LayeredWindow
            : AlphaPresentation.None;

    // The target a transparent window's context draws for when it takes the layered way, else null. Logs the way
    // when it is not the swapchain's, naming the device, since both of the others cost something the log should
    // explain: a readback per frame, or the alpha itself.
    private static ILayeredTarget? LayeredTargetFor(VulkanDevice device, SdlVulkanWindow window)
    {
        var way = ChooseAlphaPresentation(device.SupportsPremultipliedAlpha(window.Surface), OperatingSystem.IsWindows());
        if (way == AlphaPresentation.LayeredWindow && OperatingSystem.IsWindows())
        {
            SdlVulkanLog.Logger.TransparentWindowLayered(device.Name);
            return new LayeredWindow(window.GetNativeWindowHandle());
        }
        if (way == AlphaPresentation.None)
            SdlVulkanLog.Logger.TransparentWindowOpaque(device.Name);
        return null;
    }

    /// <summary>
    /// A layered context on <paramref name="device"/> drawing for <paramref name="target"/>, with no window or surface:
    /// what the tests drive the layered frame path through, on the offscreen device the suite runs on.
    /// </summary>
    internal static VulkanContext CreateLayeredForSharedDevice(VulkanDevice device, ILayeredTarget target,
        uint vertexBufferSize = 4 * 1024 * 1024)
        => CreateShared(device, VkSurfaceKHR.Null, 0, 0, vertexBufferSize, wantsAlpha: true, target);

    // What a swapchain reports as out of date: the window is no longer the size of the image.
    private bool LayeredWindowResized() => _layered!.PixelSize != (SwapchainWidth, SwapchainHeight);

    // CreateSwapchain's images for a layered context: one, the context's own. Returns the count, as
    // CreateSwapchainImages does for a swapchain.
    private uint CreateLayeredImage(VkFormat format, VkExtent2D extent)
    {
        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D(extent.width, extent.height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            // Drawn into, or resolved into under MSAA, as a swapchain image is; copied out of for the window (and for
            // the DEBUG inspector's capture, which takes it as it takes a swapchain image).
            usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&imageCI, null, out _layeredImage).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_layeredImage, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _layeredImageMemory).CheckResult();
        DeviceApi.vkBindImageMemory(_layeredImage, _layeredImageMemory, 0).CheckResult();

        _swapchainImages = [_layeredImage];
        // Nothing is presented, so nothing waits on a present.
        _renderFinishedSemaphores = [];
        EnsureLayeredReadback((ulong)extent.width * extent.height * 4);
        AlphaPresentation = AlphaPresentation.LayeredWindow;
        return 1;
    }

    private void CleanupLayeredImage()
    {
        if (_layeredImage != VkImage.Null)
        {
            DeviceApi.vkDestroyImage(_layeredImage);
            _layeredImage = VkImage.Null;
        }
        if (_layeredImageMemory != VkDeviceMemory.Null)
        {
            DeviceApi.vkFreeMemory(_layeredImageMemory);
            _layeredImageMemory = VkDeviceMemory.Null;
        }
    }

    // Remade only when the window's size changes. Reached from CreateSwapchain, after the drain every rebuild makes,
    // so no copy into the old buffer is still running.
    private void EnsureLayeredReadback(ulong size)
    {
        if (_layeredReadback != VkBuffer.Null && _layeredReadbackSize == size)
            return;
        CleanupLayeredReadback();

        VkBufferCreateInfo bufferCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateBuffer(&bufferCI, null, out _layeredReadback).CheckResult();
        DeviceApi.vkGetBufferMemoryRequirements(_layeredReadback, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _dev.FindReadbackMemoryType(memReqs.memoryTypeBits)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _layeredReadbackMemory).CheckResult();
        DeviceApi.vkBindBufferMemory(_layeredReadback, _layeredReadbackMemory, 0).CheckResult();
        void* mapped;
        DeviceApi.vkMapMemory(_layeredReadbackMemory, 0, size, 0, &mapped).CheckResult();
        _layeredReadbackMapped = (byte*)mapped;
        _layeredReadbackSize = size;
    }

    private void CleanupLayeredReadback()
    {
        if (_layeredReadback == VkBuffer.Null)
            return;
        DeviceApi.vkUnmapMemory(_layeredReadbackMemory);
        DeviceApi.vkDestroyBuffer(_layeredReadback);
        DeviceApi.vkFreeMemory(_layeredReadbackMemory);
        _layeredReadback = VkBuffer.Null;
        _layeredReadbackMemory = VkDeviceMemory.Null;
        _layeredReadbackMapped = null;
        _layeredReadbackSize = 0;
    }

    // Called from SubmitFrame after vkCmdEndRenderPass, as RecordPresentCapture is: the frame's image, finished,
    // copied into the readback buffer, and left where the next frame's pass expects to find it.
    private void RecordLayeredReadback(VkCommandBuffer cmd)
    {
        var image = _swapchainImages[_currentImageIndex];
        var finished = _dev.ColorFinalLayout;
        TransitionImageLayout(cmd, image,
            finished, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.TransferRead,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.Transfer);

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,   // rows tightly packed, top-down: what a top-down 32-bit DIB is
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D(SwapchainWidth, SwapchainHeight, 1)
        };
        DeviceApi.vkCmdCopyImageToBuffer(cmd, image, VkImageLayout.TransferSrcOptimal, _layeredReadback, 1, &region);

        // The next frame draws into this image: its pass's colour write waits for this copy's read.
        TransitionImageLayout(cmd, image,
            VkImageLayout.TransferSrcOptimal, finished,
            VkAccessFlags.TransferRead, VkAccessFlags.ColorAttachmentWrite,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ColorAttachmentOutput);

        // And the copy made visible to the host, which reads the buffer once the frame's fence has signalled.
        VkBufferMemoryBarrier toHost = new()
        {
            srcAccessMask = VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.HostRead,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            buffer = _layeredReadback,
            offset = 0,
            size = VK_WHOLE_SIZE
        };
        DeviceApi.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, 0,
            0, null, 1, &toHost, 0, null);
    }

    // SubmitFrame's present, for a layered context whose frame was submitted under the fence of `frame`.
    private void PresentLayered(int frame)
    {
        var fence = _inFlightFences[frame];
        var waitResult = DeviceApi.vkWaitForFences(1, &fence, true, FenceWaitTimeoutNs);
        NoteDeviceLost(waitResult, "vkWaitForFences(layered window)");
        // The frame is on the queue and its fence still says when it is done; it is only not shown. Thrown as
        // BeginFrame's fence wait throws it, so the event loop retries without tearing anything down, and the frame
        // it draws then is the one shown.
        if (waitResult == VkResult.Timeout)
            throw new VkException(waitResult, "layered window frame did not finish in time; it was not shown");
        waitResult.CheckResult();

        // A false here is the window having changed size since the frame began, which the next BeginFrame reports,
        // or the system refusing it, which the target has logged; either way the window keeps what it showed.
        _layered!.Present(new ReadOnlySpan<byte>(_layeredReadbackMapped, (int)_layeredReadbackSize),
            SwapchainWidth, SwapchainHeight);
    }

    // From Dispose, after CleanupSwapchain has destroyed the image.
    private void CleanupLayered()
    {
        CleanupLayeredReadback();
        _layered?.Dispose();
    }
}
