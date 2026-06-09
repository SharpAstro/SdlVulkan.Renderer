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
                memoryTypeIndex = FindMemoryType(msaaMemReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
            };
            DeviceApi.vkAllocateMemory(&msaaAlloc, null, out _msaaMemory).CheckResult();
            DeviceApi.vkBindImageMemory(_msaaImage, _msaaMemory, 0).CheckResult();

            var msaaViewCI = new VkImageViewCreateInfo(
                _msaaImage, VkImageViewType.Image2D, OffscreenFormat,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&msaaViewCI, null, out _msaaImageView).CheckResult();
        }

        // Framebuffer
        Span<VkImageView> attachments = stackalloc VkImageView[2];
        if (MsaaSamples != VkSampleCountFlags.Count1)
        {
            attachments[0] = _msaaImageView;
            attachments[1] = _offscreenImageView;
            fixed (VkImageView* pAtt = attachments)
            {
                VkFramebufferCreateInfo fbCI = new()
                {
                    renderPass = RenderPass,
                    attachmentCount = 2,
                    pAttachments = pAtt,
                    width = width, height = height, layers = 1
                };
                DeviceApi.vkCreateFramebuffer(&fbCI, null, out _offscreenFramebuffer).CheckResult();
            }
        }
        else
        {
            var view = _offscreenImageView;
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = RenderPass,
                attachmentCount = 1,
                pAttachments = &view,
                width = width, height = height, layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out _offscreenFramebuffer).CheckResult();
        }
    }

    /// <summary>
    /// Recreate the offscreen render target at a new size, keeping the device, command buffers,
    /// sync objects, vertex buffers, and the renderer's font atlases intact — so glyphs stay warm
    /// across differently-sized pages in a multi-page raster/export job (a fresh context per page
    /// would re-rasterize every glyph). Count1 (no MSAA) only, which is what the offscreen
    /// raster/export path uses; CleanupOffscreenTarget doesn't free MSAA attachments.
    /// </summary>
    public void ResizeOffscreen(uint width, uint height)
    {
        if (!_isOffscreen) throw new InvalidOperationException("ResizeOffscreen requires CreateOffscreen");
        if (MsaaSamples != VkSampleCountFlags.Count1)
            throw new InvalidOperationException("ResizeOffscreen supports Count1 offscreen targets only");
        if (width == _offscreenWidth && height == _offscreenHeight) return;

        DeviceApi.vkDeviceWaitIdle(); // no in-flight frame may reference the target we're about to destroy
        CleanupOffscreenTarget();
        _offscreenWidth = width;
        _offscreenHeight = height;
        CreateOffscreenTarget(width, height);
    }

    /// <summary>
    /// Offscreen counterpart of <see cref="BeginFrame"/>. Waits on the frame fence, resets
    /// the command buffer, and returns it ready for recording. No swapchain acquire.
    /// </summary>
    public VkCommandBuffer BeginOffscreenFrame()
    {
        if (!_isOffscreen) throw new InvalidOperationException("BeginOffscreenFrame requires CreateOffscreen");

        var fence = _inFlightFences[_currentFrame];
        DeviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);
        DeviceApi.vkResetFences(1, &fence);

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

        VkClearValue clear = new();
        clear.color = new VkClearColorValue(clearR, clearG, clearB, clearA);

        VkRenderPassBeginInfo rpBI = new()
        {
            renderPass = RenderPass,
            framebuffer = _offscreenFramebuffer,
            renderArea = new VkRect2D(0, 0, _offscreenWidth, _offscreenHeight),
            clearValueCount = 1,
            pClearValues = &clear
        };
        DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);

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
        DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &si, _inFlightFences[_currentFrame]).CheckResult();

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    /// <summary>Blocks until the most recently submitted offscreen frame completes.</summary>
    public void WaitOffscreenFrameComplete()
    {
        // The previous frame's fence is the one we just submitted against.
        var prevFrame = (_currentFrame + MaxFramesInFlight - 1) % MaxFramesInFlight;
        var fence = _inFlightFences[prevFrame];
        DeviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);
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
        DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &si2, VkFence.Null).CheckResult();
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

        _offscreenFramebuffer = VkFramebuffer.Null;
        _offscreenImageView = VkImageView.Null;
        _offscreenImage = VkImage.Null;
        _offscreenMemory = VkDeviceMemory.Null;
    }
}
