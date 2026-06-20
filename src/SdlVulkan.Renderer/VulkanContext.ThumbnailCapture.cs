using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// Live-device thumbnail capture — a secondary offscreen render target that lives on the SAME
// VkDevice as the swapchain context, so it can re-issue a page's already-tessellated, already-
// uploaded geometry (persistent vertex buffers, the live glyph atlas, resident image textures) at
// thumbnail scale with ZERO re-tessellation. Contrast with CreateOffscreen, which spins up a
// separate headless VkDevice and therefore cannot see the live device's GPU resources.
//
// The capture pass is recorded into the SAME frame command buffer, before BeginRenderPass (from the
// OnPreRenderPass hook). The vkCmdCopyImageToBuffer that follows rides the frame's fence, so the
// readback is consumed — without blocking the render thread — the next time that fence index is
// waited at the top of BeginFrame (see ConsumeThumbnailReadback). No extra submit, no extra fence.
//
// Pipeline compatibility: the capture render pass uses the same attachment formats, sample count,
// and subpass attachment references as the swapchain render pass, so VkPipelineSet's pre-baked
// pipelines bind into it unchanged. Only loadOp/storeOp/finalLayout differ (those don't affect
// render-pass compatibility): the resolve attachment finalizes as TransferSrcOptimal so the copy
// needs no manual layout barrier.
public sealed unsafe partial class VulkanContext
{
    // Capture target — format matches the swapchain (B8G8R8A8Unorm) for pipeline compatibility.
    private VkRenderPass _thumbRenderPass;
    private VkImage _thumbResolveImage;          // single-sample copy source (also the MSAA resolve target)
    private VkDeviceMemory _thumbResolveMemory;
    private VkImageView _thumbResolveView;
    private VkImage _thumbMsaaImage;             // multisample color (MSAA only) — MUST be its own image;
    private VkDeviceMemory _thumbMsaaMemory;     // the swapchain's _msaaImage is in active use here.
    private VkImageView _thumbMsaaView;
    private VkFramebuffer _thumbFramebuffer;
    private VkBuffer _thumbReadbackBuffer;       // host-visible, persistent, sized to capacity
    private VkDeviceMemory _thumbReadbackMemory;

    private uint _thumbTargetW;                  // fixed allocated capacity (never resized on the render thread)
    private uint _thumbTargetH;
    private bool _thumbTargetReady;

    // Per-capture state.
    private uint _thumbCapW;                     // current capture sub-rect (<= target capacity)
    private uint _thumbCapH;
    private bool _thumbPending;                  // a copy is recorded, awaiting its fence
    private int _thumbPendingIndex;              // frame-fence index the recorded copy rides
    private bool _thumbReady;                    // a finished snapshot is waiting for the caller to fetch
    private byte[]? _thumbReadyRgba;
    private uint _thumbReadyW;
    private uint _thumbReadyH;

    /// <summary>
    /// True while a capture's copy has been recorded but not yet completed+snapshotted, or a finished
    /// snapshot is waiting to be fetched. Callers should not request a new capture until this is false.
    /// </summary>
    public bool ThumbnailCaptureBusy => _thumbPending || _thumbReady;

    /// <summary>True once <see cref="EnsureThumbnailTarget"/> has built the capture target.</summary>
    public bool ThumbnailTargetReady => _thumbTargetReady;

    /// <summary>
    /// Allocate the fixed-size capture target (render pass, MSAA color + resolve image, framebuffer,
    /// host-visible readback buffer). Call ONCE up front (e.g. on first capture request), never mid
    /// steady-state — it is not a resize: the images are brand new so no in-flight frame references
    /// them, but reallocating later would need a device-wait that would stall the render thread.
    /// Per-page captures render into a (w,h) sub-rect of this target, so size it to the largest
    /// thumbnail you will ever request (e.g. canonical width × tallest expected page).
    /// </summary>
    public bool EnsureThumbnailTarget(uint maxW, uint maxH)
    {
        if (_thumbTargetReady)
            return maxW <= _thumbTargetW && maxH <= _thumbTargetH;
        if (maxW == 0 || maxH == 0)
            return false;

        _thumbTargetW = maxW;
        _thumbTargetH = maxH;
        _thumbRenderPass = CreateThumbnailRenderPass(OffscreenFormat, MsaaSamples);
        CreateThumbnailTarget(maxW, maxH);
        CreateThumbnailReadbackBuffer((ulong)maxW * maxH * 4);
        _thumbTargetReady = true;
        return true;
    }

    private void CreateThumbnailTarget(uint width, uint height)
    {
        // Resolve / copy-source image (single-sample). Under MSAA this is the resolve target;
        // without MSAA it is the sole color attachment. Either way it is the copy source.
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
        DeviceApi.vkCreateImage(&imgCI, null, out _thumbResolveImage).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_thumbResolveImage, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _thumbResolveMemory).CheckResult();
        DeviceApi.vkBindImageMemory(_thumbResolveImage, _thumbResolveMemory, 0).CheckResult();

        var viewCI = new VkImageViewCreateInfo(
            _thumbResolveImage, VkImageViewType.Image2D, OffscreenFormat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&viewCI, null, out _thumbResolveView).CheckResult();

        // Dedicated multisample color image (MSAA only). NOTE: unlike the offscreen path we cannot
        // borrow the _msaaImage fields — the live swapchain owns those.
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
            DeviceApi.vkCreateImage(&msaaImgCI, null, out _thumbMsaaImage).CheckResult();
            DeviceApi.vkGetImageMemoryRequirements(_thumbMsaaImage, out var msaaMemReqs);
            VkMemoryAllocateInfo msaaAlloc = new()
            {
                allocationSize = msaaMemReqs.size,
                memoryTypeIndex = FindMemoryType(msaaMemReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
            };
            DeviceApi.vkAllocateMemory(&msaaAlloc, null, out _thumbMsaaMemory).CheckResult();
            DeviceApi.vkBindImageMemory(_thumbMsaaImage, _thumbMsaaMemory, 0).CheckResult();

            var msaaViewCI = new VkImageViewCreateInfo(
                _thumbMsaaImage, VkImageViewType.Image2D, OffscreenFormat,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&msaaViewCI, null, out _thumbMsaaView).CheckResult();
        }

        // Framebuffer wired to the capture render pass.
        Span<VkImageView> attachments = stackalloc VkImageView[2];
        if (MsaaSamples != VkSampleCountFlags.Count1)
        {
            attachments[0] = _thumbMsaaView;
            attachments[1] = _thumbResolveView;
            fixed (VkImageView* pAtt = attachments)
            {
                VkFramebufferCreateInfo fbCI = new()
                {
                    renderPass = _thumbRenderPass,
                    attachmentCount = 2,
                    pAttachments = pAtt,
                    width = width, height = height, layers = 1
                };
                DeviceApi.vkCreateFramebuffer(&fbCI, null, out _thumbFramebuffer).CheckResult();
            }
        }
        else
        {
            var view = _thumbResolveView;
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = _thumbRenderPass,
                attachmentCount = 1,
                pAttachments = &view,
                width = width, height = height, layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out _thumbFramebuffer).CheckResult();
        }
    }

    private void CreateThumbnailReadbackBuffer(ulong size)
    {
        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateBuffer(&bufCI, null, out _thumbReadbackBuffer).CheckResult();
        DeviceApi.vkGetBufferMemoryRequirements(_thumbReadbackBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _thumbReadbackMemory).CheckResult();
        DeviceApi.vkBindBufferMemory(_thumbReadbackBuffer, _thumbReadbackMemory, 0);
    }

    // Capture render pass — same attachment refs as the swapchain render pass (so pre-baked pipelines
    // are compatible), but the resolve/color attachment finalizes as TransferSrcOptimal and an extra
    // subpass→EXTERNAL transfer dependency makes the resolve write visible to vkCmdCopyImageToBuffer.
    private VkRenderPass CreateThumbnailRenderPass(VkFormat format, VkSampleCountFlags msaaSamples)
    {
        // Make the color write visible to the transfer copy that follows EndRenderPass.
        VkSubpassDependency toTransfer = new()
        {
            srcSubpass = 0, dstSubpass = VK_SUBPASS_EXTERNAL,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite,
            dstStageMask = VkPipelineStageFlags.Transfer,
            dstAccessMask = VkAccessFlags.TransferRead
        };
        VkSubpassDependency fromExternal = new()
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput, srcAccessMask = 0,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite
        };

        if (msaaSamples == VkSampleCountFlags.Count1)
        {
            VkAttachmentDescription colorAttachment = new()
            {
                format = format,
                samples = VkSampleCountFlags.Count1,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VkAttachmentStoreOp.DontCare,
                initialLayout = VkImageLayout.Undefined,
                finalLayout = VkImageLayout.TransferSrcOptimal
            };
            VkAttachmentReference colorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
            VkSubpassDescription subpass = new()
            {
                pipelineBindPoint = VkPipelineBindPoint.Graphics,
                colorAttachmentCount = 1,
                pColorAttachments = &colorRef
            };
            Span<VkSubpassDependency> deps = stackalloc VkSubpassDependency[2] { fromExternal, toTransfer };
            fixed (VkSubpassDependency* pDeps = deps)
            {
                VkRenderPassCreateInfo rpCI = new()
                {
                    attachmentCount = 1, pAttachments = &colorAttachment,
                    subpassCount = 1, pSubpasses = &subpass,
                    dependencyCount = 2, pDependencies = pDeps
                };
                DeviceApi.vkCreateRenderPass(&rpCI, null, out var rp).CheckResult();
                return rp;
            }
        }

        // MSAA: multisample color (0) resolves to the single-sample copy-source image (1).
        Span<VkAttachmentDescription> attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new() // multisample color (transient, not stored)
        {
            format = format,
            samples = msaaSamples,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.ColorAttachmentOptimal
        };
        attachments[1] = new() // resolve target = copy source
        {
            format = format,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.TransferSrcOptimal
        };
        VkAttachmentReference msaaColorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        VkAttachmentReference resolveRef = new() { attachment = 1, layout = VkImageLayout.ColorAttachmentOptimal };
        VkSubpassDescription msaaSubpass = new()
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &msaaColorRef,
            pResolveAttachments = &resolveRef
        };
        Span<VkSubpassDependency> msaaDeps = stackalloc VkSubpassDependency[2] { fromExternal, toTransfer };
        fixed (VkAttachmentDescription* pAttachments = attachments)
        fixed (VkSubpassDependency* pDeps = msaaDeps)
        {
            VkRenderPassCreateInfo msaaRpCI = new()
            {
                attachmentCount = 2, pAttachments = pAttachments,
                subpassCount = 1, pSubpasses = &msaaSubpass,
                dependencyCount = 2, pDependencies = pDeps
            };
            DeviceApi.vkCreateRenderPass(&msaaRpCI, null, out var renderPass).CheckResult();
            return renderPass;
        }
    }

    /// <summary>
    /// Begins the thumbnail capture render pass into the (w,h) top-left sub-rect of the capture
    /// target, clearing to white (the page background). Record geometry draws after this, then call
    /// <see cref="EndThumbnailCapturePassAndCopy"/>. Returns false (records nothing) if the target
    /// isn't ready, a previous capture is still in flight, or (w,h) exceeds the allocated capacity.
    /// </summary>
    public bool BeginThumbnailCapturePass(VkCommandBuffer cmd, uint w, uint h)
    {
        if (!_thumbTargetReady || _thumbPending || _thumbReady) return false;
        if (w == 0 || h == 0 || w > _thumbTargetW || h > _thumbTargetH) return false;

        _thumbCapW = w;
        _thumbCapH = h;

        VkClearValue clear = new();
        clear.color = new VkClearColorValue(1f, 1f, 1f, 1f); // white page background
        VkRenderPassBeginInfo rpBI = new()
        {
            renderPass = _thumbRenderPass,
            framebuffer = _thumbFramebuffer,
            renderArea = new VkRect2D(0, 0, w, h),
            clearValueCount = 1,
            pClearValues = &clear
        };
        DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);

        VkViewport vp = new(0, 0, w, h, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, vp);
        VkRect2D sc = new(0, 0, w, h);
        DeviceApi.vkCmdSetScissor(cmd, 0, sc);
        return true;
    }

    /// <summary>
    /// Ends the capture render pass and records the copy of the (w,h) sub-rect into the readback
    /// buffer. The resolve image is already in TransferSrcOptimal (render-pass finalLayout) so no
    /// layout barrier is needed. The copy rides the frame fence; the snapshot is consumed without
    /// blocking at the next BeginFrame that waits this fence index (see ConsumeThumbnailReadback).
    /// </summary>
    public void EndThumbnailCapturePassAndCopy(VkCommandBuffer cmd)
    {
        DeviceApi.vkCmdEndRenderPass(cmd);

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,   // tightly packed to imageExtent.width
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D(_thumbCapW, _thumbCapH, 1)
        };
        DeviceApi.vkCmdCopyImageToBuffer(cmd, _thumbResolveImage, VkImageLayout.TransferSrcOptimal,
            _thumbReadbackBuffer, 1, &region);

        _thumbPending = true;
        _thumbPendingIndex = _currentFrame; // EndFrame submits this frame under _inFlightFences[_currentFrame]
    }

    /// <summary>
    /// Called from BeginFrame immediately after the in-flight fence wait (and before the reset).
    /// If a recorded copy rode the fence index that was just waited, its GPU work is now guaranteed
    /// complete, so snapshot the readback buffer (BGRA→RGBA) into a managed array the caller can
    /// fetch later via <see cref="TryGetThumbnailReadback"/>. No GPU wait happens here — it piggybacks
    /// on the wait BeginFrame already performed, so the render thread never blocks for the capture.
    /// </summary>
    private void ConsumeThumbnailReadback()
    {
        if (!_thumbPending || _thumbPendingIndex != _currentFrame) return;

        var w = _thumbCapW;
        var h = _thumbCapH;
        var pixelCount = (int)(w * h);
        var size = (ulong)(pixelCount * 4);

        void* mapped;
        DeviceApi.vkMapMemory(_thumbReadbackMemory, 0, size, 0, &mapped);
        var rgba = new byte[pixelCount * 4];
        var src = new Span<byte>(mapped, pixelCount * 4);
        for (var i = 0; i < pixelCount; i++)
        {
            rgba[i * 4 + 0] = src[i * 4 + 2]; // R ← B
            rgba[i * 4 + 1] = src[i * 4 + 1]; // G
            rgba[i * 4 + 2] = src[i * 4 + 0]; // B ← R
            rgba[i * 4 + 3] = src[i * 4 + 3]; // A
        }
        DeviceApi.vkUnmapMemory(_thumbReadbackMemory);

        _thumbReadyRgba = rgba;
        _thumbReadyW = w;
        _thumbReadyH = h;
        _thumbReady = true;
        _thumbPending = false;
    }

    /// <summary>
    /// Hands the most recent finished capture snapshot to the caller (RGBA, top-to-bottom rows) and
    /// clears the ready state so the next capture can be scheduled. Returns false if none is ready.
    /// </summary>
    public bool TryGetThumbnailReadback(out byte[] rgba, out int width, out int height)
    {
        if (!_thumbReady || _thumbReadyRgba is null)
        {
            rgba = [];
            width = 0;
            height = 0;
            return false;
        }
        rgba = _thumbReadyRgba;
        width = (int)_thumbReadyW;
        height = (int)_thumbReadyH;
        _thumbReadyRgba = null;
        _thumbReady = false;
        return true;
    }

    private void CleanupThumbnailTarget()
    {
        if (!_thumbTargetReady && _thumbRenderPass == VkRenderPass.Null) return;

        if (_thumbFramebuffer != VkFramebuffer.Null) DeviceApi.vkDestroyFramebuffer(_thumbFramebuffer);
        if (_thumbResolveView != VkImageView.Null) DeviceApi.vkDestroyImageView(_thumbResolveView);
        if (_thumbResolveImage != VkImage.Null) DeviceApi.vkDestroyImage(_thumbResolveImage);
        if (_thumbResolveMemory != VkDeviceMemory.Null) DeviceApi.vkFreeMemory(_thumbResolveMemory);
        if (_thumbMsaaView != VkImageView.Null) DeviceApi.vkDestroyImageView(_thumbMsaaView);
        if (_thumbMsaaImage != VkImage.Null) DeviceApi.vkDestroyImage(_thumbMsaaImage);
        if (_thumbMsaaMemory != VkDeviceMemory.Null) DeviceApi.vkFreeMemory(_thumbMsaaMemory);
        if (_thumbReadbackBuffer != VkBuffer.Null) DeviceApi.vkDestroyBuffer(_thumbReadbackBuffer);
        if (_thumbReadbackMemory != VkDeviceMemory.Null) DeviceApi.vkFreeMemory(_thumbReadbackMemory);
        if (_thumbRenderPass != VkRenderPass.Null) DeviceApi.vkDestroyRenderPass(_thumbRenderPass);

        _thumbFramebuffer = VkFramebuffer.Null;
        _thumbResolveView = VkImageView.Null;
        _thumbResolveImage = VkImage.Null;
        _thumbResolveMemory = VkDeviceMemory.Null;
        _thumbMsaaView = VkImageView.Null;
        _thumbMsaaImage = VkImage.Null;
        _thumbMsaaMemory = VkDeviceMemory.Null;
        _thumbReadbackBuffer = VkBuffer.Null;
        _thumbReadbackMemory = VkDeviceMemory.Null;
        _thumbRenderPass = VkRenderPass.Null;
        _thumbTargetReady = false;
        _thumbPending = false;
        _thumbReady = false;
        _thumbReadyRgba = null;
    }
}
