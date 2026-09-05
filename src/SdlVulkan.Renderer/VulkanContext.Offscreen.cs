using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// Offscreen rendering path — single VkImage render target, no surface, no swapchain.
// Useful for headless tests, thumbnail/raster workers, and CI without a display server.
// BeginOffscreenFrame/EndOffscreenFrame are drop-in replacements for BeginFrame/EndFrame,
// so VkRenderer and higher-level consumers work unchanged. After EndOffscreenFrame completes
// (it blocks on the frame fence), call ReadbackOffscreenRgba to pull the pixels out.
public sealed unsafe partial class VulkanContext
{
    // Offscreen render target — format matches the swapchain path (B8G8R8A8Unorm) so render
    // pass compatibility with VkPipelineSet's pre-baked pipelines is preserved.
    private VkImage _offscreenImage;
    private VkDeviceMemory _offscreenMemory;
    private VkImageView _offscreenImageView;
    private VkFramebuffer _offscreenFramebuffer;
    private uint _offscreenWidth;
    private uint _offscreenHeight;
    private bool _isOffscreen;

    public bool IsOffscreen => _isOffscreen;
    public uint OffscreenWidth => _offscreenWidth;
    public uint OffscreenHeight => _offscreenHeight;
    public VkFormat OffscreenFormat => VkFormat.B8G8R8A8Unorm;

    /// <summary>
    /// Creates a VulkanContext that renders to a single offscreen VkImage instead of a
    /// swapchain. No VkSurfaceKHR, no SDL window, no VK_KHR_swapchain required at runtime
    /// (though the extension is still advertised on the device — every modern GPU has it,
    /// and skipping it would complicate this factory's physical-device pick).
    /// </summary>
    public static VulkanContext CreateOffscreen(VkInstance instance, uint width, uint height,
        uint vertexBufferSize = 4 * 1024 * 1024, VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1)
    {
        // Headless device (no surface, no swapchain extension). The offscreen context owns it.
        var device = VulkanDevice.CreateOffscreen(instance, msaaSamples);
        var ctx = new VulkanContext(device, VkSurfaceKHR.Null, vertexBufferSize, ownsDevice: true);

        ctx._isOffscreen = true;
        ctx._offscreenWidth = width;
        ctx._offscreenHeight = height;

        ctx.CreateSyncObjects();
        ctx.AllocateCommandBuffers();
        ctx.CreateVertexBuffers();
        ctx.CreateOffscreenTarget(width, height);

        return ctx;
    }

    private void CreateOffscreenTarget(uint width, uint height)
    {
        // Main readback image (also the resolve target under MSAA).
        VkImageCreateInfo imgCI = new()
        {
            imageType = VkImageType.Image2D,
            format = OffscreenFormat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&imgCI, null, out _offscreenImage).CheckResult();

        DeviceApi.vkGetImageMemoryRequirements(_offscreenImage, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _offscreenMemory).CheckResult();
        DeviceApi.vkBindImageMemory(_offscreenImage, _offscreenMemory, 0).CheckResult();

        var viewCI = new VkImageViewCreateInfo(
            _offscreenImage, VkImageViewType.Image2D, OffscreenFormat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&viewCI, null, out _offscreenImageView).CheckResult();

        // MSAA attachment — allocated via the _msaaImage/_msaaMemory/_msaaImageView fields the
        // swapchain path also uses (we're not using the swapchain, so they're free).
        if (MsaaSamples != VkSampleCountFlags.Count1)
        {
            VkImageCreateInfo msaaImgCI = new()
            {
                imageType = VkImageType.Image2D,
                format = OffscreenFormat,
                extent = new VkExtent3D(width, height, 1),
                mipLevels = 1,
                arrayLayers = 1,
                samples = MsaaSamples,
                tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransientAttachment,
                sharingMode = VkSharingMode.Exclusive
            };
            DeviceApi.vkCreateImage(&msaaImgCI, null, out _msaaImage).CheckResult();

            DeviceApi.vkGetImageMemoryRequirements(_msaaImage, out var msaaMemReqs);
            VkMemoryAllocateInfo msaaAlloc = new()
            {
                allocationSize = msaaMemReqs.size,
                memoryTypeIndex = FindTransientMemoryType(msaaMemReqs.memoryTypeBits)
            };
            DeviceApi.vkAllocateMemory(&msaaAlloc, null, out _msaaMemory).CheckResult();
            DeviceApi.vkBindImageMemory(_msaaImage, _msaaMemory, 0).CheckResult();

            var msaaViewCI = new VkImageViewCreateInfo(
                _msaaImage, VkImageViewType.Image2D, OffscreenFormat,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&msaaViewCI, null, out _msaaImageView).CheckResult();
        }

        // Depth attachment — the swapchain path's fields, free here for the same reason the MSAA ones are.
        CreateDepthAttachment(width, height, out _depthImage, out _depthMemory, out _depthImageView);

        // Framebuffer: colour (multisample under MSAA, else the readback image), depth, and under MSAA
        // the readback image as the resolve target.
        var msaa = MsaaSamples != VkSampleCountFlags.Count1;
        _offscreenFramebuffer = CreateCompatibleFramebuffer(RenderPass,
            colorView: msaa ? _msaaImageView : _offscreenImageView,
            depthView: _depthImageView,
            resolveView: _offscreenImageView,
            width, height);
    }

    /// <summary>
    /// Recreate the offscreen render target at a new size, keeping the device, command buffers,
    /// sync objects, vertex buffers, and the renderer's font atlases intact — so glyphs stay warm
    /// across differently-sized pages in a multi-page raster/export job (a fresh context per page
    /// would re-rasterize every glyph). Works under MSAA as well as Count1: the resolve target and the
    /// multisampled attachment are both torn down and recreated at the new size.
    /// </summary>
    public void ResizeOffscreen(uint width, uint height)
    {
        if (!_isOffscreen) throw new InvalidOperationException("ResizeOffscreen requires CreateOffscreen");
        if (width == _offscreenWidth && height == _offscreenHeight) return;

        // No in-flight frame may reference the target we are about to destroy. Bounded, and skipped on a
        // known-stuck GPU, for the reason every other drain in this class is: an unbounded
        // vkDeviceWaitIdle here blocks the calling thread forever on a wedged device, and the offscreen
        // path is reached from export — so the export would never return and never say why. Same call
        // the swapchain recreate and surface-loss paths make, and the same trade on timeout: we are
        // about to destroy and recreate this target regardless, so a drain that times out degrades to
        // the rebuild it was already doing instead of a hang.
        //
        // TryDrainDevice, NOT TryWaitPriorFramesIdle: that one deliberately excludes the CURRENT
        // frame's fence, because it exists for a mid-record atlas grow where that fence is reset and
        // unsubmitted. ResizeOffscreen runs BETWEEN frames, where the current index can still hold a
        // pending submit from MaxFramesInFlight frames ago — the frame most likely to be reading the
        // old target. Excluding it would trade this hang for a destroy-while-referenced.
        TryDrainDevice(DrainTimeoutNs, "offscreen resize");
        CleanupOffscreenTarget();
        _offscreenWidth = width;
        _offscreenHeight = height;
        CreateOffscreenTarget(width, height);
    }

    /// <summary>Submit attempts before an offscreen submit rejection is treated as terminal. Small on
    /// purpose: this exists to ride out a transient driver rejection, not to grind against a real
    /// failure, and every attempt after the first is already an anomaly worth surfacing.</summary>
    private const int OffscreenSubmitAttempts = 4;

    /// <summary>
    /// Offscreen counterpart of <see cref="BeginFrame"/>. Waits on the frame fence, resets
    /// the command buffer, and returns it ready for recording. No swapchain acquire.
    /// </summary>
    public VkCommandBuffer BeginOffscreenFrame()
    {
        if (!_isOffscreen) throw new InvalidOperationException("BeginOffscreenFrame requires CreateOffscreen");

        var fence = _inFlightFences[_currentFrame];
        // Skip the wait when nothing is in flight under this index. A fence that was reset for a submit
        // which then failed is unsignaled with nothing behind it to ever signal it, and this wait has NO
        // timeout — so waiting on it is a permanent hang, not a stall. That is exactly what happened
        // before: EndOffscreenFrame threw on a rejected submit without advancing the index, and the next
        // frame parked here forever. The ledger is the authority on whether a wait can be satisfied.
        if (Volatile.Read(ref _submitPending[_currentFrame]) != 0)
        {
            var waitResult = DeviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);
            NoteDeviceLost(waitResult, "vkWaitForFences(offscreen)");
        }
        _frameOrdinal++;
        // Same contract as the swapchain BeginFrame: the wait proves frame (ordinal - MaxFramesInFlight)
        // retired, so deferred destroys scheduled against it run here.
        FlushRetiredDeferredDestroys();

        // Not reset here — EndOffscreenFrame resets it immediately before the submit that signals it,
        // for the same reason as the swapchain path (see the note in BeginFrame). It matters MORE here:
        // this wait is unbounded, so a draw that threw between begin and end would orphan the fence and
        // the next BeginOffscreenFrame would block forever with no timeout to escape through — a
        // permanently hung export/thumbnail thread rather than a recoverable stall.
        var cmd = _commandBuffers[_currentFrame];
        DeviceApi.vkResetCommandBuffer(cmd, 0);
        VkCommandBufferBeginInfo bi = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        DeviceApi.vkBeginCommandBuffer(cmd, &bi);

        _vertexOffset = 0;
        return cmd;
    }

    /// <summary>
    /// Binds the offscreen framebuffer and starts the render pass with a clear.
    /// Mirrors <see cref="BeginRenderPass"/> for the swapchain path.
    /// </summary>
    public void BeginOffscreenRenderPass(VkCommandBuffer cmd, float clearR, float clearG, float clearB, float clearA)
    {
        if (!_isOffscreen) throw new InvalidOperationException("BeginOffscreenRenderPass requires CreateOffscreen");

        Span<VkClearValue> clears = stackalloc VkClearValue[ClearValueCount];
        FillClearValues(clears, clearR, clearG, clearB, clearA);

        fixed (VkClearValue* pClears = clears)
        {
            VkRenderPassBeginInfo rpBI = new()
            {
                renderPass = RenderPass,
                framebuffer = _offscreenFramebuffer,
                renderArea = new VkRect2D(0, 0, _offscreenWidth, _offscreenHeight),
                clearValueCount = ClearValueCount,
                pClearValues = pClears
            };
            DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);
        }

        VkViewport vp = new(0, 0, _offscreenWidth, _offscreenHeight, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, vp);
        VkRect2D sc = new(0, 0, _offscreenWidth, _offscreenHeight);
        DeviceApi.vkCmdSetScissor(cmd, 0, sc);
    }

    /// <summary>
    /// Offscreen counterpart of <see cref="EndFrame"/>. Ends the command buffer, submits,
    /// and blocks on the frame fence (via vkWaitForFences on next Begin, or call
    /// <see cref="WaitOffscreenFrameComplete"/> to wait right now).
    /// </summary>
    public void EndOffscreenFrame(VkCommandBuffer cmd)
    {
        if (!_isOffscreen) throw new InvalidOperationException("EndOffscreenFrame requires CreateOffscreen");

        DeviceApi.vkCmdEndRenderPass(cmd);
        DeviceApi.vkEndCommandBuffer(cmd);

        VkSubmitInfo si = new()
        {
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };
        // No queue-ownership assertion here, deliberately: CreateOffscreen gives this context its OWN
        // device, so this queue is private and can never race a window's frame submit. Successive jobs
        // driven from Task.Run also land on different pool threads, which is legal (they never overlap)
        // and would trip an identity-based check. The invariant that matters — one offscreen context is
        // not used concurrently — belongs to its owner, not here.
        var frameFence = _inFlightFences[_currentFrame];
        DeviceApi.vkResetFences(1, &frameFence);
        var submitResult = SubmitOffscreen(&si, frameFence, "submit(offscreen)");

        if (submitResult == VkResult.Success)
        {
            Volatile.Write(ref _submitOrdinal[_currentFrame], _frameOrdinal);
            Volatile.Write(ref _submitPending[_currentFrame], 1);
            Interlocked.Increment(ref _submitsTotal);
            _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
            return;
        }

        // Nothing is in flight under this index: the fence was reset and the submit did not take. Mark it
        // so, and advance, so that neither this index's next Begin nor a drain can wait on a fence that
        // can never signal. Then fail loudly.
        //
        // Deliberately NOT the swapchain path's dropped-frame degradation. There, a lost frame is one
        // flicker and the next frame corrects it. Here the frame IS the deliverable — an export or a
        // thumbnail — so silently continuing would read back whatever the target happened to hold and
        // hand the caller stale or blank pixels as if they were the page. A caller can retry a throw; it
        // cannot detect a plausible-looking wrong image.
        Volatile.Write(ref _submitPending[_currentFrame], 0);
        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        submitResult.CheckResult();
    }

    /// <summary>Attempts an offscreen queue submit, retrying a rejection rather than degrading.
    /// <para>
    /// Qualcomm Adreno returns <c>VK_ERROR_INITIALIZATION_FAILED</c> from <c>vkQueueSubmit</c>, which is
    /// not a spec-legal return there, and the work does NOT execute (see the long note in
    /// <c>SubmitFrame</c>). The swapchain path answers that by dropping the frame, which it can afford.
    /// This path cannot: its output is the product, so it retries instead — the submit failed, so the
    /// command buffer was never consumed and re-submitting it is the same work, not a duplicate.
    /// </para>
    /// A short backoff between attempts because the rejection is transient and pressure-related; this
    /// runs on an export/capture thread, never the render thread, so a few ms of sleep on a failure path
    /// costs nothing. Returns the last result, so a persistent rejection still surfaces to the caller.
    /// </summary>
    private VkResult SubmitOffscreen(VkSubmitInfo* si, VkFence fence, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = DeviceApi.vkQueueSubmit(GraphicsQueue, 1, si, fence);
            RenderDiag.Vk(what, result, $"attempt={attempt}/{OffscreenSubmitAttempts}");
            NoteDeviceLost(result, what);
            if (result != VkResult.ErrorInitializationFailed) return result;

            Interlocked.Increment(ref _submitsRejected);
            if (attempt >= OffscreenSubmitAttempts) return result;
            Thread.Sleep(attempt);
        }
    }

    /// <summary>Blocks until the most recently submitted offscreen frame completes.</summary>
    public void WaitOffscreenFrameComplete()
    {
        // The previous frame's fence is the one we just submitted against.
        var prevFrame = (_currentFrame + MaxFramesInFlight - 1) % MaxFramesInFlight;
        // ...unless that submit did not take, in which case there is no work to wait for and this
        // unbounded wait would never return. Same reasoning as BeginOffscreenFrame.
        if (Volatile.Read(ref _submitPending[prevFrame]) == 0) return;
        var fence = _inFlightFences[prevFrame];
        var waitResult = DeviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);
        NoteDeviceLost(waitResult, "vkWaitForFences(offscreen complete)");
    }

    /// <summary>
    /// Copies the offscreen image into a freshly-allocated RGBA byte array (R,G,B,A per pixel,
    /// top-to-bottom row order). Blocks until the GPU finishes the copy. Call after
    /// <see cref="WaitOffscreenFrameComplete"/> (or the next BeginOffscreenFrame) to ensure
    /// the render pass above has finished writing the image.
    /// </summary>
    public byte[] ReadbackOffscreenRgba()
    {
        if (!_isOffscreen) throw new InvalidOperationException("ReadbackOffscreenRgba requires CreateOffscreen");

        var pixelCount = (int)(_offscreenWidth * _offscreenHeight);
        var size = (ulong)(pixelCount * 4);

        // Host-visible staging buffer to receive the image copy.
        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateBuffer(&bufCI, null, out var stagingBuffer).CheckResult();
        DeviceApi.vkGetBufferMemoryRequirements(stagingBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out var stagingMemory).CheckResult();
        DeviceApi.vkBindBufferMemory(stagingBuffer, stagingMemory, 0);

        // One-shot command buffer: transition image ColorAttachment→TransferSrc, copy, transition back.
        DeviceApi.vkAllocateCommandBuffer(CommandPool, out var cmd).CheckResult();
        VkCommandBufferBeginInfo bi = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        DeviceApi.vkBeginCommandBuffer(cmd, &bi);

        TransitionImageLayout(cmd, _offscreenImage,
            VkImageLayout.ColorAttachmentOptimal, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.TransferRead,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.Transfer);

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D(_offscreenWidth, _offscreenHeight, 1)
        };
        DeviceApi.vkCmdCopyImageToBuffer(cmd, _offscreenImage, VkImageLayout.TransferSrcOptimal,
            stagingBuffer, 1, &region);

        TransitionImageLayout(cmd, _offscreenImage,
            VkImageLayout.TransferSrcOptimal, VkImageLayout.ColorAttachmentOptimal,
            VkAccessFlags.TransferRead, VkAccessFlags.ColorAttachmentWrite,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ColorAttachmentOutput);

        DeviceApi.vkEndCommandBuffer(cmd);
        VkSubmitInfo si2 = new() { commandBufferCount = 1, pCommandBuffers = &cmd };
        // Queue submit + wait + a command-pool free — all external-synchronization points, all on this
        // offscreen device's OWN private queue (see the note in EndOffscreenFrame).
        var copyResult = SubmitOffscreen(&si2, VkFence.Null, "submit(readback copy)");
        if (copyResult != VkResult.Success)
        {
            // The copy never executed, so the staging buffer still holds uninitialized memory. Release it
            // and fail: mapping it would hand back noise shaped exactly like a page of pixels, and there
            // is no way for the caller to tell that from a render.
            DeviceApi.vkFreeCommandBuffers(CommandPool, 1, &cmd);
            DeviceApi.vkDestroyBuffer(stagingBuffer);
            DeviceApi.vkFreeMemory(stagingMemory);
            throw new VkException(copyResult, "offscreen readback copy was never submitted");
        }
        DeviceApi.vkQueueWaitIdle(GraphicsQueue);
        DeviceApi.vkFreeCommandBuffers(CommandPool, 1, &cmd);

        // Map and copy out. B8G8R8A8 → convert to R8G8B8A8 for caller convenience.
        void* mapped;
        DeviceApi.vkMapMemory(stagingMemory, 0, size, 0, &mapped);
        var result = new byte[pixelCount * 4];
        var src = new Span<byte>(mapped, pixelCount * 4);
        for (var i = 0; i < pixelCount; i++)
        {
            result[i * 4 + 0] = src[i * 4 + 2]; // R ← B
            result[i * 4 + 1] = src[i * 4 + 1]; // G
            result[i * 4 + 2] = src[i * 4 + 0]; // B ← R
            result[i * 4 + 3] = src[i * 4 + 3]; // A
        }
        DeviceApi.vkUnmapMemory(stagingMemory);

        DeviceApi.vkDestroyBuffer(stagingBuffer);
        DeviceApi.vkFreeMemory(stagingMemory);
        return result;
    }

    private void TransitionImageLayout(VkCommandBuffer cmd, VkImage image,
        VkImageLayout oldLayout, VkImageLayout newLayout,
        VkAccessFlags srcAccess, VkAccessFlags dstAccess,
        VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage)
    {
        VkImageMemoryBarrier barrier = new()
        {
            oldLayout = oldLayout, newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            srcAccessMask = srcAccess, dstAccessMask = dstAccess
        };
        DeviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void CleanupOffscreenTarget()
    {
        if (_offscreenFramebuffer != VkFramebuffer.Null)
            DeviceApi.vkDestroyFramebuffer(_offscreenFramebuffer);
        if (_offscreenImageView != VkImageView.Null)
            DeviceApi.vkDestroyImageView(_offscreenImageView);
        if (_offscreenImage != VkImage.Null)
            DeviceApi.vkDestroyImage(_offscreenImage);
        if (_offscreenMemory != VkDeviceMemory.Null)
            DeviceApi.vkFreeMemory(_offscreenMemory);

        DestroyDepthAttachment(ref _depthImage, ref _depthMemory, ref _depthImageView);

        // The MSAA attachment CreateOffscreenTarget allocates alongside the readback image. Freeing it
        // here is what lets ResizeOffscreen recreate an MSAA target instead of leaking one per resize
        // — which is why resizing used to be refused outright for anything but Count1.
        if (_msaaImageView != VkImageView.Null)
            DeviceApi.vkDestroyImageView(_msaaImageView);
        if (_msaaImage != VkImage.Null)
            DeviceApi.vkDestroyImage(_msaaImage);
        if (_msaaMemory != VkDeviceMemory.Null)
            DeviceApi.vkFreeMemory(_msaaMemory);

        _offscreenFramebuffer = VkFramebuffer.Null;
        _offscreenImageView = VkImageView.Null;
        _offscreenImage = VkImage.Null;
        _offscreenMemory = VkDeviceMemory.Null;
        _msaaImageView = VkImageView.Null;
        _msaaImage = VkImage.Null;
        _msaaMemory = VkDeviceMemory.Null;
    }
}
