#if DEBUG
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

// DEBUG-only swapchain capture for the live UI debug inspector (see DebugInspector.cs).
//
// The capture is recorded INTO THE FRAME'S OWN command buffer, after vkCmdEndRenderPass (the image
// is in PresentSrcKHR, the render pass's finalLayout) and before vkEndCommandBuffer -- while this
// process still owns the acquired image. The readback is then consumed at the top of the BeginFrame
// that waits the same fence index, exactly the ThumbnailCapture pattern (VulkanContext.ThumbnailCapture.cs):
// no extra submit, no extra fence, no GPU wait beyond the one BeginFrame already performs.
//
// This REPLACED a post-present one-shot readback that transitioned the just-presented image without
// re-acquiring it. That violated the swapchain ownership contract twice over -- the validation layer
// reported a WRITE_AFTER_PRESENT hazard plus "layout transition on a presentable image that has not
// been acquired" on every single screenshot -- and a barrier against an image the presentation
// engine still owns is a license for the driver to park the whole queue behind it. On the Adreno
// X1-85 a parked queue is indistinguishable from the stuck-fence wedge PR #80's abandon work made
// survivable, and the readback ran at exactly the wedge-shaped moment: between frames, right after
// a present. A screenshot must never be able to wedge the app it is observing.
public sealed unsafe partial class VulkanContext
{
    // Pending-capture state. Render thread ONLY, like the frame state machine it rides on.
    private bool _presentCaptureRequested;    // a capture should be recorded into the next presented frame
    private bool _presentCapturePending;      // a copy is recorded, awaiting its frame fence
    private int _presentCapturePendingIndex;  // frame-fence index the recorded copy rides
    private uint _presentCaptureW;            // extent of the recorded copy (swapchain size at record time)
    private uint _presentCaptureH;
    private VkBuffer _presentCaptureBuffer;   // host-visible readback buffer, persistent, grown on demand
    private VkDeviceMemory _presentCaptureMemory;
    private ulong _presentCaptureCapacity;
    private byte[]? _presentCaptureReadyRgba; // finished RGBA snapshot awaiting TryTakePresentCapture
    private uint _presentCaptureReadyW;
    private uint _presentCaptureReadyH;

    /// <summary>True while the in-flight fence wait is timing out (the GPU is late or stuck). The
    /// inspector reads this to fail a screenshot with a structured error instead of queueing more
    /// work behind a fence that is not signalling.</summary>
    internal bool GpuFenceStuck => _fenceWaitStuck;

    /// <summary>
    /// Asks the NEXT presented frame to capture itself. Render thread only. The result arrives via
    /// <see cref="TryTakePresentCapture"/> once that frame's fence has been waited (typically two
    /// frames later); the caller keeps the window redrawing until then. Any stale unconsumed snapshot
    /// is dropped so a new request can never answer with an older frame.
    /// </summary>
    internal void RequestPresentCapture()
    {
        AssertFrameThread(nameof(RequestPresentCapture));
        _presentCaptureReadyRgba = null;
        _presentCaptureRequested = true;
    }

    /// <summary>
    /// Hands over the finished capture (RGBA, top-to-bottom rows) and clears the ready state.
    /// Returns false while no capture has completed.
    /// </summary>
    internal bool TryTakePresentCapture(out byte[] rgba, out uint width, out uint height)
    {
        if (_presentCaptureReadyRgba is not { } ready)
        {
            rgba = [];
            width = 0;
            height = 0;
            return false;
        }
        rgba = ready;
        width = _presentCaptureReadyW;
        height = _presentCaptureReadyH;
        _presentCaptureReadyRgba = null;
        return true;
    }

    // Called from SubmitFrame after vkCmdEndRenderPass, before vkEndCommandBuffer, so the copy is
    // ordered after all rendering and before the present that releases the image -- the only window
    // in which touching a swapchain image is legal. An aborted frame that closed its render pass
    // captures too (a partially-drawn debug screenshot beats a stepped operation that never
    // completes); a frame that died before BeginRenderPass skips, and the request carries over.
    partial void RecordPresentCapture(VkCommandBuffer cmd)
    {
        if (!_presentCaptureRequested || _presentCapturePending || _isOffscreen)
            return;

        var width = SwapchainWidth;
        var height = SwapchainHeight;
        var size = (ulong)width * height * 4;
        if (size == 0)
            return;

        EnsurePresentCaptureBuffer(size);

        var image = _swapchainImages[_currentImageIndex];

        TransitionImageLayout(cmd, image,
            VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.TransferRead,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.Transfer);

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D(width, height, 1)
        };
        DeviceApi.vkCmdCopyImageToBuffer(cmd, image, VkImageLayout.TransferSrcOptimal,
            _presentCaptureBuffer, 1, &region);

        TransitionImageLayout(cmd, image,
            VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR,
            VkAccessFlags.TransferRead, VkAccessFlags.MemoryRead,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.BottomOfPipe);

        _presentCaptureW = width;
        _presentCaptureH = height;
        _presentCapturePending = true;
        _presentCapturePendingIndex = _currentFrame; // SubmitFrame submits under _inFlightFences[_currentFrame]
        _presentCaptureRequested = false;
    }

    // Mirror of the thumbnail cancellation in SubmitFrame's rejected-submit branch: the copy recorded
    // into this frame died with it, so nothing will ever write the buffer. Re-arm the request rather
    // than just clearing it -- the next frame that does submit records a fresh capture, and the
    // inspector's stepped screenshot completes instead of waiting on a capture that no longer exists.
    partial void CancelPresentCaptureOnRejectedSubmit()
    {
        if (_presentCapturePending && _presentCapturePendingIndex == _currentFrame)
        {
            _presentCapturePending = false;
            _presentCaptureRequested = true;
        }
    }

    // Called from BeginFrame immediately after the in-flight fence wait (and before the reset), like
    // ConsumeThumbnailReadback: if the recorded copy rode the fence index that was just waited, its
    // GPU work is complete and the buffer can be snapshotted with no extra GPU wait.
    partial void ConsumePresentCaptureReadback()
    {
        if (!_presentCapturePending || _presentCapturePendingIndex != _currentFrame)
            return;

        var pixelCount = (int)(_presentCaptureW * _presentCaptureH);
        var size = (ulong)(pixelCount * 4);

        void* mapped;
        DeviceApi.vkMapMemory(_presentCaptureMemory, 0, size, 0, &mapped);
        var rgba = new byte[pixelCount * 4];
        var src = new Span<byte>(mapped, pixelCount * 4);
        for (var i = 0; i < pixelCount; i++)
        {
            rgba[i * 4 + 0] = src[i * 4 + 2]; // R <- B (swapchain is B8G8R8A8)
            rgba[i * 4 + 1] = src[i * 4 + 1]; // G
            rgba[i * 4 + 2] = src[i * 4 + 0]; // B <- R
            rgba[i * 4 + 3] = src[i * 4 + 3]; // A
        }
        DeviceApi.vkUnmapMemory(_presentCaptureMemory);

        _presentCaptureReadyRgba = rgba;
        _presentCaptureReadyW = _presentCaptureW;
        _presentCaptureReadyH = _presentCaptureH;
        _presentCapturePending = false;
    }

    partial void CleanupPresentCapture()
    {
        if (_presentCaptureBuffer != VkBuffer.Null)
        {
            DeviceApi.vkDestroyBuffer(_presentCaptureBuffer);
            DeviceApi.vkFreeMemory(_presentCaptureMemory);
            _presentCaptureBuffer = VkBuffer.Null;
            _presentCaptureMemory = VkDeviceMemory.Null;
            _presentCaptureCapacity = 0;
        }
        _presentCaptureRequested = false;
        _presentCapturePending = false;
        _presentCaptureReadyRgba = null;
    }

    private void EnsurePresentCaptureBuffer(ulong size)
    {
        if (_presentCaptureBuffer != VkBuffer.Null && _presentCaptureCapacity >= size)
            return;

        if (_presentCaptureBuffer != VkBuffer.Null)
        {
            // Safe to destroy: a pending copy into this buffer is refused above (_presentCapturePending
            // gates RecordPresentCapture), so the buffer can only be idle here.
            DeviceApi.vkDestroyBuffer(_presentCaptureBuffer);
            DeviceApi.vkFreeMemory(_presentCaptureMemory);
        }

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateBuffer(&bufCI, null, out _presentCaptureBuffer).CheckResult();
        DeviceApi.vkGetBufferMemoryRequirements(_presentCaptureBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _presentCaptureMemory).CheckResult();
        DeviceApi.vkBindBufferMemory(_presentCaptureBuffer, _presentCaptureMemory, 0);
        _presentCaptureCapacity = size;
    }
}
#endif
