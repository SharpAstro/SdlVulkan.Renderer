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
        var instanceApi = GetApi(instance);

        var physicalDevice = PickPhysicalDeviceOffscreen(instanceApi, out var queueFamily);

        float queuePriority = 1.0f;
        VkDeviceQueueCreateInfo queueCI = new()
        {
            queueFamilyIndex = queueFamily,
            queueCount = 1,
            pQueuePriorities = &queuePriority
        };

        // Offscreen renders never touch a swapchain, so don't request VK_KHR_swapchain on the
        // device. Important for headless environments (Linux CI with Mesa lavapipe / llvmpipe
        // software rasterizer, containers without a display server) where the instance has no
        // surface extensions enabled — swapchain-on-device would still load but enabling it
        // without the instance-level counterpart is awkward to justify, and skipping it keeps
        // the device request minimal.
        VkDeviceCreateInfo deviceCI = new()
        {
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueCI,
            enabledExtensionCount = 0,
            ppEnabledExtensionNames = null,
        };

        instanceApi.vkCreateDevice(physicalDevice, &deviceCI, null, out var device).CheckResult();
        var deviceApi = GetApi(instance, device);
        deviceApi.vkGetDeviceQueue(queueFamily, 0, out var graphicsQueue);

        VkCommandPoolCreateInfo poolCI = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = queueFamily
        };
        deviceApi.vkCreateCommandPool(&poolCI, null, out var commandPool).CheckResult();

        // Offscreen render pass: finalLayout = ColorAttachmentOptimal so we can transition
        // to TransferSrcOptimal manually for readback. The swapchain path uses PresentSrcKHR
        // which isn't copyable without an extra transition anyway.
        var renderPass = CreateOffscreenRenderPass(deviceApi, VkFormat.B8G8R8A8Unorm, msaaSamples);

        // Descriptor pool, layout, pipeline layout — identical to the swapchain path so
        // VkPipelineSet's pipelines can be used without modification.
        VkDescriptorPoolSize poolSize = new()
        {
            type = VkDescriptorType.CombinedImageSampler,
            descriptorCount = MaxDescriptorSets
        };
        VkDescriptorPoolCreateInfo dpCI = new()
        {
            flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
            maxSets = MaxDescriptorSets,
            poolSizeCount = 1,
            pPoolSizes = &poolSize
        };
        deviceApi.vkCreateDescriptorPool(&dpCI, null, out var descriptorPool).CheckResult();

        VkDescriptorSetLayoutBinding binding = new()
        {
            binding = 0,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Fragment
        };
        VkDescriptorSetLayoutCreateInfo dslCI = new()
        {
            bindingCount = 1,
            pBindings = &binding
        };
        deviceApi.vkCreateDescriptorSetLayout(&dslCI, null, out var descriptorSetLayout).CheckResult();

        var setLayout = descriptorSetLayout;
        VkDescriptorSetAllocateInfo dsAI = new()
        {
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &setLayout
        };
        VkDescriptorSet descriptorSet;
        deviceApi.vkAllocateDescriptorSets(&dsAI, &descriptorSet).CheckResult();

        VkPushConstantRange pushRange = new()
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
            offset = 0,
            size = 84
        };
        VkPipelineLayoutCreateInfo plCI = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange
        };
        deviceApi.vkCreatePipelineLayout(&plCI, null, out var pipelineLayout).CheckResult();

        var ctx = new VulkanContext(
            instance, instanceApi, VkSurfaceKHR.Null,
            physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, commandPool, renderPass,
            descriptorPool, descriptorSetLayout, descriptorSet, pipelineLayout,
            vertexBufferSize, msaaSamples);

        ctx._isOffscreen = true;
        ctx._offscreenWidth = width;
        ctx._offscreenHeight = height;

        ctx.CreateSyncObjects();
        ctx.AllocateCommandBuffers();
        ctx.CreateVertexBuffers();
        ctx.CreateOffscreenTarget(width, height);

        return ctx;
    }

    private static VkPhysicalDevice PickPhysicalDeviceOffscreen(VkInstanceApi instanceApi, out uint queueFamily)
    {
        uint count = 0;
        instanceApi.vkEnumeratePhysicalDevices(&count, null);
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* pDevices = devices)
            instanceApi.vkEnumeratePhysicalDevices(&count, pDevices);

        foreach (var pd in devices)
        {
            uint qCount = 0;
            instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(pd, &qCount, null);
            var props = new VkQueueFamilyProperties[qCount];
            fixed (VkQueueFamilyProperties* pProps = props)
                instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(pd, &qCount, pProps);

            for (uint i = 0; i < qCount; i++)
            {
                if ((props[i].queueFlags & VkQueueFlags.Graphics) != 0)
                {
                    queueFamily = i;
                    return pd;
                }
            }
        }

        throw new InvalidOperationException("No suitable Vulkan physical device found (offscreen)");
    }

    private static VkRenderPass CreateOffscreenRenderPass(VkDeviceApi deviceApi, VkFormat format,
        VkSampleCountFlags msaaSamples)
    {
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
                finalLayout = VkImageLayout.ColorAttachmentOptimal // readback transitions manually
            };
            VkAttachmentReference colorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
            VkSubpassDescription subpass = new()
            {
                pipelineBindPoint = VkPipelineBindPoint.Graphics,
                colorAttachmentCount = 1,
                pColorAttachments = &colorRef
            };
            VkSubpassDependency dep = new()
            {
                srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
                srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput, srcAccessMask = 0,
                dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
                dstAccessMask = VkAccessFlags.ColorAttachmentWrite
            };
            VkRenderPassCreateInfo rpCI = new()
            {
                attachmentCount = 1, pAttachments = &colorAttachment,
                subpassCount = 1, pSubpasses = &subpass,
                dependencyCount = 1, pDependencies = &dep
            };
            deviceApi.vkCreateRenderPass(&rpCI, null, out var rp).CheckResult();
            return rp;
        }

        // MSAA: multisample color (0) resolves to offscreen image (1)
        Span<VkAttachmentDescription> attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new()
        {
            format = format, samples = msaaSamples,
            loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.ColorAttachmentOptimal
        };
        attachments[1] = new()
        {
            format = format, samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.DontCare, storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.ColorAttachmentOptimal
        };
        VkAttachmentReference msaaColorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        VkAttachmentReference resolveRef = new() { attachment = 1, layout = VkImageLayout.ColorAttachmentOptimal };
        VkSubpassDescription msaaSubpass = new()
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1, pColorAttachments = &msaaColorRef, pResolveAttachments = &resolveRef
        };
        VkSubpassDependency msaaDep = new()
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput, srcAccessMask = 0,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite
        };
        fixed (VkAttachmentDescription* pAtt = attachments)
        {
            VkRenderPassCreateInfo rpCI = new()
            {
                attachmentCount = 2, pAttachments = pAtt,
                subpassCount = 1, pSubpasses = &msaaSubpass,
                dependencyCount = 1, pDependencies = &msaaDep
            };
            deviceApi.vkCreateRenderPass(&rpCI, null, out var rp).CheckResult();
            return rp;
        }
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
