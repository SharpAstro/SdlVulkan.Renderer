using Vortice.Vulkan;
using Vortice.ShaderCompiler;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

public sealed unsafe class VkPipelineSet : IDisposable
{
    public VkPipeline FlatPipeline { get; }
    public VkPipeline TexturedPipeline { get; }
    public VkPipeline EllipsePipeline { get; }
    public VkPipeline PagePipeline { get; }
    public VkPipeline StrokePipeline { get; }

    // Blend mode variants of FlatPipeline
    public VkPipeline FlatMultiplyPipeline { get; }
    public VkPipeline FlatScreenPipeline { get; }
    public VkPipeline FlatDarkenPipeline { get; }
    public VkPipeline FlatLightenPipeline { get; }

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
            // Color glyphs (emoji, COLR) have their own RGB; monochrome glyphs are white (1,1,1) with varying alpha.
            // Detect color glyphs by checking if RGB deviates from white.
            float isColor = 1.0 - step(0.99, min(texel.r, min(texel.g, texel.b)));
            vec3 rgb = mix(pc.color.rgb, texel.rgb, isColor);
            FragColor = vec4(rgb, pc.color.a * texel.a);
        }
        """;

    // Pass-through fragment shader for textures — no glyph color detection
    private const string PageFragmentSource = """
        #version 450
        layout(location = 0) in vec2 vTexCoord;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
        layout(set = 0, binding = 0) uniform sampler2D uTexture;
        layout(location = 0) out vec4 FragColor;
        void main() {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    // Stroke pipeline: expands line segments to screen-space quads in the vertex shader.
    // Each vertex carries both endpoints (aP0, aP1) and side/end selectors.
    // halfWidth is in the same coordinate space as the projection matrix.
    private const string StrokeVertexSource = """
        #version 450
        layout(location = 0) in vec2 aP0;
        layout(location = 1) in vec2 aP1;
        layout(location = 2) in vec2 aParams;
        layout(push_constant) uniform PC { mat4 proj; vec4 color; float halfWidth; } pc;
        void main() {
            vec2 pos = mix(aP0, aP1, aParams.y);
            vec2 dir = aP1 - aP0;
            float len = length(dir);
            vec2 normal = len > 0.0001 ? vec2(-dir.y, dir.x) / len : vec2(0.0, 1.0);
            pos += normal * aParams.x * pc.halfWidth;
            gl_Position = pc.proj * vec4(pos, 0.0, 1.0);
        }
        """;

    private const string StrokeFragmentSource = """
        #version 450
        layout(push_constant) uniform PC { mat4 proj; vec4 color; float halfWidth; } pc;
        layout(location = 0) out vec4 FragColor;
        void main() {
            FragColor = pc.color;
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
        layout(push_constant) uniform PC { mat4 proj; vec4 color; float innerRadius; } pc;
        layout(location = 0) out vec4 FragColor;
        void main() {
            float dist = dot(vLocal, vLocal);
            if (dist > 1.0) discard;
            // Ring mode: discard pixels inside the inner radius (when innerRadius > 0)
            if (pc.innerRadius > 0.0 && dist < pc.innerRadius * pc.innerRadius) discard;
            FragColor = pc.color;
        }
        """;

    #endregion

    private VkPipelineSet(VkDeviceApi deviceApi, VkPipeline flat, VkPipeline textured, VkPipeline ellipse, VkPipeline page, VkPipeline stroke,
        VkPipeline flatMultiply, VkPipeline flatScreen, VkPipeline flatDarken, VkPipeline flatLighten)
    {
        _deviceApi = deviceApi;
        FlatPipeline = flat;
        TexturedPipeline = textured;
        EllipsePipeline = ellipse;
        PagePipeline = page;
        StrokePipeline = stroke;
        FlatMultiplyPipeline = flatMultiply;
        FlatScreenPipeline = flatScreen;
        FlatDarkenPipeline = flatDarken;
        FlatLightenPipeline = flatLighten;
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
        var pageFrag = CompileAndCreateModule(deviceApi, compiler, PageFragmentSource, "page.frag", ShaderKind.FragmentShader);
        var ellipseVert = CompileAndCreateModule(deviceApi, compiler, EllipseVertexSource, "ellipse.vert", ShaderKind.VertexShader);
        var ellipseFrag = CompileAndCreateModule(deviceApi, compiler, EllipseFragmentSource, "ellipse.frag", ShaderKind.FragmentShader);
        var strokeVert = CompileAndCreateModule(deviceApi, compiler, StrokeVertexSource, "stroke.vert", ShaderKind.VertexShader);
        var strokeFrag = CompileAndCreateModule(deviceApi, compiler, StrokeFragmentSource, "stroke.frag", ShaderKind.FragmentShader);

        try
        {
            // Flat pipeline: vec2 pos only
            VkVertexInputBindingDescription flatBinding = new(2 * sizeof(float));
            VkVertexInputAttributeDescription flatAttr = new(0, VkFormat.R32G32Sfloat, 0);
            var msaa = ctx.MsaaSamples;
            var flat = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, msaaSamples: msaa);

            // Textured pipeline: vec2 pos + vec2 uv (for font atlas glyphs)
            VkVertexInputBindingDescription texBinding = new(4 * sizeof(float));
            var texAttrs = stackalloc VkVertexInputAttributeDescription[2];
            texAttrs[0] = new(0, VkFormat.R32G32Sfloat, 0);
            texAttrs[1] = new(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));
            var textured = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, texVert, texFrag,
                &texBinding, 1, texAttrs, 2, msaaSamples: msaa);

            // Page pipeline: same vertex layout as textured, but pass-through fragment shader
            var page = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, texVert, pageFrag,
                &texBinding, 1, texAttrs, 2, msaaSamples: msaa);

            // Ellipse pipeline: vec2 pos + vec2 local
            VkVertexInputBindingDescription ellipseBinding = new(4 * sizeof(float));
            var ellipseAttrs = stackalloc VkVertexInputAttributeDescription[2];
            ellipseAttrs[0] = new(0, VkFormat.R32G32Sfloat, 0);
            ellipseAttrs[1] = new(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));
            var ellipse = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, ellipseVert, ellipseFrag,
                &ellipseBinding, 1, ellipseAttrs, 2, msaaSamples: msaa);

            // Stroke pipeline: vec2 P0 + vec2 P1 + vec2 params (side, end)
            VkVertexInputBindingDescription strokeBinding = new(6 * sizeof(float));
            var strokeAttrs = stackalloc VkVertexInputAttributeDescription[3];
            strokeAttrs[0] = new(0, VkFormat.R32G32Sfloat, 0);                  // aP0
            strokeAttrs[1] = new(1, VkFormat.R32G32Sfloat, 2 * sizeof(float));  // aP1
            strokeAttrs[2] = new(2, VkFormat.R32G32Sfloat, 4 * sizeof(float));  // aParams
            var stroke = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, strokeVert, strokeFrag,
                &strokeBinding, 1, strokeAttrs, 3, msaaSamples: msaa);

            // Blend mode variants of the flat pipeline
            var flatMultiply = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.DstColor, VkBlendFactor.OneMinusSrcAlpha, msaaSamples: msaa);
            var flatScreen = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.One, VkBlendFactor.OneMinusSrcColor, msaaSamples: msaa);
            var flatDarken = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.One, VkBlendFactor.One, VkBlendOp.Min, msaa);
            var flatLighten = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.One, VkBlendFactor.One, VkBlendOp.Max, msaa);

            return new VkPipelineSet(deviceApi, flat, textured, ellipse, page, stroke,
                flatMultiply, flatScreen, flatDarken, flatLighten);
        }
        finally
        {
            deviceApi.vkDestroyShaderModule(flatVert);
            deviceApi.vkDestroyShaderModule(flatFrag);
            deviceApi.vkDestroyShaderModule(texVert);
            deviceApi.vkDestroyShaderModule(texFrag);
            deviceApi.vkDestroyShaderModule(pageFrag);
            deviceApi.vkDestroyShaderModule(ellipseVert);
            deviceApi.vkDestroyShaderModule(ellipseFrag);
            deviceApi.vkDestroyShaderModule(strokeVert);
            deviceApi.vkDestroyShaderModule(strokeFrag);
        }
    }

    public void Dispose()
    {
        _deviceApi.vkDestroyPipeline(FlatPipeline);
        _deviceApi.vkDestroyPipeline(FlatMultiplyPipeline);
        _deviceApi.vkDestroyPipeline(FlatScreenPipeline);
        _deviceApi.vkDestroyPipeline(FlatDarkenPipeline);
        _deviceApi.vkDestroyPipeline(FlatLightenPipeline);
        _deviceApi.vkDestroyPipeline(TexturedPipeline);
        _deviceApi.vkDestroyPipeline(EllipsePipeline);
        _deviceApi.vkDestroyPipeline(PagePipeline);
        _deviceApi.vkDestroyPipeline(StrokePipeline);
    }

    private static VkPipeline CreatePipeline(
        VkDeviceApi deviceApi, VkRenderPass renderPass, VkPipelineLayout layout,
        VkShaderModule vertModule, VkShaderModule fragModule,
        VkVertexInputBindingDescription* bindings, uint bindingCount,
        VkVertexInputAttributeDescription* attributes, uint attributeCount,
        VkBlendFactor srcColorFactor = VkBlendFactor.SrcAlpha,
        VkBlendFactor dstColorFactor = VkBlendFactor.OneMinusSrcAlpha,
        VkBlendOp blendOp = VkBlendOp.Add,
        VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1)
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

        VkPipelineMultisampleStateCreateInfo multisample = new()
        {
            rasterizationSamples = msaaSamples
        };

        VkPipelineColorBlendAttachmentState blendAttachment = new()
        {
            colorWriteMask = VkColorComponentFlags.All,
            blendEnable = true,
            srcColorBlendFactor = srcColorFactor,
            dstColorBlendFactor = dstColorFactor,
            colorBlendOp = blendOp,
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
