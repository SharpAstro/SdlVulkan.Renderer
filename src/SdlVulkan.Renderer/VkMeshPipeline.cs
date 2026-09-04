using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// A depth-tested lit-mesh pipeline for <see cref="VulkanContext.SceneRenderPass"/>.
/// </summary>
/// <remarks>
/// <para>Not part of <see cref="VkPipelineSet"/>, and it cannot be: that set is baked up front
/// against the swapchain pass, whereas the scene pass does not exist until
/// <see cref="VulkanContext.EnsureSceneTargets"/> has run and chosen a depth format the device
/// actually supports. So this is created on demand, against the pass it will draw into, and owns
/// its own layout.</para>
/// <para>It carries its own pipeline layout rather than borrowing the shared one for two reasons
/// that both matter: the shared range is 84 bytes and a 3D transform plus material needs 96, and
/// the shared layout declares a combined-image-sampler set this shader has no use for.</para>
/// </remarks>
public sealed unsafe class VkMeshPipeline : IDisposable
{
    /// <summary>Push-constant floats: <c>mat4 mvp</c> (16), <c>vec4 color</c> (4), <c>vec4 lightDir</c> (4).</summary>
    public const int PushConstantFloats = 24;

    /// <summary>Bytes per vertex: <c>vec3 position</c> + <c>vec3 normal</c>.</summary>
    public const uint VertexStride = 6 * sizeof(float);

    private readonly VkDeviceApi _deviceApi;

    public VkPipeline Pipeline { get; }
    public VkPipelineLayout Layout { get; }

    private VkMeshPipeline(VkDeviceApi deviceApi, VkPipeline pipeline, VkPipelineLayout layout)
    {
        _deviceApi = deviceApi;
        Pipeline = pipeline;
        Layout = layout;
    }

    /// <summary>
    /// Create the pipeline for <paramref name="renderPass"/>, which must have a depth attachment —
    /// in practice <see cref="VulkanContext.SceneRenderPass"/>, after
    /// <see cref="VulkanContext.EnsureSceneTargets"/> has returned true.
    /// </summary>
    public static VkMeshPipeline Create(VulkanContext ctx, VkRenderPass renderPass)
    {
        var deviceApi = ctx.DeviceApi;

        VkPushConstantRange pushRange = new()
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
            offset = 0,
            size = PushConstantFloats * sizeof(float)
        };
        VkPipelineLayoutCreateInfo layoutCI = new()
        {
            setLayoutCount = 0,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange
        };
        deviceApi.vkCreatePipelineLayout(&layoutCI, null, out var layout).CheckResult();

        var vertModule = VkPipelineSet.LoadEmbeddedModule(deviceApi, "mesh.vert");
        var fragModule = VkPipelineSet.LoadEmbeddedModule(deviceApi, "mesh.frag");
        try
        {
            VkUtf8ReadOnlyString entryPoint = "main"u8;
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new() { stage = VkShaderStageFlags.Vertex, module = vertModule, pName = entryPoint };
            stages[1] = new() { stage = VkShaderStageFlags.Fragment, module = fragModule, pName = entryPoint };

            VkVertexInputBindingDescription binding = new(VertexStride);
            var attributes = stackalloc VkVertexInputAttributeDescription[2];
            attributes[0] = new(0, VkFormat.R32G32B32Sfloat, 0);                 // position
            attributes[1] = new(1, VkFormat.R32G32B32Sfloat, 3 * sizeof(float)); // normal

            VkPipelineVertexInputStateCreateInfo vertexInput = new()
            {
                vertexBindingDescriptionCount = 1,
                pVertexBindingDescriptions = &binding,
                vertexAttributeDescriptionCount = 2,
                pVertexAttributeDescriptions = attributes
            };

            VkPipelineInputAssemblyStateCreateInfo inputAssembly = new(VkPrimitiveTopology.TriangleList);
            VkPipelineViewportStateCreateInfo viewportState = new(1, 1);

            // Culling OFF. A tessellated CAD assembly routinely disagrees about winding between parts,
            // and a dropped face is a hole in the model; the fragment shader lights two-sided to match.
            VkPipelineRasterizationStateCreateInfo rasterizer = new()
            {
                polygonMode = VkPolygonMode.Fill,
                lineWidth = 1.0f,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.CounterClockwise
            };

            // Single-sample to match the scene target, which supersamples instead of using MSAA.
            VkPipelineMultisampleStateCreateInfo multisample = new()
            {
                rasterizationSamples = VkSampleCountFlags.Count1
            };

            // The whole point of the pass. Less against a 1.0 clear: a fragment survives when it is
            // nearer than what is already there, and on a freshly cleared buffer everything is.
            VkPipelineDepthStencilStateCreateInfo depthStencil = new()
            {
                depthTestEnable = true,
                depthWriteEnable = true,
                depthCompareOp = VkCompareOp.Less,
                depthBoundsTestEnable = false,
                stencilTestEnable = false
            };

            // Blending OFF, unlike every other pipeline here. Depth decides visibility, and alpha
            // blending depth-tested geometry is order-dependent again — the exact property this pass
            // exists to escape. Translucent meshes need a sorted second pass, not a blend state here.
            var blendAttachments = stackalloc VkPipelineColorBlendAttachmentState[1];
            blendAttachments[0] = new VkPipelineColorBlendAttachmentState
            {
                colorWriteMask = VkColorComponentFlags.All,
                blendEnable = false
            };
            VkPipelineColorBlendStateCreateInfo colorBlend = new()
            {
                attachmentCount = 1,
                pAttachments = blendAttachments
            };

            var dynamicStates = stackalloc VkDynamicState[2];
            dynamicStates[0] = VkDynamicState.Viewport;
            dynamicStates[1] = VkDynamicState.Scissor;
            VkPipelineDynamicStateCreateInfo dynamicState = new()
            {
                dynamicStateCount = 2,
                pDynamicStates = dynamicStates
            };

            VkGraphicsPipelineCreateInfo pipelineCI = new()
            {
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &inputAssembly,
                pViewportState = &viewportState,
                pRasterizationState = &rasterizer,
                pMultisampleState = &multisample,
                pDepthStencilState = &depthStencil,
                pColorBlendState = &colorBlend,
                pDynamicState = &dynamicState,
                layout = layout,
                renderPass = renderPass,
                subpass = 0
            };

            deviceApi.vkCreateGraphicsPipeline(pipelineCI, out var pipeline).CheckResult();
            return new VkMeshPipeline(deviceApi, pipeline, layout);
        }
        finally
        {
            deviceApi.vkDestroyShaderModule(vertModule);
            deviceApi.vkDestroyShaderModule(fragModule);
        }
    }

    /// <summary>
    /// Bind and draw one mesh inside an open scene pass. <paramref name="pushConstants"/> is
    /// <see cref="PushConstantFloats"/> floats: a column-major mvp, then rgba, then the model-space
    /// direction TO the light.
    /// </summary>
    public void Draw(VkCommandBuffer cmd, ReadOnlySpan<float> pushConstants,
        VkBuffer vertexBuffer, ulong vertexOffset, uint vertexCount)
    {
        if (pushConstants.Length < PushConstantFloats || vertexCount == 0) return;

        _deviceApi.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, Pipeline);
        fixed (float* pPC = pushConstants)
            _deviceApi.vkCmdPushConstants(cmd, Layout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0,
                PushConstantFloats * sizeof(float), pPC);

        var buffer = vertexBuffer;
        var offset = vertexOffset;
        _deviceApi.vkCmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        _deviceApi.vkCmdDraw(cmd, vertexCount, 1, 0, 0);
    }

    public void Dispose()
    {
        _deviceApi.vkDestroyPipeline(Pipeline);
        _deviceApi.vkDestroyPipelineLayout(Layout);
    }
}
