using System.Diagnostics;
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
    // Capacity of ONE descriptor pool, not a global ceiling: when a pool fills, another is added
    // (see AllocateDescriptorSet). A pool cannot be resized once created, so growth is always by
    // chaining — existing sets keep their handles and stay valid across a grow.
    private const uint DescriptorSetsPerPool = 512; // font atlas + textures
    // The widest set this device allocates: a masked set binds a texture and a mask. Pool descriptor
    // counts are sized by it so every pool can actually deliver its stated number of sets.
    private const uint MaxDescriptorsPerSet = 2;

    public VkInstance Instance { get; }
    public VkInstanceApi InstanceApi { get; }
    public VkPhysicalDevice PhysicalDevice { get; }
    public VkDevice Device { get; }
    public VkDeviceApi DeviceApi { get; }
    public VkQueue GraphicsQueue { get; }
    public uint GraphicsQueueFamily { get; }
    public VkCommandPool CommandPool { get; }
    public VkRenderPass RenderPass { get; }

    /// <summary>The color format the render pass — and therefore the swapchain images — use. Chosen
    /// from the surface's supported formats on the swapchain path (B8G8R8A8Unorm on desktop for
    /// readback/offscreen byte-order parity; R8G8B8A8Unorm on Android/Mali, which offers no BGRA);
    /// fixed to B8G8R8A8Unorm on the offscreen path. Render pass and swapchain MUST agree on it.</summary>
    public VkFormat ColorFormat { get; }

    /// <summary>The pool the next descriptor set will be taken from. Not the only one — see
    /// <see cref="AllocateDescriptorSet"/>; kept single-valued for callers that just need a
    /// representative handle.</summary>
    public VkDescriptorPool DescriptorPool => _currentPool;

    /// <summary>One linear/clamp-to-edge sampler shared by every <see cref="VkTexture"/>. Textures
    /// used to create their own identical sampler each, which put a page of a few thousand small
    /// images straight into <c>maxSamplerAllocationCount</c> (commonly 4096) for no benefit —
    /// samplers carry no per-image state.</summary>
    public VkSampler LinearClampSampler { get; }

    public VkDescriptorSetLayout DescriptorSetLayout { get; }
    public VkDescriptorSet DescriptorSet { get; }
    public VkPipelineLayout PipelineLayout { get; }

    /// <summary>Layout for a texture-plus-mask set: binding 0 the texture, binding 1 the coverage
    /// mask. Bound by <see cref="VkPipelineSet.MaskedPipeline"/> and nothing else, which is what
    /// keeps <see cref="DescriptorSetLayout"/> a single sampler for every other textured draw.</summary>
    public VkDescriptorSetLayout MaskedDescriptorSetLayout { get; }

    /// <summary>Pipeline layout over <see cref="MaskedDescriptorSetLayout"/>, with the same 84-byte
    /// push constants as <see cref="PipelineLayout"/>.</summary>
    public VkPipelineLayout MaskedPipelineLayout { get; }

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

    /// <summary>
    /// The depth format every render pass on this device carries as its depth attachment, chosen once
    /// from what the physical device supports (see <see cref="ChooseDepthFormat"/>). Uniform for the
    /// same reason <see cref="MsaaSamples"/> is: the pre-baked pipelines are created against it.
    /// </summary>
    public VkFormat DepthFormat { get; }

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
    // Every pool created so far, in creation order — all destroyed together at teardown.
    private readonly List<VkDescriptorPool> _descriptorPools = new();
    // Sets handed back by FreeDescriptorSet, ready to be re-issued. Every set in this device has
    // the SAME single-combined-image-sampler layout, so a returned set is re-pointed at another
    // image by UpdateDescriptorSet and reused as-is. That is why nothing here calls
    // vkFreeDescriptorSets: recycling keeps the handle valid (strictly safer than freeing a set a
    // command buffer might still reference) and means the pool count tracks PEAK live sets rather
    // than growing with churn.
    private readonly Stack<VkDescriptorSet> _freeDescriptorSets = new();
    // Masked sets recycle separately: see AllocateMaskedDescriptorSet for why the two cannot mix.
    private readonly Stack<VkDescriptorSet> _freeMaskedDescriptorSets = new();
    private VkDescriptorPool _currentPool;
    private uint _setsLeftInCurrentPool;
    // Whether this device's Dispose also destroys the VkInstance. True on the standalone and
    // offscreen paths (the device was handed an instance it's expected to tear down). False under
    // SdlVulkanApp, which owns the instance and shares one device across windows — there the app
    // destroys the instance after the device is gone.
    private readonly bool _ownsInstance;
    // Headless: no swapchain, no window loop, so this device's queue is reached only by whatever job
    // happens to be running. See AssertQueueThread.
    private bool _privateQueue;
    private bool _disposed;

    private VulkanDevice(
        VkInstance instance, VkInstanceApi instanceApi,
        VkPhysicalDevice physicalDevice, VkDevice device, VkDeviceApi deviceApi,
        VkQueue graphicsQueue, uint graphicsQueueFamily,
        VkCommandPool commandPool, VkRenderPass renderPass,
        VkDescriptorPool descriptorPool, VkDescriptorSetLayout descriptorSetLayout,
        VkDescriptorSet descriptorSet, VkPipelineLayout pipelineLayout,
        VkDescriptorSetLayout maskedSetLayout, VkPipelineLayout maskedPipelineLayout,
        VkFormat colorFormat, VkFormat depthFormat, VkSampleCountFlags msaaSamples, bool ownsInstance)
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
        ColorFormat = colorFormat;
        _descriptorPools.Add(descriptorPool);
        _currentPool = descriptorPool;
        // Create() already took the shared set below out of this pool.
        _setsLeftInCurrentPool = DescriptorSetsPerPool - 1;
        DescriptorSetLayout = descriptorSetLayout;
        DescriptorSet = descriptorSet;
        PipelineLayout = pipelineLayout;
        MaskedDescriptorSetLayout = maskedSetLayout;
        MaskedPipelineLayout = maskedPipelineLayout;
        MsaaSamples = msaaSamples;
        DepthFormat = depthFormat;

        // Linear filtering, clamp to edge, no mips — the settings every VkTexture used to ask for
        // individually.
        VkSamplerCreateInfo samplerCI = new()
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            mipmapMode = VkSamplerMipmapMode.Linear,
            maxLod = 1.0f
        };
        deviceApi.vkCreateSampler(&samplerCI, null, out var sharedSampler).CheckResult();
        LinearClampSampler = sharedSampler;
    }

    /// <summary>
    /// Creates a device for on-screen rendering. <paramref name="surface"/> is a probe used only to
    /// pick a present-capable queue family; the device requests <c>VK_KHR_swapchain</c>. The same
    /// device can then back multiple <see cref="VulkanContext"/> windows (each with its own surface),
    /// provided they share this instance and the swapchain format, depth format and MSAA the render
    /// pass bakes in.
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

        // Pick a color format the surface actually supports; the render pass and swapchain images must
        // agree, so choose it once here. Desktop offers BGRA (kept for readback/offscreen byte-order
        // parity); Android/Mali offers only RGBA.
        var colorFormat = PickSurfaceColorFormat(instanceApi, physicalDevice, surface);
        var depthFormat = ChooseDepthFormat(instanceApi, physicalDevice);

        // Swapchain render pass — clears, and leaves the presented image in PresentSrcKHR.
        var renderPass = CreateCompatibleRenderPass(deviceApi, colorFormat, depthFormat, msaaSamples,
            VkAttachmentLoadOp.Clear, VkImageLayout.Undefined, VkImageLayout.PresentSrcKHR);

        return CreateCommon(instance, instanceApi, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, renderPass, colorFormat, depthFormat, msaaSamples, ownsInstance);
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

        var depthFormat = ChooseDepthFormat(instanceApi, physicalDevice);
        // Offscreen render pass — clears, and leaves the image in ColorAttachmentOptimal so the readback
        // can transition it for the copy itself.
        var renderPass = CreateCompatibleRenderPass(deviceApi, VkFormat.B8G8R8A8Unorm, depthFormat, msaaSamples,
            VkAttachmentLoadOp.Clear, VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal);

        var dev = CreateCommon(instance, instanceApi, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, renderPass, VkFormat.B8G8R8A8Unorm, depthFormat, msaaSamples, ownsInstance);
        dev.MarkQueuePrivate();
        return dev;
    }

    // Shared tail of both factories: command pool, descriptor pool/layout/set, pipeline layout.
    // Identical on the swapchain and offscreen paths so VkPipelineSet's pre-baked pipelines and the
    // 84-byte push-constant layout work in either mode — the only per-mode difference is the render
    // pass (passed in) and the physical-device pick / swapchain extension (done by the callers).
    private static VulkanDevice CreateCommon(
        VkInstance instance, VkInstanceApi instanceApi,
        VkPhysicalDevice physicalDevice, VkDevice device, VkDeviceApi deviceApi,
        VkQueue graphicsQueue, uint queueFamily, VkRenderPass renderPass,
        VkFormat colorFormat, VkFormat depthFormat, VkSampleCountFlags msaaSamples, bool ownsInstance)
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
            // Two per set, not one: a masked set (see MaskedDescriptorSetLayout) binds a texture AND
            // a mask, so a pool whose descriptor count matched its set count would run out of
            // descriptors at half its stated set capacity. The allocator recovers from that -- an
            // OutOfPoolMemory refusal just retires the pool -- but it would retire every pool at
            // half use, doubling the pools a masked-heavy page creates for no reason.
            descriptorCount = DescriptorSetsPerPool * MaxDescriptorsPerSet
        };
        VkDescriptorPoolCreateInfo dpCI = new()
        {
            flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
            maxSets = DescriptorSetsPerPool,
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

        // A SECOND layout, texture plus mask, for VkPipelineSet.MaskedPipeline. Additive rather than
        // a binding added to the layout above, because that one is shared by every textured pipeline
        // in the renderer -- the font atlas included -- so widening it would oblige every ordinary
        // draw to bind a mask it does not use.
        var maskedBindings = stackalloc VkDescriptorSetLayoutBinding[2];
        maskedBindings[0] = new()
        {
            binding = 0,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Fragment
        };
        maskedBindings[1] = new()
        {
            binding = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Fragment
        };
        VkDescriptorSetLayoutCreateInfo maskedDslCI = new()
        {
            bindingCount = 2,
            pBindings = maskedBindings
        };
        deviceApi.vkCreateDescriptorSetLayout(&maskedDslCI, null, out var maskedSetLayout).CheckResult();

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

        // Same push constants, the masked set layout: a masked pipeline is the ordinary textured one
        // with a second sampler, so everything the shaders read through push constants is unchanged.
        var maskedLayout = maskedSetLayout;
        VkPipelineLayoutCreateInfo maskedPlCI = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &maskedLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange
        };
        deviceApi.vkCreatePipelineLayout(&maskedPlCI, null, out var maskedPipelineLayout).CheckResult();

        return new VulkanDevice(
            instance, instanceApi, physicalDevice, device, deviceApi,
            graphicsQueue, queueFamily, commandPool, renderPass,
            descriptorPool, descriptorSetLayout, descriptorSet, pipelineLayout,
            maskedSetLayout, maskedPipelineLayout, colorFormat, depthFormat,
            msaaSamples, ownsInstance);
    }

    /// <summary>
    /// Allocates a new descriptor set from the pool with the shared layout.
    /// Used by VkTexture to get its own descriptor set for texture binding.
    /// </summary>
    public VkDescriptorSet AllocateDescriptorSet()
    {
        lock (_descriptorPoolLock)
        {
            // Recycle first: a returned set is indistinguishable from a fresh one here, since all
            // sets share the single-combined-image-sampler layout.
            if (_freeDescriptorSets.Count > 0) return _freeDescriptorSets.Pop();

            var set = TryAllocateFromCurrentPool();
            if (set == VkDescriptorSet.Null)
            {
                // The pool is full. It cannot be enlarged, so add another and take from that. A
                // fixed pool used to be a hard ceiling on how many textures a document could have:
                // a page carrying a few thousand small images exhausted it, and because the glyph
                // atlas draws from the same pool, TEXT could be the thing that got refused.
                AddDescriptorPool();
                set = TryAllocateFromCurrentPool();
                if (set == VkDescriptorSet.Null)
                    throw new InvalidOperationException(
                        "descriptor set allocation failed against a freshly created pool");
            }
            return set;
        }
    }

    /// <summary>
    /// Allocates a set with <see cref="MaskedDescriptorSetLayout"/> -- a texture and a mask.
    /// </summary>
    /// <remarks>
    /// Recycled through a free list of its own. A returned set is only interchangeable with another
    /// of the SAME layout, so the two kinds cannot share one stack: handing a single-sampler set to a
    /// masked draw would leave binding 1 unwritten, which reads as a bound-but-undefined descriptor
    /// rather than as an error.
    /// </remarks>
    public VkDescriptorSet AllocateMaskedDescriptorSet()
    {
        lock (_descriptorPoolLock)
        {
            if (_freeMaskedDescriptorSets.Count > 0) return _freeMaskedDescriptorSets.Pop();

            var set = TryAllocateFromCurrentPool(MaskedDescriptorSetLayout);
            if (set == VkDescriptorSet.Null)
            {
                AddDescriptorPool();
                set = TryAllocateFromCurrentPool(MaskedDescriptorSetLayout);
                if (set == VkDescriptorSet.Null)
                    throw new InvalidOperationException(
                        "masked descriptor set allocation failed against a freshly created pool");
            }
            return set;
        }
    }

    /// <summary>
    /// Points a masked set at its two images: <paramref name="imageView"/> at binding 0 is what gets
    /// drawn, <paramref name="maskView"/> at binding 1 is the coverage its alpha is multiplied by.
    /// </summary>
    /// <remarks>
    /// Both writes go in ONE vkUpdateDescriptorSets call. Two calls would work, but a set whose
    /// second binding is written later is, in between, a set the shader may read with binding 1
    /// undefined -- and a descriptor that is merely undefined produces a plausible picture on one
    /// driver and a device loss on another.
    /// </remarks>
    public void UpdateMaskedDescriptorSet(VkDescriptorSet targetSet,
        VkImageView imageView, VkSampler sampler, VkImageView maskView, VkSampler maskSampler)
    {
        VkDescriptorImageInfo texInfo = new()
        {
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            imageView = imageView,
            sampler = sampler
        };
        VkDescriptorImageInfo maskInfo = new()
        {
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
            imageView = maskView,
            sampler = maskSampler
        };
        var writes = stackalloc VkWriteDescriptorSet[2];
        writes[0] = new()
        {
            dstSet = targetSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &texInfo
        };
        writes[1] = new()
        {
            dstSet = targetSet,
            dstBinding = 1,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &maskInfo
        };
        DeviceApi.vkUpdateDescriptorSets(2, writes, 0, null);
    }

    /// <summary>Hands a masked set back for re-issue. See <see cref="FreeDescriptorSet"/>.</summary>
    public void FreeMaskedDescriptorSet(VkDescriptorSet set)
    {
        lock (_descriptorPoolLock)
        {
            if (set != VkDescriptorSet.Null) _freeMaskedDescriptorSets.Push(set);
        }
    }

    /// <summary>Takes one set from the current pool, or returns null if that pool is spent.
    /// Caller holds <see cref="_descriptorPoolLock"/>.</summary>
    private VkDescriptorSet TryAllocateFromCurrentPool() => TryAllocateFromCurrentPool(DescriptorSetLayout);

    /// <summary>Caller holds <see cref="_descriptorPoolLock"/>.</summary>
    private VkDescriptorSet TryAllocateFromCurrentPool(VkDescriptorSetLayout setLayout)
    {
        if (_setsLeftInCurrentPool == 0) return VkDescriptorSet.Null;

        var layout = setLayout;
        VkDescriptorSetAllocateInfo dsAI = new()
        {
            descriptorPool = _currentPool,
            descriptorSetCount = 1,
            pSetLayouts = &layout
        };
        VkDescriptorSet set;
        var result = DeviceApi.vkAllocateDescriptorSets(&dsAI, &set);
        // A driver may refuse before our own count says empty (fragmentation, or it accounts for
        // pool capacity differently). Treat that as "this pool is done" rather than an error.
        if (result is VkResult.ErrorOutOfPoolMemory or VkResult.ErrorFragmentedPool)
        {
            _setsLeftInCurrentPool = 0;
            return VkDescriptorSet.Null;
        }
        result.CheckResult();
        _setsLeftInCurrentPool--;
        return set;
    }

    /// <summary>Caller holds <see cref="_descriptorPoolLock"/>.</summary>
    private void AddDescriptorPool()
    {
        VkDescriptorPoolSize poolSize = new()
        {
            type = VkDescriptorType.CombinedImageSampler,
            descriptorCount = DescriptorSetsPerPool * MaxDescriptorsPerSet
        };
        VkDescriptorPoolCreateInfo dpCI = new()
        {
            flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
            maxSets = DescriptorSetsPerPool,
            poolSizeCount = 1,
            pPoolSizes = &poolSize
        };
        DeviceApi.vkCreateDescriptorPool(&dpCI, null, out var pool).CheckResult();
        _descriptorPools.Add(pool);
        _currentPool = pool;
        _setsLeftInCurrentPool = DescriptorSetsPerPool;
    }

    /// <summary>
    /// Frees a descriptor set back to the pool.
    /// </summary>
    public void FreeDescriptorSet(VkDescriptorSet set)
    {
        lock (_descriptorPoolLock)
        {
            // Recycled, not returned to the driver: with several pools in play we would otherwise
            // have to remember which one issued this set, and re-issuing keeps the handle valid
            // rather than invalidating one an in-flight command buffer may still name.
            if (set != VkDescriptorSet.Null) _freeDescriptorSets.Push(set);
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
        => TryFindMemoryType(typeFilter, properties, out var index)
            ? index
            : throw new InvalidOperationException("Failed to find suitable memory type");

    /// <summary>
    /// The memory type for a <c>TransientAttachment</c> image: lazily allocated where the device
    /// offers it, plain device-local otherwise.
    /// </summary>
    /// <remarks>
    /// A transient attachment — the multisample colour and the depth, both loaded by clear and never
    /// stored — is consumed entirely inside its render pass, and a tiled GPU keeps it in tile memory.
    /// A memory type with <c>LAZILY_ALLOCATED</c> lets the driver back it only if it ever has to,
    /// which on such a device is never. That is the difference between a large sheet exported at 300
    /// dpi under 4x MSAA costing 16 bytes a pixel PER transient image and costing nothing: adding the
    /// depth attachment doubled that bill, and on a shared-memory Adreno the second gigabyte was the one
    /// <c>vkAllocateMemory</c> refused. Desktop GPUs offer no such type and take device-local, as before.
    /// </remarks>
    public uint FindTransientMemoryType(uint typeFilter)
    {
        if (TryFindMemoryType(typeFilter,
                VkMemoryPropertyFlags.DeviceLocal | VkMemoryPropertyFlags.LazilyAllocated, out var lazy))
        {
            TransientMemoryIsLazy = true;
            return lazy;
        }
        return FindMemoryType(typeFilter, VkMemoryPropertyFlags.DeviceLocal);
    }

    /// <summary>Whether the device has any lazily allocated memory type at all — a tiler does, a
    /// desktop GPU (and lavapipe) does not.</summary>
    public bool OffersLazilyAllocatedMemory
    {
        get
        {
            EnsureMemoryProperties();
            for (var i = 0; i < _memProperties.memoryTypeCount; i++)
                if ((_memProperties.memoryTypes[i].propertyFlags & VkMemoryPropertyFlags.LazilyAllocated) != 0)
                    return true;
            return false;
        }
    }

    /// <summary>True once <see cref="FindTransientMemoryType"/> has placed a transient attachment in
    /// lazily allocated memory — that is, this device is keeping them in tile memory.</summary>
    public bool TransientMemoryIsLazy { get; private set; }

    private bool TryFindMemoryType(uint typeFilter, VkMemoryPropertyFlags properties, out uint index)
    {
        EnsureMemoryProperties();
        for (uint i = 0; i < _memProperties.memoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (_memProperties.memoryTypes[(int)i].propertyFlags & properties) == properties)
            {
                index = i;
                return true;
            }
        }
        index = 0;
        return false;
    }

    private void EnsureMemoryProperties()
    {
        if (_memPropertiesCached) return;
        InstanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out _memProperties);
        _memPropertiesCached = true;
    }

    // ---- Queue ownership: enforced, not locked ---------------------------------------------------
    // Vulkan requires external synchronization of a VkQueue (vkQueueSubmit / vkQueuePresentKHR /
    // vkQueueWaitIdle) and of a VkCommandPool. The obvious implementation is a lock on every submit —
    // but that puts a lock on the hot path to guard a hazard this design does not actually have.
    //
    // Submission here is SINGLE-OWNER per device, checked against every call site: windows share one
    // device but all render on the SDL event-loop thread; every offscreen context (the viewer's
    // rasterizer, all test fixtures) is built by CreateOffscreen with its OWN device and therefore its
    // own private queue; live thumbnail capture rides the window's own frame; and ExecuteOneShot is
    // reachable only from VkTexture.CreateFromBgra — the legacy eager-upload path that CreateDeferred
    // superseded, whose only remaining caller is a single-threaded fork test. One owner needs no
    // mutual exclusion.
    //
    // That property is what makes the lock unnecessary, so it is asserted rather than left to prose:
    // if a future change submits from a second thread, this fails immediately with a name attached
    // instead of surfacing months later as an unexplained fence that never signals. DEBUG-only, so
    // the shipped path carries neither a lock nor a check.
    //
    // Scope: identity-based, so it is applied only where "the owning thread" is a stable invariant —
    // the windowed frame submit and the DEBUG readback. Offscreen paths opt out (see
    // VulkanContext.Offscreen.cs): their queue is private, and successive Task.Run jobs legitimately
    // arrive on different pool threads without ever overlapping.
    private int _queueThreadId;

    /// <summary>Marks this device's queue as private to whatever job is running, which is what a
    /// headless device's is. See <see cref="AssertQueueThread"/>.</summary>
    internal void MarkQueuePrivate() => _privateQueue = true;

    [Conditional("DEBUG")]
    internal void AssertQueueThread(string method)
    {
        // A headless device opts out, as the scope note above says it should. Its queue is reached
        // only from the job currently driving it, and successive jobs legitimately arrive on
        // different pool threads without ever overlapping -- a test collection running serially on
        // the thread pool is exactly that, and it was failing here on whichever thread it drew
        // second. The windowed path keeps the check, where a single owning thread IS the invariant.
        if (_privateQueue) return;

        var id = Environment.CurrentManagedThreadId;
        var owner = Interlocked.CompareExchange(ref _queueThreadId, id, 0);
        if (owner != 0 && owner != id)
        {
            throw new InvalidOperationException(
                $"{method} submitted to this device's queue from thread {id}, but the queue is owned by " +
                $"thread {owner}. VkQueue and VkCommandPool require external synchronization; this device " +
                "relies on single-owner submission instead of a lock. Either submit from the owning " +
                "thread (hand the work to it), or give this path its own device via CreateOffscreen.");
        }
    }

    public void ExecuteOneShot(Action<VkCommandBuffer> action)
    {
        // Touches both the queue and the shared command pool, each of which needs external
        // synchronization; this device provides it by single ownership (see AssertQueueThread).
        AssertQueueThread(nameof(ExecuteOneShot));

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
    // --- Device-object churn counters (wedge forensics) -------------------------------------------
    // A GPU hang leaves no GPU-side dump, and the field wedges keep outrunning hypotheses: the
    // 2026-08-04 one had the SDF atlas (the only instrumented subsystem) completely idle, and the
    // residency logs had to be read backwards to learn that buffer/texture churn was in flight
    // instead. These count every buffer/image/memory create+free that goes through the device's own
    // helpers or VkTexture, cumulatively; VkRenderer snapshots them at each BeginFrame so the wedge
    // breadcrumb can print what the HUNG submission's frame was doing. Interlocked because creation
    // can happen off the render thread (thumbnail capture, probes); these are creation paths, never
    // per-draw, so the cost is noise.
    private long _buffersCreated, _buffersFreed, _imagesCreated, _imagesFreed, _memAllocs, _memFrees;

    /// <summary>Cumulative device-object churn snapshot. Deltas between two snapshots are the churn
    /// in between — see <see cref="VkRenderer.DeviceChurnBreadcrumb"/>.</summary>
    public readonly record struct DeviceChurn(
        long BuffersCreated, long BuffersFreed, long ImagesCreated, long ImagesFreed,
        long MemAllocs, long MemFrees);

    public DeviceChurn ChurnCounters => new(
        Interlocked.Read(ref _buffersCreated), Interlocked.Read(ref _buffersFreed),
        Interlocked.Read(ref _imagesCreated), Interlocked.Read(ref _imagesFreed),
        Interlocked.Read(ref _memAllocs), Interlocked.Read(ref _memFrees));

    internal void NoteBufferCreated() { Interlocked.Increment(ref _buffersCreated); Interlocked.Increment(ref _memAllocs); }
    internal void NoteBufferDestroyed() { Interlocked.Increment(ref _buffersFreed); Interlocked.Increment(ref _memFrees); }
    internal void NoteImageCreated() { Interlocked.Increment(ref _imagesCreated); Interlocked.Increment(ref _memAllocs); }
    internal void NoteImageDestroyed() { Interlocked.Increment(ref _imagesFreed); Interlocked.Increment(ref _memFrees); }

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

        NoteBufferCreated();
        return (buffer, memory);
    }

    public void DestroyBuffer(VkBuffer buffer, VkDeviceMemory memory)
    {
        DeviceApi.vkDestroyBuffer(buffer);
        DeviceApi.vkFreeMemory(memory);
        NoteBufferDestroyed();
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

        DeviceApi.vkDestroySampler(LinearClampSampler);
        DeviceApi.vkDestroyPipelineLayout(PipelineLayout);
        DeviceApi.vkDestroyDescriptorSetLayout(DescriptorSetLayout);
        DeviceApi.vkDestroyPipelineLayout(MaskedPipelineLayout);
        DeviceApi.vkDestroyDescriptorSetLayout(MaskedDescriptorSetLayout);
        foreach (var pool in _descriptorPools)
            DeviceApi.vkDestroyDescriptorPool(pool);
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

        // Prefer a discrete GPU, but accept any device that can present. On a host with both a discrete
        // card and an integrated one, taking the first suitable device means taking whatever the loader
        // happened to enumerate first, which can silently land on integrated graphics sharing system
        // memory instead of dedicated VRAM. Preference only, never a requirement: an iGPU-only machine
        // still gets a device, so this narrows nothing.
        var fallback = VkPhysicalDevice.Null;
        uint fallbackFamily = 0;

        foreach (var pd in devices)
        {
            if (!TryFindGraphicsQueue(instanceApi, pd, surface, out var family))
                continue;

            instanceApi.vkGetPhysicalDeviceProperties(pd, out var props);
            if (props.deviceType == VkPhysicalDeviceType.DiscreteGpu)
            {
                queueFamily = family;
                LogSelectedDevice(instanceApi, pd, family, count);
                return pd;
            }

            if (fallback == VkPhysicalDevice.Null)
            {
                fallback = pd;
                fallbackFamily = family;
            }
        }

        if (fallback != VkPhysicalDevice.Null)
        {
            queueFamily = fallbackFamily;
            LogSelectedDevice(instanceApi, fallback, fallbackFamily, count);
            return fallback;
        }

        throw new InvalidOperationException("No suitable Vulkan physical device found");
    }

    /// <summary>
    /// Records which physical device the picker settled on. Not optional diagnostics: the pickers take
    /// the FIRST device meeting their requirements and enumeration order belongs to the loader, so on a
    /// host with a discrete card plus an integrated one there is otherwise no way to tell which GPU is
    /// driving, and no way to attribute a later device loss or driver quirk to hardware.
    /// </summary>
    private static void LogSelectedDevice(VkInstanceApi instanceApi, VkPhysicalDevice device, uint queueFamily, uint deviceCount)
    {
        instanceApi.vkGetPhysicalDeviceProperties(device, out var props);
        var api = props.apiVersion;
        // apiVersion uses the standard 22/12/10 split. driverVersion does NOT: its encoding is
        // vendor-specific (NVIDIA and Intel each pack it differently), so it is reported raw rather
        // than decoded into a version that would be wrong for most vendors.
        SdlVulkanLog.Logger.PhysicalDeviceSelected(
            VkStringInterop.ConvertToManaged(props.deviceName) ?? "<unknown>",
            props.deviceType.ToString(),
            props.driverVersion.ToString(),
            $"{api >> 22}.{(api >> 12) & 0x3FF}.{api & 0xFFF}",
            queueFamily,
            deviceCount);
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

        // Same discrete-first preference as the surface picker, for the same reason: an offscreen
        // render (export, headless test) should not silently land on integrated graphics because the
        // loader listed it first. There is no surface to satisfy here, only a graphics queue.
        var fallback = VkPhysicalDevice.Null;
        uint fallbackFamily = 0;

        foreach (var pd in devices)
        {
            uint qCount = 0;
            instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(pd, &qCount, null);
            var props = new VkQueueFamilyProperties[qCount];
            fixed (VkQueueFamilyProperties* pProps = props)
                instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(pd, &qCount, pProps);

            for (uint i = 0; i < qCount; i++)
            {
                if ((props[i].queueFlags & VkQueueFlags.Graphics) == 0)
                    continue;

                instanceApi.vkGetPhysicalDeviceProperties(pd, out var devProps);
                if (devProps.deviceType == VkPhysicalDeviceType.DiscreteGpu)
                {
                    queueFamily = i;
                    LogSelectedDevice(instanceApi, pd, i, count);
                    return pd;
                }

                if (fallback == VkPhysicalDevice.Null)
                {
                    fallback = pd;
                    fallbackFamily = i;
                }
                break; // first graphics queue on this device is enough; move to the next device
            }
        }

        if (fallback != VkPhysicalDevice.Null)
        {
            queueFamily = fallbackFamily;
            LogSelectedDevice(instanceApi, fallback, fallbackFamily, count);
            return fallback;
        }

        throw new InvalidOperationException("No suitable Vulkan physical device found (offscreen)");
    }

    // Chooses a swapchain color format the surface supports: B8G8R8A8Unorm when available (desktop —
    // keeps the readback/offscreen byte order), else R8G8B8A8Unorm (Android/Mali offers no BGRA), else
    // the surface's first advertised format. A single legacy Undefined entry means "any", so BGRA is safe.
    private static VkFormat PickSurfaceColorFormat(VkInstanceApi instanceApi, VkPhysicalDevice physicalDevice, VkSurfaceKHR surface)
    {
        uint count = 0;
        instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, &count, null);
        if (count == 0)
            return VkFormat.B8G8R8A8Unorm;

        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[(int)count];
        fixed (VkSurfaceFormatKHR* p = formats)
            instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, &count, p);

        if (count == 1 && formats[0].format == VkFormat.Undefined)
            return VkFormat.B8G8R8A8Unorm;

        var hasBgra = false;
        var hasRgba = false;
        foreach (var f in formats)
        {
            if (f.format == VkFormat.B8G8R8A8Unorm) hasBgra = true;
            else if (f.format == VkFormat.R8G8B8A8Unorm) hasRgba = true;
        }
        if (hasBgra) return VkFormat.B8G8R8A8Unorm;
        if (hasRgba) return VkFormat.R8G8B8A8Unorm;
        return formats[0].format;
    }

    /// <summary>How many subpass dependencies every render pass here declares. See
    /// <see cref="FillSubpassDependencies"/> for why this is uniform.</summary>
    internal const uint SubpassDependencyCount = 2;

    /// <summary>
    /// The subpass dependencies EVERY render pass in this renderer declares, identical in content and
    /// count across all of them (swapchain, offscreen, thumbnail capture).
    /// <para>
    /// Render-pass compatibility is not only about attachments. Two passes whose dependency lists
    /// differ are incompatible, so a pipeline baked against one may not legally be used inside the
    /// other. The swapchain pass declared one dependency and the thumbnail-capture pass two, while
    /// both drew with the same pre-baked pipelines: the validation layer reports that as
    /// VUID-vkCmdDraw-renderPass-02684, "dependencyCount is incompatible ... 2 != 1". Declaring one
    /// shared pair everywhere is what keeps the shared pipelines legal, and the trailing transfer
    /// dependency costs the present path nothing it can measure.
    /// </para>
    /// <para>
    /// The trailing subpass-to-external entry covers BOTH ways a pass's output is consumed after it
    /// ends: a transfer read (ThumbnailCapture's vkCmdCopyImageToBuffer) and a fragment-shader read
    /// (VulkanContext.CachedLayer, whose result is sampled by a later draw in the same command
    /// buffer). Widening the existing entry rather than adding a third is deliberate -- the count and
    /// content have to match across every pass, so a pass needing different synchronisation cannot
    /// have its own list. Widening only ever adds ordering, so it cannot introduce a hazard.
    /// </para>
    /// <para>
    /// The external-to-subpass entry carries <c>srcAccessMask = ColorAttachmentWrite</c> on purpose.
    /// At 0 it orders execution but establishes no memory dependency against the PREVIOUS frame's
    /// storeOp write to the same attachment, so vkCmdBeginRenderPass's layout transition races it and
    /// synchronization validation reports a WRITE_AFTER_WRITE hazard on every alternating frame pair.
    /// </para>
    /// <para>
    /// Its <c>dstAccessMask</c> admits COLOR_ATTACHMENT_READ as well as WRITE, for the damage pass
    /// (<c>VulkanContext.Damage</c>): a <c>loadOp LOAD</c> READS the attachment, and with WRITE alone
    /// that read is not ordered after the pass's own PresentSrc -> ColorAttachmentOptimal transition.
    /// Synchronization validation reports it once per swapchain image on every partial frame:
    /// "vkCmdBeginRenderPass(): READ_AFTER_WRITE hazard ... loadOp access is not synchronized with the
    /// attachment layout transition ... must allow VK_ACCESS_2_COLOR_ATTACHMENT_READ_BIT" (GTX 1070,
    /// SDK 1.4.357). It lives HERE and not on the damage pass alone because dependencies are not among
    /// the things render-pass compatibility exempts: widening only that pass made it incompatible with
    /// every framebuffer and pipeline built against the clearing pass (VUID 00904 / 02684 on each
    /// partial frame). A clearing pass admitting a read it never performs costs nothing.
    /// </para>
    /// <para>
    /// The depth-stencil stages and accesses are here for the same reason and by the same argument.
    /// Every pass carries a depth attachment (see <see cref="CreateCompatibleRenderPass"/>), and its
    /// depth loadOp CLEAR write needs ordering against the previous frame's storeOp write to the same
    /// image — the identical WRITE_AFTER_WRITE the colour entry above documents, one attachment
    /// over. Widening only ever adds ordering, so it cannot introduce a hazard.
    /// </para>
    /// </summary>
    internal static void FillSubpassDependencies(Span<VkSubpassDependency> deps)
    {
        deps[0] = new()
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput
                | VkPipelineStageFlags.LateFragmentTests,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite
                | VkAccessFlags.DepthStencilAttachmentWrite,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput
                | VkPipelineStageFlags.EarlyFragmentTests,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.ColorAttachmentRead
                | VkAccessFlags.DepthStencilAttachmentWrite | VkAccessFlags.DepthStencilAttachmentRead
        };
        deps[1] = new()
        {
            srcSubpass = 0, dstSubpass = VK_SUBPASS_EXTERNAL,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput
                | VkPipelineStageFlags.LateFragmentTests,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite
                | VkAccessFlags.DepthStencilAttachmentWrite,
            dstStageMask = VkPipelineStageFlags.Transfer | VkPipelineStageFlags.FragmentShader,
            dstAccessMask = VkAccessFlags.TransferRead | VkAccessFlags.ShaderRead
        };
    }

    /// <summary>
    /// The first depth format the device supports as an optimal-tiling depth-stencil attachment.
    /// </summary>
    /// <remarks>
    /// <para>D32 first because a 32-bit float depth buffer is the one that does not visibly z-fight on
    /// content with a wide depth range; the packed 24-bit formats after it because some devices offer
    /// only those. Vulkan requires depth-stencil attachment support for at least one of D32_SFLOAT and
    /// X8_D24_UNORM_PACK32, so the list cannot come up empty on a conforming device — which is why a
    /// miss throws rather than degrading to a renderer without depth: it is a broken driver, not a
    /// configuration to support.</para>
    /// <para>The sample count needs no check of its own: <c>framebufferDepthSampleCounts</c> is
    /// required to include 1 and 4, the same guarantee <c>framebufferColorSampleCounts</c> gives the
    /// colour attachment this renderer already relies on.</para>
    /// </remarks>
    private static VkFormat ChooseDepthFormat(VkInstanceApi instanceApi, VkPhysicalDevice physicalDevice)
    {
        ReadOnlySpan<VkFormat> candidates =
        [
            VkFormat.D32Sfloat,
            VkFormat.X8D24UnormPack32,
            VkFormat.D32SfloatS8Uint,
            VkFormat.D24UnormS8Uint,
        ];

        foreach (var format in candidates)
        {
            instanceApi.vkGetPhysicalDeviceFormatProperties(physicalDevice, format, out var props);
            if ((props.optimalTilingFeatures & VkFormatFeatureFlags.DepthStencilAttachment) != 0)
                return format;
        }
        throw new InvalidOperationException(
            "The device supports no depth-stencil attachment format, which Vulkan requires of every conforming implementation.");
    }

    /// <summary>
    /// Creates a render pass in the ONE shape every pass in this renderer has, so that the pre-baked
    /// pipelines (<see cref="VkPipelineSet"/>, created against the device's <see cref="RenderPass"/>)
    /// bind into all of them: the swapchain pass, the damage-preserving load pass, the offscreen
    /// pass, the cached layer and the thumbnail capture.
    /// </summary>
    /// <remarks>
    /// <para>Render-pass compatibility is per attachment reference — format and sample count — plus
    /// the dependency list, and is indifferent to load/store ops and layouts. So the callers state
    /// only what may legitimately differ: how the colour attachment is loaded and where it starts and
    /// ends. Everything else is fixed here, and building it in one place is what makes the
    /// compatibility structural rather than a discipline five copies had to keep by hand.</para>
    /// <para>Attachment order is <b>colour (0), depth (1), resolve (2, MSAA only)</b>. Depth sits at
    /// index 1 in both modes so the clear values are the same two entries everywhere (see
    /// <see cref="VulkanContext.FillClearValues"/>), and so a framebuffer's attachment list is built
    /// by one helper (<see cref="VulkanContext.CreateCompatibleFramebuffer"/>).</para>
    /// <para><b>The depth attachment is on every pass, and 2D drawing never reads it.</b> It exists
    /// for depth-tested meshes (<see cref="VkRenderer.DrawMesh"/>), which are drawn inline in the same
    /// pass as everything else — antialiased by the same MSAA, clipped by the same scissor, with no
    /// intermediate target. Since compatibility is per attachment, a pass without it could not host
    /// the shared pipelines once they carry a depth-stencil state, so every pass has one and the 2D
    /// pipelines simply switch depth testing off. It is cleared at pass start, then again over each
    /// region a consumer draws meshes into (<see cref="VkRenderer.BeginMeshRegion"/>), never stored,
    /// and never sampled; with <c>TransientAttachment</c> usage a tiler need not allocate it at
    /// all.</para>
    /// <para>The MSAA colour attachment is transient too (cleared, resolved, not stored), which is why
    /// <paramref name="colorLoadOp"/> can only apply to it when there is no resolve: a multisample
    /// attachment cannot be re-loaded from its resolved image, and
    /// <c>VulkanContext.Damage</c> declines to build a load pass under MSAA for exactly that
    /// reason.</para>
    /// </remarks>
    /// <param name="colorLoadOp">How the stored colour is loaded: Clear for a fresh frame, Load for a
    /// damage repaint that keeps the previous contents.</param>
    /// <param name="colorInitialLayout">The layout the stored colour image is in when the pass begins:
    /// Undefined for a clearing pass, PresentSrcKHR for the load pass over a presented image.</param>
    /// <param name="colorFinalLayout">Where the stored colour image is left: PresentSrcKHR for the
    /// swapchain, ShaderReadOnlyOptimal for a sampled layer, TransferSrcOptimal for a copy source,
    /// ColorAttachmentOptimal where the consumer transitions it itself.</param>
    internal static VkRenderPass CreateCompatibleRenderPass(VkDeviceApi deviceApi, VkFormat colorFormat,
        VkFormat depthFormat, VkSampleCountFlags msaaSamples,
        VkAttachmentLoadOp colorLoadOp, VkImageLayout colorInitialLayout, VkImageLayout colorFinalLayout)
    {
        var msaa = msaaSamples != VkSampleCountFlags.Count1;
        var attachmentCount = msaa ? 3 : 2;
        Span<VkAttachmentDescription> attachments = stackalloc VkAttachmentDescription[attachmentCount];

        if (msaa)
        {
            // Multisample colour: cleared, resolved into attachment 2, never stored.
            attachments[0] = new()
            {
                format = colorFormat,
                samples = msaaSamples,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.DontCare,
                stencilLoadOp = VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VkAttachmentStoreOp.DontCare,
                initialLayout = VkImageLayout.Undefined,
                finalLayout = VkImageLayout.ColorAttachmentOptimal
            };
            // The resolve target IS the stored image: its load op is irrelevant (the resolve overwrites
            // it) and its final layout is the caller's.
            attachments[2] = new()
            {
                format = colorFormat,
                samples = VkSampleCountFlags.Count1,
                loadOp = VkAttachmentLoadOp.DontCare,
                storeOp = VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VkAttachmentStoreOp.DontCare,
                initialLayout = VkImageLayout.Undefined,
                finalLayout = colorFinalLayout
            };
        }
        else
        {
            attachments[0] = new()
            {
                format = colorFormat,
                samples = VkSampleCountFlags.Count1,
                loadOp = colorLoadOp,
                storeOp = VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare,
                stencilStoreOp = VkAttachmentStoreOp.DontCare,
                initialLayout = colorInitialLayout,
                finalLayout = colorFinalLayout
            };
        }

        // Depth: cleared to the far plane at pass start, consumed entirely inside the pass, stored
        // nowhere. storeOp DontCare is what makes the image's TransientAttachment usage meaningful — the
        // flag alone does not make it transient, the pass has to agree that nothing outlives it. The
        // stencil aspect is unused whatever the format carries.
        attachments[1] = new()
        {
            format = depthFormat,
            samples = msaaSamples,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.DepthStencilAttachmentOptimal
        };

        VkAttachmentReference colorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        VkAttachmentReference depthRef = new() { attachment = 1, layout = VkImageLayout.DepthStencilAttachmentOptimal };
        VkAttachmentReference resolveRef = new() { attachment = 2, layout = VkImageLayout.ColorAttachmentOptimal };

        VkSubpassDescription subpass = new()
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef,
            pResolveAttachments = msaa ? &resolveRef : null,
            pDepthStencilAttachment = &depthRef
        };

        // The shared pair every pass declares — see FillSubpassDependencies for why it is uniform.
        Span<VkSubpassDependency> deps = stackalloc VkSubpassDependency[(int)SubpassDependencyCount];
        FillSubpassDependencies(deps);

        fixed (VkAttachmentDescription* pAttachments = attachments)
        fixed (VkSubpassDependency* pDeps = deps)
        {
            VkRenderPassCreateInfo rpCI = new()
            {
                attachmentCount = (uint)attachmentCount, pAttachments = pAttachments,
                subpassCount = 1, pSubpasses = &subpass,
                dependencyCount = SubpassDependencyCount, pDependencies = pDeps
            };
            deviceApi.vkCreateRenderPass(&rpCI, null, out var renderPass).CheckResult();
            return renderPass;
        }
    }
}
