using Vortice.Vulkan;
using Vortice.ShaderCompiler;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

internal sealed unsafe class VkPipelineSet : IDisposable
{
    public VkPipeline FlatPipeline { get; }
    public VkPipeline TexturedPipeline { get; }
    public VkPipeline EllipsePipeline { get; }

    private readonly VkDeviceApi _deviceApi;

    #region GLSL 450 Shaders

    private const string FlatVertexSource = """
        #version 450
        layout(location = 0) in vec2 aPos;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        void main() {
            gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
        }
        """;

    private const string FlatFragmentSource = """
        #version 450
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        layout(location = 0) out vec4 FragColor;
        void main() {
            FragColor = pc.color;
        }
        """;

    private const string TextureVertexSource = """
        #version 450
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTexCoord;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        layout(location = 0) out vec2 vTexCoord;
        void main() {
            gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string TextureFragmentSource = """
        #version 450
        layout(location = 0) in vec2 vTexCoord;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        layout(set = 0, binding = 0) uniform sampler2D uTexture;
        layout(location = 0) out vec4 FragColor;
        void main() {
            vec4 texel = texture(uTexture, vTexCoord);
            FragColor = vec4(pc.color.rgb, pc.color.a * texel.a);
        }
        """;

    private const string EllipseVertexSource = """
        #version 450
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aLocalPos;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        layout(location = 0) out vec2 vLocal;
        void main() {
            gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
            vLocal = aLocalPos;
        }
        """;

    private const string EllipseFragmentSource = """
        #version 450
        layout(location = 0) in vec2 vLocal;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        layout(location = 0) out vec4 FragColor;
        void main() {
            float dist = dot(vLocal, vLocal);
            if (dist > 1.0) discard;
            FragColor = pc.color;
        }
        """;

    #endregion

    private VkPipelineSet(VkDeviceApi deviceApi, VkPipeline flat, VkPipeline textured, VkPipeline ellipse)
    {
        _deviceApi = deviceApi;
        FlatPipeline = flat;
        TexturedPipeline = textured;
        EllipsePipeline = ellipse;
    }

    public static VkPipelineSet Create(VulkanContext ctx)
    {
        var deviceApi = ctx.DeviceApi;

        // Compile shaders to SPIR-V
        using var compiler = new Compiler();

        var flatVert = CompileAndCreateModule(deviceApi, compiler, FlatVertexSource, "flat.vert", ShaderKind.VertexShader);
        var flatFrag = CompileAndCreateModule(deviceApi, compiler, FlatFragmentSource, "flat.frag", ShaderKind.FragmentShader);
        var texVert = CompileAndCreateModule(deviceApi, compiler, TextureVertexSource, "tex.vert", ShaderKind.VertexShader);
        var texFrag = CompileAndCreateModule(deviceApi, compiler, TextureFragmentSource, "tex.frag", ShaderKind.FragmentShader);
        var ellipseVert = CompileAndCreateModule(deviceApi, compiler, EllipseVertexSource, "ellipse.vert", ShaderKind.VertexShader);
        var ellipseFrag = CompileAndCreateModule(deviceApi, compiler, EllipseFragmentSource, "ellipse.frag", ShaderKind.FragmentShader);

        try
        {
            // Flat pipeline: vec2 pos only
            VkVertexInputBindingDescription flatBinding = new(2 * sizeof(float));
            VkVertexInputAttributeDescription flatAttr = new(0, VkFormat.R32G32Sfloat, 0);
            var flat = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1);

            // Textured pipeline: vec2 pos + vec2 uv
            VkVertexInputBindingDescription texBinding = new(4 * sizeof(float));
            var texAttrs = stackalloc VkVertexInputAttributeDescription[2];
            texAttrs[0] = new(0, VkFormat.R32G32Sfloat, 0);
            texAttrs[1] = new(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));
            var textured = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, texVert, texFrag,
                &texBinding, 1, texAttrs, 2);

            // Ellipse pipeline: vec2 pos + vec2 local
            VkVertexInputBindingDescription ellipseBinding = new(4 * sizeof(float));
            var ellipseAttrs = stackalloc VkVertexInputAttributeDescription[2];
            ellipseAttrs[0] = new(0, VkFormat.R32G32Sfloat, 0);
            ellipseAttrs[1] = new(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));
            var ellipse = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, ellipseVert, ellipseFrag,
                &ellipseBinding, 1, ellipseAttrs, 2);

            return new VkPipelineSet(deviceApi, flat, textured, ellipse);
        }
        finally
        {
            deviceApi.vkDestroyShaderModule(flatVert);
            deviceApi.vkDestroyShaderModule(flatFrag);
            deviceApi.vkDestroyShaderModule(texVert);
            deviceApi.vkDestroyShaderModule(texFrag);
            deviceApi.vkDestroyShaderModule(ellipseVert);
            deviceApi.vkDestroyShaderModule(ellipseFrag);
        }
    }

    public void Dispose()
    {
        _deviceApi.vkDestroyPipeline(FlatPipeline);
        _deviceApi.vkDestroyPipeline(TexturedPipeline);
        _deviceApi.vkDestroyPipeline(EllipsePipeline);
    }

    private static VkPipeline CreatePipeline(
        VkDeviceApi deviceApi, VkRenderPass renderPass, VkPipelineLayout layout,
        VkShaderModule vertModule, VkShaderModule fragModule,
        VkVertexInputBindingDescription* bindings, uint bindingCount,
        VkVertexInputAttributeDescription* attributes, uint attributeCount)
    {
        VkUtf8ReadOnlyString entryPoint = "main"u8;

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new()
        {
            stage = VkShaderStageFlags.Vertex,
            module = vertModule,
            pName = entryPoint
        };
        stages[1] = new()
        {
            stage = VkShaderStageFlags.Fragment,
            module = fragModule,
            pName = entryPoint
        };

        VkPipelineVertexInputStateCreateInfo vertexInput = new()
        {
            vertexBindingDescriptionCount = bindingCount,
            pVertexBindingDescriptions = bindings,
            vertexAttributeDescriptionCount = attributeCount,
            pVertexAttributeDescriptions = attributes
        };

        VkPipelineInputAssemblyStateCreateInfo inputAssembly = new(VkPrimitiveTopology.TriangleList);
        VkPipelineViewportStateCreateInfo viewportState = new(1, 1);

        VkPipelineRasterizationStateCreateInfo rasterizer = new()
        {
            polygonMode = VkPolygonMode.Fill,
            lineWidth = 1.0f,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.Clockwise
        };

        VkPipelineMultisampleStateCreateInfo multisample = VkPipelineMultisampleStateCreateInfo.Default;

        VkPipelineColorBlendAttachmentState blendAttachment = new()
        {
            colorWriteMask = VkColorComponentFlags.All,
            blendEnable = true,
            srcColorBlendFactor = VkBlendFactor.SrcAlpha,
            dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            colorBlendOp = VkBlendOp.Add,
            srcAlphaBlendFactor = VkBlendFactor.One,
            dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            alphaBlendOp = VkBlendOp.Add
        };

        VkPipelineColorBlendStateCreateInfo colorBlend = new(blendAttachment);

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
            pColorBlendState = &colorBlend,
            pDynamicState = &dynamicState,
            layout = layout,
            renderPass = renderPass,
            subpass = 0
        };

        deviceApi.vkCreateGraphicsPipeline(pipelineCI, out var pipeline).CheckResult();
        return pipeline;
    }

    private static VkShaderModule CompileAndCreateModule(VkDeviceApi deviceApi, Compiler compiler, string source, string fileName, ShaderKind kind)
    {
        var options = new CompilerOptions
        {
            TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
            ShaderStage = kind
        };

        var result = compiler.Compile(source, fileName, options);
        if (result.Status != CompilationStatus.Success)
            throw new InvalidOperationException($"Shader compilation failed ({fileName}): {result.ErrorMessage}");

        var spirv = result.Bytecode;
        fixed (byte* pSpirv = spirv)
        {
            VkShaderModuleCreateInfo createInfo = new()
            {
                codeSize = (nuint)spirv.Length,
                pCode = (uint*)pSpirv
            };
            deviceApi.vkCreateShaderModule(&createInfo, null, out var module).CheckResult();
            return module;
        }
    }
}
