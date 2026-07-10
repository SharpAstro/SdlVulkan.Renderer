using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Device-level Vulkan state shared across one or more <see cref="VulkanContext"/> windows:
/// the physical/logical device, graphics queue, command pool, render pass, descriptor pool +
/// layout + the fixed font-atlas descriptor set, and the shared 84-byte push-constant pipeline
/// layout. Created once per <see cref="VkInstance"/> — from a window's surface for on-screen
/// rendering (<see cref="Create"/>) or headless via <see cref="CreateOffscreen"/>.
/// <para>
/// Everything keyed off a device rather than a swapchain (font atlases, SDF atlases, textures,
/// pipelines) is built against a <c>VulkanDevice</c> so a single set of those resources can be
/// reused by every window that shares this device. A <see cref="VulkanContext"/> holds one of
/// these and forwards the device-level members; it owns the device only when it created it
/// (single-window / offscreen), so multiple windows can share one device without double-free.
/// </para>
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
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
    public VkDescriptorSet DescriptorSet { get; }
    public VkPipelineLayout PipelineLayout { get; }

    /// <summary>
    /// True when the GPU is known wedged — the owning <see cref="VulkanContext"/>'s per-frame fence
    /// has been timing out. The context is the sole writer (it mirrors its own fence-stuck state
    /// here). Device-level teardown and cross-component render-thread drains consult this to skip an
    /// unbounded <c>vkDeviceWaitIdle</c> that would otherwise hang the UI thread on a stuck device.
    /// For a device shared by several windows this reflects the most recent context update; the
    /// single-window host (TianWen) is exact.
    /// </summary>
    public bool IsGpuStuck { get; internal set; }

    /// <summary>MSAA sample count (Count1 = no MSAA). Uniform across all windows on this device —
    /// the render pass and the pre-baked pipelines bake it in, so every swapchain sharing this
    /// device renders at the same sample count.</summary>
    public VkSampleCountFlags MsaaSamples { get; }

    private uint _maxImageDimension2D;
    /// <summary>Device <c>maxImageDimension2D</c> limit (queried lazily, then cached). Consumers cap
    /// atlas/texture sizes against this so they never request an image larger than the GPU allows.</summary>
    public uint MaxImageDimension2D
    {
        get
        {
            if (_maxImageDimension2D == 0)
            {
                InstanceApi.vkGetPhysicalDeviceProperties(PhysicalDevice, out var props);
                _maxImageDimension2D = props.limits.maxImageDimension2D;
            }
            return _maxImageDimension2D;
        }
    }

    // Descriptor pool operations need external synchronization for multi-threaded access
    private readonly Lock _descriptorPoolLock = new();
    // Whether this device's Dispose also destroys the VkInstance. True on the standalone and
    // offscreen paths (the device was handed an instance it's expected to tear down). False under
    // SdlVulkanApp, which owns the instance and shares one device across windows — there the app
    // destroys the instance after the device is gone.
    private readonly bool _ownsInstance;
    private bool _disposed;

    private VulkanDevice(
        VkInstance instance, VkInstanceApi instanceApi,
        VkPhysicalDevice physicalDevice, VkDevice device, VkDeviceApi deviceApi,
        VkQueue graphicsQueue, uint graphicsQueueFamily,
        VkCommandPool commandPool, VkRenderPass renderPass,
        VkDescriptorPool descriptorPool, VkDescriptorSetLayout descriptorSetLayout,
        VkDescriptorSet descriptorSet, VkPipelineLayout pipelineLayout,
        VkSampleCountFlags msaaSamples, bool ownsInstance)
    {
        _ownsInstance = ownsInstance;
        Instance = instance;
        InstanceApi = instanceApi;
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
        MsaaSamples = msaaSamples;
    }

    /// <summary>
    /// Creates a device for on-screen rendering. <paramref name="surface"/> is a probe used only to
    /// pick a present-capable queue family; the device requests <c>VK_KHR_swapchain</c>. The same
    /// device can then back multiple <see cref="VulkanContext"/> windows (each with its own surface),
    /// provided they share this instance and the swapchain format/MSAA the render pass bakes in.
    /// </summary>
    public static VulkanDevice Create(VkInstance instance, VkSurfaceKHR surface,
        VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1, bool ownsInstance = true)
    {
        var instanceApi = GetApi(instance);
        var physicalDevice = PickPhysicalDevice(instanceApi, surface, out var queueFamily);

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

        // Swapchain render pass — final layout PresentSrcKHR.
        var renderPass = CreateRenderPass(deviceApi, VkFormat.B8G8R8A8Unorm, msaaSamples);

        return CreateCommon(instance, instanceApi, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, renderPass, msaaSamples, ownsInstance);
    }

    /// <summary>
    /// Creates a headless device with no surface and no <c>VK_KHR_swapchain</c> — pairs with
    /// <see cref="VulkanContext.CreateOffscreen"/>. Its render pass leaves the color attachment in
    /// <c>ColorAttachmentOptimal</c> so the image can be transitioned for readback.
    /// </summary>
    public static VulkanDevice CreateOffscreen(VkInstance instance,
        VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1, bool ownsInstance = true)
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
        // surface extensions enabled.
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

        var renderPass = CreateOffscreenRenderPass(deviceApi, VkFormat.B8G8R8A8Unorm, msaaSamples);

        return CreateCommon(instance, instanceApi, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, renderPass, msaaSamples, ownsInstance);
    }

    // Shared tail of both factories: command pool, descriptor pool/layout/set, pipeline layout.
    // Identical on the swapchain and offscreen paths so VkPipelineSet's pre-baked pipelines and the
    // 84-byte push-constant layout work in either mode — the only per-mode difference is the render
    // pass (passed in) and the physical-device pick / swapchain extension (done by the callers).
    private static VulkanDevice CreateCommon(
        VkInstance instance, VkInstanceApi instanceApi,
        VkPhysicalDevice physicalDevice, VkDevice device, VkDeviceApi deviceApi,
        VkQueue graphicsQueue, uint queueFamily, VkRenderPass renderPass,
        VkSampleCountFlags msaaSamples, bool ownsInstance)
    {
        // Command pool
        VkCommandPoolCreateInfo poolCI = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = queueFamily
        };
        deviceApi.vkCreateCommandPool(&poolCI, null, out var commandPool).CheckResult();

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

        return new VulkanDevice(
            instance, instanceApi, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, commandPool, renderPass,
            descriptorPool, descriptorSetLayout, descriptorSet, pipelineLayout, msaaSamples, ownsInstance);
    }

    /// <summary>
    /// Allocates a new descriptor set from the pool with the shared layout.
    /// Used by VkTexture to get its own descriptor set for texture binding.
    /// </summary>
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

    // A device's memory properties never change — query the 504-byte struct once instead of
    // round-tripping into the ICD on every buffer/image allocation.
    private VkPhysicalDeviceMemoryProperties _memProperties;
    private bool _memPropertiesCached;

    public uint FindMemoryType(uint typeFilter, VkMemoryPropertyFlags properties)
    {
        if (!_memPropertiesCached)
        {
            InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out _memProperties);
            _memPropertiesCached = true;
        }
        for (uint i = 0; i < _memProperties.memoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (_memProperties.memoryTypes[(int)i].propertyFlags & properties) == properties)
                return i;
        }
        throw new InvalidOperationException("Failed to find suitable memory type");
    }

    public void ExecuteOneShot(Action<VkCommandBuffer> action)
    {
        DeviceApi.vkAllocateCommandBuffer(CommandPool, out var cmd).CheckResult();

        VkCommandBufferBeginInfo beginInfo = new()
        {
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };
        // Check Begin/End: when these silently fail (bad cmd-pool flags, driver
        // state corruption from a prior submit, etc.) the next submit blows up
        // with a misleading error code. Surface the real first failure here.
        DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();
        action(cmd);
        DeviceApi.vkEndCommandBuffer(cmd).CheckResult();

        VkSubmitInfo submitInfo = new()
        {
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };
        DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &submitInfo, VkFence.Null).CheckResult();
        DeviceApi.vkQueueWaitIdle(GraphicsQueue).CheckResult();
        DeviceApi.vkFreeCommandBuffers(CommandPool, cmd);
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

    /// <summary>
    /// Blocks until the device has finished all submitted work on every queue. Because one
    /// <see cref="VulkanDevice"/> is shared across all of an <see cref="SdlVulkanApp"/>'s windows, this
    /// is the safe quiesce point before moving a document's GPU resources (persistent vertex buffers,
    /// image textures) from one window to another — e.g. tearing a tab out into its own window. Once
    /// this returns, no in-flight command buffer from either window can still reference those resources,
    /// so re-binding them to the destination window's renderer is race-free.
    /// </summary>
    public void WaitIdle() => DeviceApi.vkDeviceWaitIdle();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain any in-flight work before tearing down device-level objects. Safe to call again
        // here even if an owning context already waited — vkDeviceWaitIdle is idempotent. Skip it
        // when the GPU is known wedged: an unbounded wait on a stuck device would hang the quit
        // (the "Not responding" failure mode the recovery path was hardened against).
        if (!IsGpuStuck)
        {
            DeviceApi.vkDeviceWaitIdle();
        }

        DeviceApi.vkDestroyPipelineLayout(PipelineLayout);
        DeviceApi.vkDestroyDescriptorSetLayout(DescriptorSetLayout);
        DeviceApi.vkDestroyDescriptorPool(DescriptorPool);
        DeviceApi.vkDestroyRenderPass(RenderPass);
        DeviceApi.vkDestroyCommandPool(CommandPool);
        DeviceApi.vkDestroyDevice();

        // Only destroy the instance if we own it (standalone / offscreen). Under SdlVulkanApp the
        // instance outlives this device — it backs the surfaces of any sibling windows — so the app
        // tears it down after the last device is gone.
        if (_ownsInstance)
        {
            VulkanValidation.DestroyMessenger(Instance, InstanceApi);
            InstanceApi.vkDestroyInstance();
        }
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

    private static VkRenderPass CreateRenderPass(VkDeviceApi deviceApi, VkFormat format,
        VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1)
    {
        if (msaaSamples == VkSampleCountFlags.Count1)
        {
            // No MSAA — single color attachment
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

            VkAttachmentReference colorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };

            VkSubpassDescription subpass = new()
            {
                pipelineBindPoint = VkPipelineBindPoint.Graphics,
                colorAttachmentCount = 1,
                pColorAttachments = &colorRef
            };

            VkSubpassDependency dependency = new()
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
                dependencyCount = 1, pDependencies = &dependency
            };

            deviceApi.vkCreateRenderPass(&rpCI, null, out var rp).CheckResult();
            return rp;
        }

        // MSAA — multisample color attachment (0) + resolve to swapchain (1)
        Span<VkAttachmentDescription> attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = new() // multisample color
        {
            format = format,
            samples = msaaSamples,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare, // resolved, no need to store
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.ColorAttachmentOptimal
        };
        attachments[1] = new() // resolve target (swapchain image)
        {
            format = format,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.PresentSrcKHR
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

        VkSubpassDependency msaaDep = new()
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput, srcAccessMask = 0,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite
        };

        fixed (VkAttachmentDescription* pAttachments = attachments)
        {
            VkRenderPassCreateInfo msaaRpCI = new()
            {
                attachmentCount = 2, pAttachments = pAttachments,
                subpassCount = 1, pSubpasses = &msaaSubpass,
                dependencyCount = 1, pDependencies = &msaaDep
            };

            deviceApi.vkCreateRenderPass(&msaaRpCI, null, out var renderPass).CheckResult();
            return renderPass;
        }
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
}
