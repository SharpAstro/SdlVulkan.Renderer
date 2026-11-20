using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// The compute pipeline behind <see cref="VkTexture.CreateDeferredFromYCbCr"/> (<c>Shaders/ycbcrhdr.comp</c>): its set
/// layout (five storage buffers: luma, Cb, Cr, the code table, the peak; and the output storage image), pipeline layout,
/// pipeline and a descriptor pool of its own. One per device, made on first use (<see cref="VulkanDevice.YCbCrConverter"/>),
/// so a device that never converts a picture never builds any of it.
/// </summary>
/// <remarks>
/// It runs on the graphics queue, in the frame's command buffer before the render pass, as the texture uploads do: a
/// queue that does graphics on a device with compute almost always does compute too, and this refuses (rather than
/// assumes) when it does not. A conversion's set comes back to the pool once its frame has retired.
/// </remarks>
internal sealed unsafe class VkYCbCrConverter : IDisposable
{
    /// <summary>Bytes of push constants: ten 32-bit words, as <c>ycbcrhdr.comp</c> declares them.</summary>
    internal const uint PushBytes = 40;

    /// <summary>The shader's workgroup edge (local_size_x and _y).</summary>
    internal const uint GroupSize = 16;

    /// <summary>Conversions whose sets may be alive at once; one is held from creation until its frame retires.</summary>
    private const uint MaxSets = 64;

    private const uint StorageBuffers = 5;

    private readonly VulkanDevice _device;
    private readonly VkDescriptorPool _pool;
    private readonly Lock _poolLock = new();
    private bool _disposed;

    public VkDescriptorSetLayout SetLayout { get; }
    public VkPipelineLayout PipelineLayout { get; }
    public VkPipeline Pipeline { get; }

    /// <summary>The device's <c>minStorageBufferOffsetAlignment</c>: where each plane may start in the shared buffer.</summary>
    public ulong StorageAlignment { get; }

    public VkYCbCrConverter(VulkanDevice device)
    {
        _device = device;
        var api = device.DeviceApi;

        uint familyCount = 0;
        device.InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device.PhysicalDevice, &familyCount, null);
        var families = new VkQueueFamilyProperties[familyCount];
        fixed (VkQueueFamilyProperties* pFamilies = families)
            device.InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device.PhysicalDevice, &familyCount, pFamilies);
        if ((families[device.GraphicsQueueFamily].queueFlags & VkQueueFlags.Compute) == 0)
            throw new NotSupportedException("The graphics queue does not run compute work, which the Y'CbCr conversion needs.");

        device.InstanceApi.vkGetPhysicalDeviceProperties(device.PhysicalDevice, out var props);
        StorageAlignment = Math.Max(4, props.limits.minStorageBufferOffsetAlignment);

        var bindings = stackalloc VkDescriptorSetLayoutBinding[(int)StorageBuffers + 1];
        for (uint i = 0; i < StorageBuffers; i++)
        {
            bindings[i] = new()
            {
                binding = i,
                descriptorType = VkDescriptorType.StorageBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute,
            };
        }
        bindings[StorageBuffers] = new()
        {
            binding = StorageBuffers,
            descriptorType = VkDescriptorType.StorageImage,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Compute,
        };
        VkDescriptorSetLayoutCreateInfo setLayoutCI = new() { bindingCount = StorageBuffers + 1, pBindings = bindings };
        api.vkCreateDescriptorSetLayout(&setLayoutCI, null, out var setLayout).CheckResult();
        SetLayout = setLayout;

        VkPushConstantRange range = new() { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = PushBytes };
        VkPipelineLayoutCreateInfo layoutCI = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &range,
        };
        api.vkCreatePipelineLayout(&layoutCI, null, out var pipelineLayout).CheckResult();
        PipelineLayout = pipelineLayout;

        var module = VkPipelineSet.LoadEmbeddedModule(api, "ycbcrhdr.comp");
        try
        {
            VkUtf8ReadOnlyString entryPoint = "main"u8;
            VkComputePipelineCreateInfo pipelineCI = new()
            {
                stage = new VkPipelineShaderStageCreateInfo
                {
                    stage = VkShaderStageFlags.Compute,
                    module = module,
                    pName = entryPoint,
                },
                layout = pipelineLayout,
            };
            VkPipeline pipeline;
            api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &pipelineCI, null, &pipeline).CheckResult();
            Pipeline = pipeline;
        }
        finally
        {
            api.vkDestroyShaderModule(module);
        }

        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new() { type = VkDescriptorType.StorageBuffer, descriptorCount = MaxSets * StorageBuffers };
        poolSizes[1] = new() { type = VkDescriptorType.StorageImage, descriptorCount = MaxSets };
        VkDescriptorPoolCreateInfo poolCI = new()
        {
            flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
            maxSets = MaxSets,
            poolSizeCount = 2,
            pPoolSizes = poolSizes,
        };
        api.vkCreateDescriptorPool(&poolCI, null, out _pool).CheckResult();
    }

    /// <summary>A set for one conversion; give it back with <see cref="Free"/> once its frame has retired.</summary>
    public VkDescriptorSet Allocate()
    {
        lock (_poolLock)
        {
            var layout = SetLayout;
            VkDescriptorSetAllocateInfo allocateInfo = new()
            {
                descriptorPool = _pool,
                descriptorSetCount = 1,
                pSetLayouts = &layout,
            };
            VkDescriptorSet set;
            var result = _device.DeviceApi.vkAllocateDescriptorSets(&allocateInfo, &set);
            if (result is VkResult.ErrorOutOfPoolMemory or VkResult.ErrorFragmentedPool)
                throw new InvalidOperationException($"More than {MaxSets} Y'CbCr conversions are waiting for their frames at once.");
            result.CheckResult();
            return set;
        }
    }

    public void Free(VkDescriptorSet set)
    {
        lock (_poolLock)
        {
            if (_disposed) return; // the pool went with the device, and every set with it
            _device.DeviceApi.vkFreeDescriptorSets(_pool, 1, &set);
        }
    }

    /// <summary>Points <paramref name="set"/> at the conversion's buffer sections and its output image.</summary>
    public void Write(VkDescriptorSet set, VkBuffer buffer, ReadOnlySpan<(ulong Offset, ulong Range)> sections, VkImageView output)
    {
        var infos = stackalloc VkDescriptorBufferInfo[(int)StorageBuffers];
        var writes = stackalloc VkWriteDescriptorSet[(int)StorageBuffers + 1];
        for (var i = 0; i < StorageBuffers; i++)
        {
            infos[i] = new() { buffer = buffer, offset = sections[i].Offset, range = sections[i].Range };
            writes[i] = new()
            {
                dstSet = set,
                dstBinding = (uint)i,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                pBufferInfo = &infos[i],
            };
        }
        VkDescriptorImageInfo imageInfo = new() { imageView = output, imageLayout = VkImageLayout.General };
        writes[StorageBuffers] = new()
        {
            dstSet = set,
            dstBinding = StorageBuffers,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageImage,
            pImageInfo = &imageInfo,
        };
        _device.DeviceApi.vkUpdateDescriptorSets(StorageBuffers + 1, writes, 0, null);
    }

    public void Dispose()
    {
        lock (_poolLock)
        {
            if (_disposed) return;
            _disposed = true;
            var api = _device.DeviceApi;
            api.vkDestroyPipeline(Pipeline);
            api.vkDestroyPipelineLayout(PipelineLayout);
            api.vkDestroyDescriptorPool(_pool);
            api.vkDestroyDescriptorSetLayout(SetLayout);
        }
    }
}
