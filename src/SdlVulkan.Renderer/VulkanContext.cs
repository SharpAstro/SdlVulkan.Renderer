using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

public sealed unsafe class VulkanContext : IDisposable
{
    private const uint VertexBufferSize = 4 * 1024 * 1024; // 4MB
    private const int MaxFramesInFlight = 2;
    private const uint MaxDescriptorSets = 512; // font atlas + textures

    public VkInstance Instance { get; }
    public VkInstanceApi InstanceApi { get; }
    public VkPhysicalDevice PhysicalDevice { get; }
    public VkDevice Device { get; }
    public VkDeviceApi DeviceApi { get; }
    public VkQueue GraphicsQueue { get; }
    public uint GraphicsQueueFamily { get; }
    public VkCommandPool CommandPool { get; }
    public VkRenderPass RenderPass { get; }
    public VkDescriptorPool DescriptorPool { get; }
    public VkDescriptorSetLayout DescriptorSetLayout { get; }
    public VkDescriptorSet DescriptorSet { get; private set; }
    public VkPipelineLayout PipelineLayout { get; }

    // Swapchain state
    public VkSwapchainKHR Swapchain { get; private set; }
    public VkFormat SwapchainFormat { get; private set; }
    public uint SwapchainWidth { get; private set; }
    public uint SwapchainHeight { get; private set; }

    private VkImage[] _swapchainImages = [];
    private VkImageView[] _swapchainImageViews = [];
    private VkFramebuffer[] _framebuffers = [];

    // Per-frame sync
    private readonly VkSemaphore[] _imageAvailableSemaphores = new VkSemaphore[MaxFramesInFlight];
    private readonly VkSemaphore[] _renderFinishedSemaphores = new VkSemaphore[MaxFramesInFlight];
    private readonly VkFence[] _inFlightFences = new VkFence[MaxFramesInFlight];
    private readonly VkCommandBuffer[] _commandBuffers = new VkCommandBuffer[MaxFramesInFlight];
    private int _currentFrame;
    private uint _currentImageIndex;

    // Per-frame vertex staging buffers (avoids race between in-flight frames)
    private readonly VkBuffer[] _vertexBuffers = new VkBuffer[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _vertexMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly float*[] _vertexMapped = new float*[MaxFramesInFlight];
    private int _vertexOffset; // in floats

    private readonly VkSurfaceKHR _surface;
    private bool _disposed;

    private VulkanContext(
        VkInstance instance, VkInstanceApi instanceApi,
        VkSurfaceKHR surface,
        VkPhysicalDevice physicalDevice,
        VkDevice device, VkDeviceApi deviceApi,
        VkQueue graphicsQueue, uint graphicsQueueFamily,
        VkCommandPool commandPool, VkRenderPass renderPass,
        VkDescriptorPool descriptorPool, VkDescriptorSetLayout descriptorSetLayout,
        VkDescriptorSet descriptorSet, VkPipelineLayout pipelineLayout)
    {
        Instance = instance;
        InstanceApi = instanceApi;
        _surface = surface;
        PhysicalDevice = physicalDevice;
        Device = device;
        DeviceApi = deviceApi;
        GraphicsQueue = graphicsQueue;
        GraphicsQueueFamily = graphicsQueueFamily;
        CommandPool = commandPool;
        RenderPass = renderPass;
        DescriptorPool = descriptorPool;
        DescriptorSetLayout = descriptorSetLayout;
        DescriptorSet = descriptorSet;
        PipelineLayout = pipelineLayout;
    }

    public static VulkanContext Create(VkInstance instance, VkSurfaceKHR surface, uint width, uint height)
    {
        var instanceApi = GetApi(instance);

        // Pick physical device
        var physicalDevice = PickPhysicalDevice(instanceApi, surface, out var queueFamily);

        // Create logical device
        float queuePriority = 1.0f;
        VkDeviceQueueCreateInfo queueCI = new()
        {
            queueFamilyIndex = queueFamily,
            queueCount = 1,
            pQueuePriorities = &queuePriority
        };

        using var extensionNames = new VkStringArray([VK_KHR_SWAPCHAIN_EXTENSION_NAME]);

        VkDeviceCreateInfo deviceCI = new()
        {
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueCI,
            enabledExtensionCount = extensionNames.Length,
            ppEnabledExtensionNames = extensionNames
        };

        instanceApi.vkCreateDevice(physicalDevice, &deviceCI, null, out var device).CheckResult();
        var deviceApi = GetApi(instance, device);

        deviceApi.vkGetDeviceQueue(queueFamily, 0, out var graphicsQueue);

        // Command pool
        VkCommandPoolCreateInfo poolCI = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = queueFamily
        };
        deviceApi.vkCreateCommandPool(&poolCI, null, out var commandPool).CheckResult();

        // Render pass
        var renderPass = CreateRenderPass(deviceApi, VkFormat.B8G8R8A8Unorm);

        // Descriptor pool — large enough for font atlas + textures
        // FreeDescriptorSet flag allows individual sets to be freed when textures are evicted
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

        // Allocate the font atlas descriptor set
        var setLayout = descriptorSetLayout;
        VkDescriptorSetAllocateInfo dsAI = new()
        {
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &setLayout
        };
        VkDescriptorSet descriptorSet;
        deviceApi.vkAllocateDescriptorSets(&dsAI, &descriptorSet).CheckResult();

        // Pipeline layout with push constants (84 bytes: mat4 + vec4 + float innerRadius) + 1 descriptor set
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
            instance, instanceApi, surface, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, commandPool, renderPass,
            descriptorPool, descriptorSetLayout, descriptorSet, pipelineLayout);

        ctx.CreateSyncObjects();
        ctx.AllocateCommandBuffers();
        ctx.CreateVertexBuffers();
        ctx.CreateSwapchain(width, height);

        return ctx;
    }

    /// <summary>
    /// Allocates a new descriptor set from the pool with the shared layout.
    /// Used by VkTexture to get its own descriptor set for texture binding.
    /// </summary>
    // Descriptor pool operations need external synchronization for multi-threaded access
    private readonly Lock _descriptorPoolLock = new();

    public VkDescriptorSet AllocateDescriptorSet()
    {
        lock (_descriptorPoolLock)
        {
            var layout = DescriptorSetLayout;
            VkDescriptorSetAllocateInfo dsAI = new()
            {
                descriptorPool = DescriptorPool,
                descriptorSetCount = 1,
                pSetLayouts = &layout
            };
            VkDescriptorSet set;
            DeviceApi.vkAllocateDescriptorSets(&dsAI, &set).CheckResult();
            return set;
        }
    }

    /// <summary>
    /// Frees a descriptor set back to the pool.
    /// </summary>
    public void FreeDescriptorSet(VkDescriptorSet set)
    {
        lock (_descriptorPoolLock)
        {
            DeviceApi.vkFreeDescriptorSets(DescriptorPool, 1, &set);
        }
    }

    /// <summary>
    /// Updates any descriptor set to point to the given image view and sampler.
    /// </summary>
    public void UpdateDescriptorSet(VkDescriptorSet targetSet, VkImageView imageView, VkSampler sampler)
    {
        VkDescriptorImageInfo imageInfo = new()
        {
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            imageView = imageView,
            sampler = sampler
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = targetSet,
            dstBinding = 0,
            dstArrayElement = 0,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            pImageInfo = &imageInfo
        };
        DeviceApi.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    public void RecreateSwapchain(uint width, uint height)
    {
        DeviceApi.vkDeviceWaitIdle();
        CleanupSwapchain();
        CreateSwapchain(width, height);
    }

    public VkCommandBuffer BeginFrame(out bool resized)
    {
        resized = false;
        var fence = _inFlightFences[_currentFrame];
        DeviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);

        var result = DeviceApi.vkAcquireNextImageKHR(Swapchain, ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame], VkFence.Null, out _currentImageIndex);

        if (result == VkResult.ErrorOutOfDateKHR)
        {
            resized = true;
            return VkCommandBuffer.Null;
        }

        DeviceApi.vkResetFences(1, &fence);

        var cmd = _commandBuffers[_currentFrame];
        DeviceApi.vkResetCommandBuffer(cmd, 0);

        VkCommandBufferBeginInfo beginInfo = new()
        {
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };
        DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo);

        // Reset vertex offset for this frame
        _vertexOffset = 0;

        return cmd;
    }

    public void BeginRenderPass(VkCommandBuffer cmd, float clearR, float clearG, float clearB, float clearA)
    {
        VkClearValue clear = new();
        clear.color = new VkClearColorValue(clearR, clearG, clearB, clearA);

        VkRenderPassBeginInfo rpBI = new()
        {
            renderPass = RenderPass,
            framebuffer = _framebuffers[_currentImageIndex],
            renderArea = new VkRect2D(0, 0, SwapchainWidth, SwapchainHeight),
            clearValueCount = 1,
            pClearValues = &clear
        };

        DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);

        // Set dynamic viewport and scissor
        VkViewport viewport = new(0, 0, SwapchainWidth, SwapchainHeight, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, viewport);
        VkRect2D scissor = new(0, 0, SwapchainWidth, SwapchainHeight);
        DeviceApi.vkCmdSetScissor(cmd, 0, scissor);
    }

    public void EndFrame(VkCommandBuffer cmd)
    {
        DeviceApi.vkCmdEndRenderPass(cmd);
        DeviceApi.vkEndCommandBuffer(cmd);

        var waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        var signalSemaphore = _renderFinishedSemaphores[_currentFrame];
        VkPipelineStageFlags waitStage = VkPipelineStageFlags.ColorAttachmentOutput;

        VkSubmitInfo submitInfo = new()
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &waitSemaphore,
            pWaitDstStageMask = &waitStage,
            commandBufferCount = 1,
            pCommandBuffers = &cmd,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &signalSemaphore
        };

        DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &submitInfo, _inFlightFences[_currentFrame]).CheckResult();

        var swapchain = Swapchain;
        var imageIndex = _currentImageIndex;
        VkPresentInfoKHR presentInfo = new()
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &signalSemaphore,
            swapchainCount = 1,
            pSwapchains = &swapchain,
            pImageIndices = &imageIndex
        };

        DeviceApi.vkQueuePresentKHR(GraphicsQueue, &presentInfo);
        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    [Conditional("DEBUG")]
    private static void DebugLogBufferFull(int vertexOffset, int requestLength)
    {
        var usedMB = (vertexOffset * sizeof(float)) / (1024f * 1024f);
        var requestKB = (requestLength * sizeof(float)) / 1024f;
        Console.Error.WriteLine($"[VkBuffer] FULL at {usedMB:F1}MB, rejected {requestKB:F0}KB write");
    }

    public uint WriteVertices(ReadOnlySpan<float> data)
    {
        var maxFloats = (int)(VertexBufferSize / sizeof(float));
        if (_vertexOffset + data.Length > maxFloats)
        {
            DebugLogBufferFull(_vertexOffset, data.Length);
            return uint.MaxValue;
        }

        var byteOffset = (uint)(_vertexOffset * sizeof(float));
        data.CopyTo(new Span<float>(_vertexMapped[_currentFrame] + _vertexOffset, data.Length));
        _vertexOffset += data.Length;
        return byteOffset;
    }

    public VkBuffer VertexBuffer => _vertexBuffers[_currentFrame];

    public void ExecuteOneShot(Action<VkCommandBuffer> action)
    {
        DeviceApi.vkAllocateCommandBuffer(CommandPool, out var cmd).CheckResult();

        VkCommandBufferBeginInfo beginInfo = new()
        {
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };
        DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo);
        action(cmd);
        DeviceApi.vkEndCommandBuffer(cmd);

        VkSubmitInfo submitInfo = new()
        {
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };
        DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &submitInfo, VkFence.Null).CheckResult();
        DeviceApi.vkQueueWaitIdle(GraphicsQueue);
        DeviceApi.vkFreeCommandBuffers(CommandPool, cmd);
    }

    public uint FindMemoryType(uint typeFilter, VkMemoryPropertyFlags properties)
    {
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out var memProperties);
        for (uint i = 0; i < memProperties.memoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProperties.memoryTypes[(int)i].propertyFlags & properties) == properties)
                return i;
        }
        throw new InvalidOperationException("Failed to find suitable memory type");
    }

    /// <summary>
    /// Creates a persistent vertex buffer with the given data. The buffer lives until explicitly destroyed.
    /// Thread-safe — can be called from background tessellation tasks.
    /// </summary>
    public (VkBuffer Buffer, VkDeviceMemory Memory) CreatePersistentVertexBuffer(ReadOnlySpan<float> data)
    {
        var size = (ulong)(data.Length * sizeof(float));

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.VertexBuffer,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateBuffer(&bufCI, null, out var buffer).CheckResult();

        DeviceApi.vkGetBufferMemoryRequirements(buffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out var memory).CheckResult();
        DeviceApi.vkBindBufferMemory(buffer, memory, 0);

        void* mapped;
        DeviceApi.vkMapMemory(memory, 0, size, 0, &mapped);
        fixed (float* pData = data)
            System.Buffer.MemoryCopy(pData, mapped, (long)size, (long)size);
        DeviceApi.vkUnmapMemory(memory);

        return (buffer, memory);
    }

    public void DestroyBuffer(VkBuffer buffer, VkDeviceMemory memory)
    {
        DeviceApi.vkDestroyBuffer(buffer);
        DeviceApi.vkFreeMemory(memory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DeviceApi.vkDeviceWaitIdle();

        CleanupSwapchain();

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vertexBuffers[i] != VkBuffer.Null)
            {
                DeviceApi.vkUnmapMemory(_vertexMemories[i]);
                DeviceApi.vkDestroyBuffer(_vertexBuffers[i]);
                DeviceApi.vkFreeMemory(_vertexMemories[i]);
            }
        }

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkDestroySemaphore(_imageAvailableSemaphores[i]);
            DeviceApi.vkDestroySemaphore(_renderFinishedSemaphores[i]);
            DeviceApi.vkDestroyFence(_inFlightFences[i]);
        }

        DeviceApi.vkDestroyPipelineLayout(PipelineLayout);
        DeviceApi.vkDestroyDescriptorSetLayout(DescriptorSetLayout);
        DeviceApi.vkDestroyDescriptorPool(DescriptorPool);
        DeviceApi.vkDestroyRenderPass(RenderPass);
        DeviceApi.vkDestroyCommandPool(CommandPool);
        DeviceApi.vkDestroyDevice();

        InstanceApi.vkDestroySurfaceKHR(_surface);
        InstanceApi.vkDestroyInstance();
    }

    private static VkPhysicalDevice PickPhysicalDevice(VkInstanceApi instanceApi, VkSurfaceKHR surface, out uint queueFamily)
    {
        uint count = 0;
        instanceApi.vkEnumeratePhysicalDevices(&count, null);
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* pDevices = devices)
            instanceApi.vkEnumeratePhysicalDevices(&count, pDevices);

        foreach (var pd in devices)
        {
            if (TryFindGraphicsQueue(instanceApi, pd, surface, out var family))
            {
                queueFamily = family;
                return pd;
            }
        }

        throw new InvalidOperationException("No suitable Vulkan physical device found");
    }

    private static bool TryFindGraphicsQueue(VkInstanceApi instanceApi, VkPhysicalDevice device, VkSurfaceKHR surface, out uint family)
    {
        uint count = 0;
        instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var props = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* pProps = props)
            instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, pProps);

        for (uint i = 0; i < count; i++)
        {
            if ((props[i].queueFlags & VkQueueFlags.Graphics) == 0) continue;

            instanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(device, i, surface, out var supported);
            if (supported)
            {
                family = i;
                return true;
            }
        }

        family = 0;
        return false;
    }

    private static VkRenderPass CreateRenderPass(VkDeviceApi deviceApi, VkFormat format)
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
            finalLayout = VkImageLayout.PresentSrcKHR
        };

        VkAttachmentReference colorRef = new()
        {
            attachment = 0,
            layout = VkImageLayout.ColorAttachmentOptimal
        };

        VkSubpassDescription subpass = new()
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef
        };

        VkSubpassDependency dependency = new()
        {
            srcSubpass = VK_SUBPASS_EXTERNAL,
            dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            srcAccessMask = 0,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite
        };

        VkRenderPassCreateInfo rpCI = new()
        {
            attachmentCount = 1,
            pAttachments = &colorAttachment,
            subpassCount = 1,
            pSubpasses = &subpass,
            dependencyCount = 1,
            pDependencies = &dependency
        };

        deviceApi.vkCreateRenderPass(&rpCI, null, out var renderPass).CheckResult();
        return renderPass;
    }

    private void CreateSwapchain(uint width, uint height)
    {
        InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(PhysicalDevice, _surface, out var caps);

        var extent = caps.currentExtent;
        if (extent.width == uint.MaxValue)
        {
            extent.width = Math.Clamp(width, caps.minImageExtent.width, caps.maxImageExtent.width);
            extent.height = Math.Clamp(height, caps.minImageExtent.height, caps.maxImageExtent.height);
        }

        var imageCount = caps.minImageCount + 1;
        if (caps.maxImageCount > 0 && imageCount > caps.maxImageCount)
            imageCount = caps.maxImageCount;

        var format = VkFormat.B8G8R8A8Unorm;
        SwapchainFormat = format;
        SwapchainWidth = extent.width;
        SwapchainHeight = extent.height;

        VkSwapchainCreateInfoKHR swapCI = new()
        {
            surface = _surface,
            minImageCount = imageCount,
            imageFormat = format,
            imageColorSpace = VkColorSpaceKHR.SrgbNonLinear,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = caps.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = VkPresentModeKHR.Fifo,
            clipped = true,
            oldSwapchain = VkSwapchainKHR.Null
        };

        DeviceApi.vkCreateSwapchainKHR(&swapCI, null, out var swapchain).CheckResult();
        Swapchain = swapchain;

        // Get swapchain images
        DeviceApi.vkGetSwapchainImagesKHR(Swapchain, out uint imgCount).CheckResult();
        Span<VkImage> images = stackalloc VkImage[(int)imgCount];
        DeviceApi.vkGetSwapchainImagesKHR(Swapchain, images).CheckResult();
        _swapchainImages = images.ToArray();

        // Create image views
        _swapchainImageViews = new VkImageView[imgCount];
        for (var i = 0; i < imgCount; i++)
        {
            var viewCI = new VkImageViewCreateInfo(
                _swapchainImages[i],
                VkImageViewType.Image2D,
                format,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&viewCI, null, out _swapchainImageViews[i]).CheckResult();
        }

        // Create framebuffers
        _framebuffers = new VkFramebuffer[imgCount];
        for (var i = 0; i < imgCount; i++)
        {
            var attachment = _swapchainImageViews[i];
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = RenderPass,
                attachmentCount = 1,
                pAttachments = &attachment,
                width = extent.width,
                height = extent.height,
                layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out _framebuffers[i]).CheckResult();
        }
    }

    private void CleanupSwapchain()
    {
        foreach (var fb in _framebuffers)
            DeviceApi.vkDestroyFramebuffer(fb);
        foreach (var iv in _swapchainImageViews)
            DeviceApi.vkDestroyImageView(iv);
        if (Swapchain != VkSwapchainKHR.Null)
            DeviceApi.vkDestroySwapchainKHR(Swapchain);

        _framebuffers = [];
        _swapchainImageViews = [];
        _swapchainImages = [];
        Swapchain = VkSwapchainKHR.Null;
    }

    private void CreateSyncObjects()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkCreateSemaphore(out _imageAvailableSemaphores[i]).CheckResult();
            DeviceApi.vkCreateSemaphore(out _renderFinishedSemaphores[i]).CheckResult();
            DeviceApi.vkCreateFence(VkFenceCreateFlags.Signaled, out _inFlightFences[i]).CheckResult();
        }
    }

    private void AllocateCommandBuffers()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkAllocateCommandBuffer(CommandPool, out _commandBuffers[i]).CheckResult();
        }
    }

    private void CreateVertexBuffers()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            VkBufferCreateInfo bufCI = new()
            {
                size = VertexBufferSize,
                usage = VkBufferUsageFlags.VertexBuffer,
                sharingMode = VkSharingMode.Exclusive
            };
            DeviceApi.vkCreateBuffer(&bufCI, null, out _vertexBuffers[i]).CheckResult();

            DeviceApi.vkGetBufferMemoryRequirements(_vertexBuffers[i], out var memReqs);
            VkMemoryAllocateInfo allocInfo = new()
            {
                allocationSize = memReqs.size,
                memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
            };
            DeviceApi.vkAllocateMemory(&allocInfo, null, out _vertexMemories[i]).CheckResult();
            DeviceApi.vkBindBufferMemory(_vertexBuffers[i], _vertexMemories[i], 0);

            void* mapped;
            DeviceApi.vkMapMemory(_vertexMemories[i], 0, VertexBufferSize, 0, &mapped);
            _vertexMapped[i] = (float*)mapped;
        }
    }
}
