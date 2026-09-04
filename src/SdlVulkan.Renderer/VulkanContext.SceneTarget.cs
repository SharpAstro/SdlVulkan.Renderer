using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// Scene target -- a sampleable secondary render target WITH A DEPTH ATTACHMENT, for content whose
// visibility is decided by geometry rather than by draw order.
//
// Everything else this renderer draws is painter's-order 2D: the back-to-front sequence of draws IS
// the occlusion, which is why no pass here had a depth attachment before this one. Depth-tested
// content cannot be expressed that way -- a mesh's own triangles occlude each other in an order that
// depends on the camera, and no CPU-side sort of them is correct for all views.
//
// It is a sibling of CachedLayer and shares that class's structure, hazards and rules: one target per
// frame in flight (the frame fence retires N-2, so a single shared target would be rewritten while a
// submitted frame still samples it), capacity allocated once and never resized on the render thread,
// and a colour attachment that finalises to ShaderReadOnlyOptimal with its own descriptor set so the
// result draws through the existing DrawTexture path.
//
// WHY A SEPARATE PASS RATHER THAN DEPTH ON THE MAIN ONE. Adding a depth attachment to the swapchain
// pass would force one onto the cached-layer, damage and thumbnail passes too -- render-pass
// compatibility is per-attachment, and every pre-baked pipeline is shared across all of them -- so
// every existing pipeline would need a depth-stencil state it does not want, to describe a buffer
// 2D drawing never reads. Isolating depth in a pass with its own pipelines leaves all of that
// untouched, and the composite is a textured quad, which the renderer already does well.
//
// DELIBERATELY SINGLE-SAMPLE, unlike CachedLayer. MSAA here would need a multisample depth image
// alongside the multisample colour, and gains less than it costs: because the result is sampled
// rather than presented, the cheaper and better-looking route to smooth edges is to allocate the
// target larger than the on-screen rect and let LinearClampSampler downscale it. A consumer wanting
// antialiasing should supersample rather than ask for MSAA here.
public sealed unsafe partial class VulkanContext
{
    private VkRenderPass _sceneRenderPass;
    private VkFormat _sceneDepthFormat = VkFormat.Undefined;

    // Colour: the sampled result, one per frame in flight.
    private readonly VkImage[] _sceneImages = new VkImage[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _sceneMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly VkImageView[] _sceneViews = new VkImageView[MaxFramesInFlight];
    private readonly VkDescriptorSet[] _sceneSets = new VkDescriptorSet[MaxFramesInFlight];

    // Depth: written and tested within a single pass and never read afterwards, so it stores nothing
    // and needs no view beyond the framebuffer's.
    private readonly VkImage[] _sceneDepthImages = new VkImage[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _sceneDepthMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly VkImageView[] _sceneDepthViews = new VkImageView[MaxFramesInFlight];

    private readonly VkFramebuffer[] _sceneFramebuffers = new VkFramebuffer[MaxFramesInFlight];

    // Undefined layout until a slot's first pass finalises it, exactly as CachedLayer tracks: sampling
    // before then is undefined content rather than an error, so it cannot be left to the caller.
    private readonly bool[] _sceneRendered = new bool[MaxFramesInFlight];

    private uint _sceneTargetW;
    private uint _sceneTargetH;
    private bool _sceneTargetReady;
    private bool _inScenePass;

    /// <summary>True once <see cref="EnsureSceneTargets"/> has built the targets.</summary>
    public bool SceneTargetReady => _sceneTargetReady;

    /// <summary>
    /// The slot the frame being recorded must render into and sample from — the frame-in-flight
    /// index, so consecutive frames alternate and no frame writes a target another may still read.
    /// </summary>
    public int SceneTargetSlot => _currentFrame;

    /// <summary>How many independent scene targets exist; a camera change dirties them all.</summary>
    public int SceneTargetSlotCount => MaxFramesInFlight;

    /// <summary>Allocated capacity, which a rendered sub-rect may not exceed.</summary>
    public uint SceneTargetCapacityWidth => _sceneTargetW;

    /// <summary>Allocated capacity, which a rendered sub-rect may not exceed.</summary>
    public uint SceneTargetCapacityHeight => _sceneTargetH;

    /// <summary>
    /// The render pass depth-tested pipelines must be created against. Null until
    /// <see cref="EnsureSceneTargets"/> has run — a pipeline cannot be baked before the targets
    /// exist, because the depth format is chosen from what the device actually supports.
    /// </summary>
    public VkRenderPass SceneRenderPass => _sceneRenderPass;

    /// <summary>The depth format chosen for <see cref="SceneRenderPass"/>, or Undefined before setup.</summary>
    public VkFormat SceneDepthFormat => _sceneDepthFormat;

    /// <summary>
    /// Whether <paramref name="slot"/> has been rendered at least once and is therefore legal to
    /// sample. A slot that has never been through <see cref="EndScenePass"/> is still in Undefined
    /// layout, which shows as garbage rather than as an error.
    /// </summary>
    public bool IsSceneTargetSlotRendered(int slot)
        => _sceneTargetReady && (uint)slot < MaxFramesInFlight && _sceneRendered[slot];

    /// <summary>
    /// The descriptor set for a slot's colour image, ready for <c>VkRenderer.DrawTexture</c> or
    /// <c>DrawTextureRegion</c>. Rendering a region smaller than capacity is the normal case, so
    /// prefer the region overloads and pass UVs derived from the sub-rect actually rendered.
    /// </summary>
    public VkDescriptorSet SceneTargetDescriptorSet(int slot)
        => (uint)slot < MaxFramesInFlight ? _sceneSets[slot] : VkDescriptorSet.Null;

    /// <summary>
    /// Allocate the fixed-capacity scene targets, one per frame in flight. Call once up front, never
    /// mid steady-state: reallocating later needs a device wait that would stall the render thread
    /// (use <see cref="ReleaseSceneTargets"/> on a genuine resize, which does that wait
    /// deliberately). Returns true if the existing allocation already covers the request, and false
    /// if no usable depth format exists — in which case the device cannot host depth-tested content
    /// and the caller must fall back rather than draw.
    /// </summary>
    public bool EnsureSceneTargets(uint maxW, uint maxH)
    {
        if (_sceneTargetReady)
            return maxW <= _sceneTargetW && maxH <= _sceneTargetH;
        if (maxW == 0 || maxH == 0)
            return false;

        _sceneDepthFormat = ChooseDepthFormat();
        if (_sceneDepthFormat == VkFormat.Undefined)
            return false;

        _sceneTargetW = maxW;
        _sceneTargetH = maxH;
        _sceneRenderPass = CreateSceneRenderPass(OffscreenFormat, _sceneDepthFormat);
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            CreateSceneTarget(i, maxW, maxH);
            _sceneRendered[i] = false;
        }
        _sceneTargetReady = true;
        return true;
    }

    /// <summary>
    /// Tear the targets down so a later <see cref="EnsureSceneTargets"/> can rebuild them at a new
    /// capacity. Drains prior frames first, because the images being freed may still be referenced by
    /// a submitted frame; skipped on a known-stuck GPU, so a resize coinciding with a wedge cannot
    /// hard-freeze the render thread.
    /// </summary>
    public void ReleaseSceneTargets()
    {
        if (!_sceneTargetReady) return;
        TryWaitPriorFramesIdle("scene target release");
        CleanupSceneTargets();
    }

    /// <summary>
    /// The first depth format the device supports as an optimal-tiling depth-stencil attachment.
    /// </summary>
    /// <remarks>
    /// D32 first because a 32-bit float depth buffer is the one that does not visibly z-fight on
    /// content with a wide depth range, and the combined formats after it because some devices offer
    /// only those. Vulkan guarantees at least one of D32_SFLOAT and D24_UNORM_S8_UINT, so returning
    /// Undefined means something is wrong with the device rather than that the list is too short —
    /// which is why the caller is asked to fall back rather than to retry with other formats.
    /// </remarks>
    private VkFormat ChooseDepthFormat()
    {
        ReadOnlySpan<VkFormat> candidates =
        [
            VkFormat.D32Sfloat,
            VkFormat.D32SfloatS8Uint,
            VkFormat.D24UnormS8Uint,
        ];

        foreach (var format in candidates)
        {
            InstanceApi.vkGetPhysicalDeviceFormatProperties(PhysicalDevice, format, out var props);
            if ((props.optimalTilingFeatures & VkFormatFeatureFlags.DepthStencilAttachment) != 0)
                return format;
        }
        return VkFormat.Undefined;
    }

    private void CreateSceneTarget(int slot, uint width, uint height)
    {
        // Colour: the sampled result. Sampled alongside ColorAttachment for the same reason
        // CachedLayer's is — it is what the descriptor set points at.
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
        DeviceApi.vkCreateImage(&imgCI, null, out _sceneImages[slot]).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_sceneImages[slot], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _sceneMemories[slot]).CheckResult();
        DeviceApi.vkBindImageMemory(_sceneImages[slot], _sceneMemories[slot], 0).CheckResult();

        var viewCI = new VkImageViewCreateInfo(
            _sceneImages[slot], VkImageViewType.Image2D, OffscreenFormat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&viewCI, null, out _sceneViews[slot]).CheckResult();

        _sceneSets[slot] = AllocateDescriptorSet();
        UpdateDescriptorSet(_sceneSets[slot], _sceneViews[slot], LinearClampSampler);

        // Depth: never sampled and never stored, so TransientAttachment lets a tiler keep it in tile
        // memory and never write it out at all. Paired with storeOp DontCare below — the flag alone
        // does not make it transient, the pass has to agree that nothing outlives it.
        VkImageCreateInfo depthCI = new()
        {
            imageType = VkImageType.Image2D,
            format = _sceneDepthFormat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.TransientAttachment,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&depthCI, null, out _sceneDepthImages[slot]).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_sceneDepthImages[slot], out var depthReqs);
        VkMemoryAllocateInfo depthAlloc = new()
        {
            allocationSize = depthReqs.size,
            memoryTypeIndex = FindMemoryType(depthReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&depthAlloc, null, out _sceneDepthMemories[slot]).CheckResult();
        DeviceApi.vkBindImageMemory(_sceneDepthImages[slot], _sceneDepthMemories[slot], 0).CheckResult();

        // Depth aspect only, even on a combined depth-stencil format: a framebuffer attachment view
        // must not name an aspect the render pass does not use, and nothing here uses stencil.
        var depthViewCI = new VkImageViewCreateInfo(
            _sceneDepthImages[slot], VkImageViewType.Image2D, _sceneDepthFormat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Depth, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&depthViewCI, null, out _sceneDepthViews[slot]).CheckResult();

        Span<VkImageView> attachments = stackalloc VkImageView[2];
        attachments[0] = _sceneViews[slot];
        attachments[1] = _sceneDepthViews[slot];
        fixed (VkImageView* pAtt = attachments)
        {
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = _sceneRenderPass,
                attachmentCount = 2,
                pAttachments = pAtt,
                width = width, height = height, layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out _sceneFramebuffers[slot]).CheckResult();
        }
    }

    /// <remarks>
    /// The one pass in this renderer that is NOT compatible with the shared pre-baked pipelines, and
    /// necessarily so: it has a second attachment they know nothing about. Only pipelines created
    /// against <see cref="SceneRenderPass"/> may draw here. The subpass dependencies still come from
    /// <see cref="VulkanDevice.FillSubpassDependencies"/> — they were widened to carry the
    /// depth stages for this pass, so the count and content stay identical everywhere.
    /// </remarks>
    private VkRenderPass CreateSceneRenderPass(VkFormat colorFormat, VkFormat depthFormat)
    {
        Span<VkSubpassDependency> sharedDeps =
            stackalloc VkSubpassDependency[(int)VulkanDevice.SubpassDependencyCount];
        VulkanDevice.FillSubpassDependencies(sharedDeps);

        Span<VkAttachmentDescription> attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new()
        {
            format = colorFormat,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.ShaderReadOnlyOptimal
        };
        // storeOp DontCare is what makes the TransientAttachment usage above meaningful: the depth
        // values are consumed entirely inside this pass, so on a tiler they need never reach memory.
        attachments[1] = new()
        {
            format = depthFormat,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.DepthStencilAttachmentOptimal
        };

        VkAttachmentReference colorRef = new()
        {
            attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal
        };
        VkAttachmentReference depthRef = new()
        {
            attachment = 1, layout = VkImageLayout.DepthStencilAttachmentOptimal
        };
        VkSubpassDescription subpass = new()
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef,
            pDepthStencilAttachment = &depthRef
        };

        fixed (VkAttachmentDescription* pAtt = attachments)
        fixed (VkSubpassDependency* pDeps = sharedDeps)
        {
            VkRenderPassCreateInfo rpCI = new()
            {
                attachmentCount = 2, pAttachments = pAtt,
                subpassCount = 1, pSubpasses = &subpass,
                dependencyCount = VulkanDevice.SubpassDependencyCount, pDependencies = pDeps
            };
            DeviceApi.vkCreateRenderPass(&rpCI, null, out var rp).CheckResult();
            return rp;
        }
    }

    /// <summary>
    /// Begin the scene pass for this frame's slot, clearing colour and depth. Must be called while no
    /// other render pass is open (render passes cannot nest), i.e. from the OnPreRenderPass hook.
    /// Returns false and records nothing if the targets are not ready, a pass is already open, or
    /// (w,h) exceeds the allocated capacity.
    /// </summary>
    /// <remarks>
    /// Depth clears to 1.0, the far plane, which pairs with a CompareOp.Less depth test: a fragment
    /// passes when it is nearer than what is already there, and on an empty buffer everything is.
    /// </remarks>
    public bool BeginScenePass(VkCommandBuffer cmd, uint w, uint h, DIR.Lib.RGBAColor32 clearColor)
    {
        if (!_sceneTargetReady || _inScenePass) return false;
        if (w == 0 || h == 0 || w > _sceneTargetW || h > _sceneTargetH) return false;

        Span<VkClearValue> clears = stackalloc VkClearValue[2];
        clears[0] = new VkClearValue();
        clears[0].color = new VkClearColorValue(clearColor.Red / 255f, clearColor.Green / 255f,
            clearColor.Blue / 255f, clearColor.Alpha / 255f);
        clears[1] = new VkClearValue();
        clears[1].depthStencil = new VkClearDepthStencilValue(1f, 0);

        fixed (VkClearValue* pClears = clears)
        {
            VkRenderPassBeginInfo rpBI = new()
            {
                renderPass = _sceneRenderPass,
                framebuffer = _sceneFramebuffers[_currentFrame],
                renderArea = new VkRect2D(0, 0, w, h),
                clearValueCount = 2,
                pClearValues = pClears
            };
            DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);
        }

        VkViewport vp = new(0, 0, w, h, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, vp);
        VkRect2D sc = new(0, 0, w, h);
        DeviceApi.vkCmdSetScissor(cmd, 0, sc);
        _inScenePass = true;
        return true;
    }

    /// <summary>
    /// Ends the pass opened by <see cref="BeginScenePass"/>. The colour image is left in
    /// ShaderReadOnlyOptimal by the render pass itself, so no barrier is recorded and nothing is
    /// submitted or waited on: the slot is sampleable for the rest of this frame's command buffer and
    /// every later frame until it is re-rendered.
    /// </summary>
    public void EndScenePass(VkCommandBuffer cmd)
    {
        if (!_inScenePass) return;
        DeviceApi.vkCmdEndRenderPass(cmd);
        _inScenePass = false;
        _sceneRendered[_currentFrame] = true;
    }

    private void CleanupSceneTargets()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_sceneFramebuffers[i] != VkFramebuffer.Null)
            {
                DeviceApi.vkDestroyFramebuffer(_sceneFramebuffers[i]);
                _sceneFramebuffers[i] = VkFramebuffer.Null;
            }
            if (_sceneDepthViews[i] != VkImageView.Null)
            {
                DeviceApi.vkDestroyImageView(_sceneDepthViews[i]);
                _sceneDepthViews[i] = VkImageView.Null;
            }
            if (_sceneDepthImages[i] != VkImage.Null)
            {
                DeviceApi.vkDestroyImage(_sceneDepthImages[i]);
                _sceneDepthImages[i] = VkImage.Null;
            }
            if (_sceneDepthMemories[i] != VkDeviceMemory.Null)
            {
                DeviceApi.vkFreeMemory(_sceneDepthMemories[i]);
                _sceneDepthMemories[i] = VkDeviceMemory.Null;
            }
            if (_sceneViews[i] != VkImageView.Null)
            {
                DeviceApi.vkDestroyImageView(_sceneViews[i]);
                _sceneViews[i] = VkImageView.Null;
            }
            if (_sceneImages[i] != VkImage.Null)
            {
                DeviceApi.vkDestroyImage(_sceneImages[i]);
                _sceneImages[i] = VkImage.Null;
            }
            if (_sceneMemories[i] != VkDeviceMemory.Null)
            {
                DeviceApi.vkFreeMemory(_sceneMemories[i]);
                _sceneMemories[i] = VkDeviceMemory.Null;
            }
            // The descriptor set returns to the pool with it; nothing to free individually.
            _sceneSets[i] = VkDescriptorSet.Null;
            _sceneRendered[i] = false;
        }

        if (_sceneRenderPass != VkRenderPass.Null)
        {
            DeviceApi.vkDestroyRenderPass(_sceneRenderPass);
            _sceneRenderPass = VkRenderPass.Null;
        }

        _sceneTargetW = 0;
        _sceneTargetH = 0;
        _sceneDepthFormat = VkFormat.Undefined;
        _sceneTargetReady = false;
        _inScenePass = false;
    }
}
