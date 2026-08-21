using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// Cached layer -- a SAMPLEABLE secondary render target on the live device, so expensive content that
// rarely changes can be rendered once and re-blitted for free while cheap chrome redraws over it every
// frame. The motivating case: a FITS viewer whose image quad runs a heavy stretch/debayer shader, where
// a mouse move that only changes a status-bar readout was re-shading the whole image.
//
// This is a sibling of ThumbnailCapture (same "secondary target on the live device, recorded into the
// frame's own command buffer" shape, same render-pass compatibility trick) and deliberately NOT of
// CreateOffscreen, which replaces the swapchain with a headless device and therefore cannot see the
// live device's textures, atlases or vertex buffers at all. Three differences from the thumbnail:
//
//   * the attachment finalises as ShaderReadOnlyOptimal rather than TransferSrcOptimal, and carries
//     Sampled usage plus its own descriptor set -- so the result is drawn with the renderer's existing
//     DrawTexture and needs no new shader or pipeline anywhere;
//   * there is no readback buffer, no copy and nothing to consume; and
//   * it PERSISTS across frames instead of being consumed once, which is the whole point.
//
// ONE TARGET PER FRAME IN FLIGHT, which is a correctness requirement and not an optimisation. A single
// shared target would be rewritten while the previously submitted frame is still sampling it: the
// frame fence retires frame N-2, never N-1, so at any point mid-record one submitted frame still
// references the view. That is the hazard VkFontAtlas.Grow guards with a drain, and which the Adreno
// X1-85 answers by failing the next vkQueueSubmit. Draining here instead would be worse than the
// problem it solves -- content that changes every frame (a zoom drag) would stall the render thread on
// every single one. With a target per slot a content change costs MaxFramesInFlight re-renders and then
// nothing, and a continuously changing view is never slower than not caching at all.
//
// Capacity is allocated once and never resized on the render thread, ThumbnailCapture's rule for the
// same reason. Size it to the largest region you will cache and render a (w,h) sub-rect of it; a
// consumer that wants panning to be free should allocate its viewport plus a margin, so a pan inside
// the margin is a blit at an offset rather than a re-render.
//
// Memory note: under MSAA each slot needs its own multisample image as well (it cannot borrow the
// swapchain's, which is in active use, nor a sibling slot's, which would reintroduce exactly the
// cross-frame hazard above). At 4x that is four times the resolve size per slot, so capacity is worth
// choosing deliberately rather than defaulting to something generous.
public sealed unsafe partial class VulkanContext
{
    private VkRenderPass _layerRenderPass;

    // Per-slot resolve image: the single-sample colour attachment (the MSAA resolve target when MSAA is
    // on) and the image the descriptor set points at.
    private readonly VkImage[] _layerImages = new VkImage[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _layerMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly VkImageView[] _layerViews = new VkImageView[MaxFramesInFlight];
    private readonly VkDescriptorSet[] _layerSets = new VkDescriptorSet[MaxFramesInFlight];

    // Per-slot multisample colour (MSAA only).
    private readonly VkImage[] _layerMsaaImages = new VkImage[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _layerMsaaMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly VkImageView[] _layerMsaaViews = new VkImageView[MaxFramesInFlight];

    private readonly VkFramebuffer[] _layerFramebuffers = new VkFramebuffer[MaxFramesInFlight];

    // A slot's image is in Undefined layout until its first pass finalises it to ShaderReadOnlyOptimal,
    // so sampling before then is illegal. Tracked rather than left to the caller because the symptom is
    // undefined content, not an error.
    private readonly bool[] _layerRendered = new bool[MaxFramesInFlight];

    private uint _layerTargetW;      // fixed allocated capacity, never resized on the render thread
    private uint _layerTargetH;
    private bool _layerTargetReady;
    private bool _inLayerPass;

    /// <summary>True once <see cref="EnsureCachedLayerTargets"/> has built the targets.</summary>
    public bool CachedLayerTargetReady => _layerTargetReady;

    /// <summary>
    /// The slot the frame being recorded must render into and sample from. It is the frame-in-flight
    /// index, so consecutive frames alternate and no frame writes a target another may still be reading.
    /// </summary>
    public int CachedLayerSlot => _currentFrame;

    /// <summary>How many independent cached-layer targets exist; a content change dirties them all.</summary>
    public int CachedLayerSlotCount => MaxFramesInFlight;

    /// <summary>Allocated capacity, which a capture sub-rect may not exceed.</summary>
    public uint CachedLayerCapacityWidth => _layerTargetW;

    /// <summary>Allocated capacity, which a capture sub-rect may not exceed.</summary>
    public uint CachedLayerCapacityHeight => _layerTargetH;

    /// <summary>
    /// Whether <paramref name="slot"/> has been rendered at least once and is therefore legal to
    /// sample. A slot that has never been through <see cref="EndCachedLayerPass"/> is still in
    /// Undefined layout: blitting it is undefined behaviour that shows as garbage rather than an error,
    /// so check this before drawing a slot you did not just render.
    /// </summary>
    public bool IsCachedLayerSlotRendered(int slot)
        => _layerTargetReady && (uint)slot < MaxFramesInFlight && _layerRendered[slot];

    /// <summary>
    /// The descriptor set for a slot's image, ready to hand to <c>VkRenderer.DrawTexture</c> or
    /// <c>DrawTextureRegion</c>. Sampling a region smaller than capacity is the normal case, so prefer
    /// the region overloads and pass UVs derived from the sub-rect actually rendered.
    /// </summary>
    public VkDescriptorSet CachedLayerDescriptorSet(int slot)
        => (uint)slot < MaxFramesInFlight ? _layerSets[slot] : VkDescriptorSet.Null;

    /// <summary>
    /// Allocate the fixed-capacity cached-layer targets, one per frame in flight. Call once up front,
    /// never mid steady-state: the images are brand new so nothing in flight references them, but
    /// reallocating later needs a device wait that would stall the render thread (use
    /// <see cref="ReleaseCachedLayerTargets"/> on a genuine resize, which does that wait deliberately).
    /// Returns true if the existing allocation already covers the request.
    /// </summary>
    public bool EnsureCachedLayerTargets(uint maxW, uint maxH)
    {
        if (_layerTargetReady)
            return maxW <= _layerTargetW && maxH <= _layerTargetH;
        if (maxW == 0 || maxH == 0)
            return false;

        _layerTargetW = maxW;
        _layerTargetH = maxH;
        _layerRenderPass = CreateCachedLayerRenderPass(OffscreenFormat, MsaaSamples);
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            CreateCachedLayerTarget(i, maxW, maxH);
            _layerRendered[i] = false;
        }
        _layerTargetReady = true;
        return true;
    }

    /// <summary>
    /// Tear the targets down so a later <see cref="EnsureCachedLayerTargets"/> can rebuild them at a new
    /// capacity, e.g. after a window resize. Drains prior frames first, because the images being freed
    /// may still be referenced by a submitted frame; skipped on a known-stuck GPU, so a resize that
    /// coincides with a wedge cannot hard-freeze the render thread.
    /// </summary>
    public void ReleaseCachedLayerTargets()
    {
        if (!_layerTargetReady) return;
        TryWaitPriorFramesIdle("cached layer release");
        CleanupCachedLayerTargets();
    }

    private void CreateCachedLayerTarget(int slot, uint width, uint height)
    {
        // Resolve / sampled image (single-sample). Under MSAA this is the resolve target; without MSAA
        // it is the sole colour attachment. Either way it is what the descriptor set samples, hence
        // Sampled alongside ColorAttachment.
        VkImageCreateInfo imgCI = new()
        {
            imageType = VkImageType.Image2D,
            format = OffscreenFormat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&imgCI, null, out _layerImages[slot]).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_layerImages[slot], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _layerMemories[slot]).CheckResult();
        DeviceApi.vkBindImageMemory(_layerImages[slot], _layerMemories[slot], 0).CheckResult();

        var viewCI = new VkImageViewCreateInfo(
            _layerImages[slot], VkImageViewType.Image2D, OffscreenFormat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&viewCI, null, out _layerViews[slot]).CheckResult();

        // Shared device sampler: samplers carry no per-image state, and a private one per target would
        // burn maxSamplerAllocationCount for nothing (the lesson VkTexture records).
        _layerSets[slot] = AllocateDescriptorSet();
        UpdateDescriptorSet(_layerSets[slot], _layerViews[slot], LinearClampSampler);

        // Dedicated multisample colour image (MSAA only). It cannot borrow the swapchain's _msaaImage
        // (in active use) nor a sibling slot's (that is the cross-frame hazard this class exists to
        // avoid), so each slot owns one.
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
            DeviceApi.vkCreateImage(&msaaImgCI, null, out _layerMsaaImages[slot]).CheckResult();
            DeviceApi.vkGetImageMemoryRequirements(_layerMsaaImages[slot], out var msaaMemReqs);
            VkMemoryAllocateInfo msaaAlloc = new()
            {
                allocationSize = msaaMemReqs.size,
                memoryTypeIndex = FindMemoryType(msaaMemReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
            };
            DeviceApi.vkAllocateMemory(&msaaAlloc, null, out _layerMsaaMemories[slot]).CheckResult();
            DeviceApi.vkBindImageMemory(_layerMsaaImages[slot], _layerMsaaMemories[slot], 0).CheckResult();

            var msaaViewCI = new VkImageViewCreateInfo(
                _layerMsaaImages[slot], VkImageViewType.Image2D, OffscreenFormat,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&msaaViewCI, null, out _layerMsaaViews[slot]).CheckResult();
        }

        Span<VkImageView> attachments = stackalloc VkImageView[2];
        if (MsaaSamples != VkSampleCountFlags.Count1)
        {
            attachments[0] = _layerMsaaViews[slot];
            attachments[1] = _layerViews[slot];
            fixed (VkImageView* pAtt = attachments)
            {
                VkFramebufferCreateInfo fbCI = new()
                {
                    renderPass = _layerRenderPass,
                    attachmentCount = 2,
                    pAttachments = pAtt,
                    width = width, height = height, layers = 1
                };
                DeviceApi.vkCreateFramebuffer(&fbCI, null, out _layerFramebuffers[slot]).CheckResult();
            }
        }
        else
        {
            var view = _layerViews[slot];
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = _layerRenderPass,
                attachmentCount = 1,
                pAttachments = &view,
                width = width, height = height, layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out _layerFramebuffers[slot]).CheckResult();
        }
    }

    /// <remarks>
    /// Identical in structure to the swapchain and thumbnail passes -- same attachment formats, sample
    /// count and subpass attachment references -- so VkPipelineSet's pre-baked pipelines are render-pass
    /// compatible and bind into it unchanged. Only loadOp/storeOp/finalLayout differ, and those do not
    /// affect compatibility. The dependency pair comes from VulkanDevice.FillSubpassDependencies for the
    /// reason stated there: a pass declaring a different count is reported as
    /// VUID-vkCmdDraw-renderPass-02684, and it is that pair's trailing entry (widened to cover a
    /// fragment-shader read for this class) which makes the colour write visible to the blit that
    /// samples it later in the same command buffer.
    /// </remarks>
    private VkRenderPass CreateCachedLayerRenderPass(VkFormat format, VkSampleCountFlags msaaSamples)
    {
        Span<VkSubpassDependency> sharedDeps =
            stackalloc VkSubpassDependency[(int)VulkanDevice.SubpassDependencyCount];
        VulkanDevice.FillSubpassDependencies(sharedDeps);

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
                finalLayout = VkImageLayout.ShaderReadOnlyOptimal
            };
            VkAttachmentReference colorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
            VkSubpassDescription subpass = new()
            {
                pipelineBindPoint = VkPipelineBindPoint.Graphics,
                colorAttachmentCount = 1,
                pColorAttachments = &colorRef
            };
            fixed (VkSubpassDependency* pDeps = sharedDeps)
            {
                VkRenderPassCreateInfo rpCI = new()
                {
                    attachmentCount = 1, pAttachments = &colorAttachment,
                    subpassCount = 1, pSubpasses = &subpass,
                    dependencyCount = VulkanDevice.SubpassDependencyCount, pDependencies = pDeps
                };
                DeviceApi.vkCreateRenderPass(&rpCI, null, out var rp).CheckResult();
                return rp;
            }
        }

        // MSAA: multisample colour (0) resolves to the single-sample sampled image (1).
        Span<VkAttachmentDescription> attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new()
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
        attachments[1] = new()
        {
            format = format,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.ShaderReadOnlyOptimal
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
        fixed (VkAttachmentDescription* pAttachments = attachments)
        fixed (VkSubpassDependency* pDeps = sharedDeps)
        {
            VkRenderPassCreateInfo msaaRpCI = new()
            {
                attachmentCount = 2, pAttachments = pAttachments,
                subpassCount = 1, pSubpasses = &msaaSubpass,
                dependencyCount = VulkanDevice.SubpassDependencyCount, pDependencies = pDeps
            };
            DeviceApi.vkCreateRenderPass(&msaaRpCI, null, out var renderPass).CheckResult();
            return renderPass;
        }
    }

    /// <summary>
    /// Begins the cached-layer render pass into the (w,h) top-left sub-rect of this frame's slot,
    /// clearing to <paramref name="clearColor"/>. Record the expensive draws after this, then call
    /// <see cref="EndCachedLayerPass"/>. Like the thumbnail pass this MUST be recorded before the main
    /// render pass begins (render passes cannot nest), i.e. from the OnPreRenderPass hook. Returns false
    /// and records nothing if the targets are not ready, a pass is already open, or (w,h) exceeds the
    /// allocated capacity.
    /// </summary>
    public bool BeginCachedLayerPass(VkCommandBuffer cmd, uint w, uint h, DIR.Lib.RGBAColor32 clearColor)
    {
        if (!_layerTargetReady || _inLayerPass) return false;
        if (w == 0 || h == 0 || w > _layerTargetW || h > _layerTargetH) return false;

        VkClearValue clear = new();
        clear.color = new VkClearColorValue(clearColor.Red / 255f, clearColor.Green / 255f,
            clearColor.Blue / 255f, clearColor.Alpha / 255f);
        VkRenderPassBeginInfo rpBI = new()
        {
            renderPass = _layerRenderPass,
            framebuffer = _layerFramebuffers[_currentFrame],
            renderArea = new VkRect2D(0, 0, w, h),
            clearValueCount = 1,
            pClearValues = &clear
        };
        DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);

        VkViewport vp = new(0, 0, w, h, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, vp);
        VkRect2D sc = new(0, 0, w, h);
        DeviceApi.vkCmdSetScissor(cmd, 0, sc);
        _inLayerPass = true;
        return true;
    }

    /// <summary>
    /// Ends the pass opened by <see cref="BeginCachedLayerPass"/>. The image is left in
    /// ShaderReadOnlyOptimal by the render pass itself (finalLayout), so no barrier is recorded and
    /// nothing is submitted or waited on: this slot is sampleable for the rest of this frame's command
    /// buffer and every later frame until it is re-rendered.
    /// </summary>
    public void EndCachedLayerPass(VkCommandBuffer cmd)
    {
        if (!_inLayerPass) return;
        DeviceApi.vkCmdEndRenderPass(cmd);
        _inLayerPass = false;
        _layerRendered[_currentFrame] = true;
    }

    private void CleanupCachedLayerTargets()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_layerFramebuffers[i] != VkFramebuffer.Null)
            {
                DeviceApi.vkDestroyFramebuffer(_layerFramebuffers[i]);
                _layerFramebuffers[i] = VkFramebuffer.Null;
            }
            if (_layerMsaaViews[i] != VkImageView.Null)
            {
                DeviceApi.vkDestroyImageView(_layerMsaaViews[i]);
                _layerMsaaViews[i] = VkImageView.Null;
            }
            if (_layerMsaaImages[i] != VkImage.Null)
            {
                DeviceApi.vkDestroyImage(_layerMsaaImages[i]);
                _layerMsaaImages[i] = VkImage.Null;
            }
            if (_layerMsaaMemories[i] != VkDeviceMemory.Null)
            {
                DeviceApi.vkFreeMemory(_layerMsaaMemories[i]);
                _layerMsaaMemories[i] = VkDeviceMemory.Null;
            }
            if (_layerViews[i] != VkImageView.Null)
            {
                DeviceApi.vkDestroyImageView(_layerViews[i]);
                _layerViews[i] = VkImageView.Null;
            }
            if (_layerImages[i] != VkImage.Null)
            {
                DeviceApi.vkDestroyImage(_layerImages[i]);
                _layerImages[i] = VkImage.Null;
            }
            if (_layerMemories[i] != VkDeviceMemory.Null)
            {
                DeviceApi.vkFreeMemory(_layerMemories[i]);
                _layerMemories[i] = VkDeviceMemory.Null;
            }
            // The descriptor set returns to the pool with it; nothing to free individually.
            _layerSets[i] = VkDescriptorSet.Null;
            _layerRendered[i] = false;
        }

        if (_layerRenderPass != VkRenderPass.Null)
        {
            DeviceApi.vkDestroyRenderPass(_layerRenderPass);
            _layerRenderPass = VkRenderPass.Null;
        }

        _layerTargetW = 0;
        _layerTargetH = 0;
        _layerTargetReady = false;
        _inLayerPass = false;
    }
}
