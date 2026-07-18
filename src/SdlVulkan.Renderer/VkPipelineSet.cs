using System.Collections.Concurrent;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

public sealed unsafe class VkPipelineSet : IDisposable
{
    // SPIR-V is pre-baked at build time (tools/BakeShaders) and embedded as resources, so there is no
    // runtime GLSL->SPIR-V compilation -- and no shaderc dependency, which ships no Android RID. The
    // bytecode is identical for every device/window; cache it per shader for the process lifetime so
    // each renderer -- each additional window, or a tab torn into its own window -- only creates
    // per-device shader modules from the cached bytes rather than re-reading the embedded stream.
    // Keyed by the shader's stable file name. Thread-safe so background window creation is safe.
    private static readonly ConcurrentDictionary<string, byte[]> SpirvCache = new();
    public VkPipeline FlatPipeline { get; }
    public VkPipeline TexturedPipeline { get; }
    public VkPipeline EllipsePipeline { get; }
    public VkPipeline PagePipeline { get; }
    public VkPipeline StrokePipeline { get; }
    public VkPipeline SdfPipeline { get; }

    // Blend mode variants of FlatPipeline
    public VkPipeline FlatMultiplyPipeline { get; }
    public VkPipeline FlatScreenPipeline { get; }
    public VkPipeline FlatDarkenPipeline { get; }
    public VkPipeline FlatLightenPipeline { get; }

    private readonly VkDeviceApi _deviceApi;

    // GLSL 450 shader sources live in Shaders/*.vert / Shaders/*.frag (with their rationale comments)
    // and are baked to Shaders/spirv/*.spv by tools/BakeShaders. Re-run that tool and commit the .spv
    // whenever a shader source changes.

    private VkPipelineSet(VkDeviceApi deviceApi, VkPipeline flat, VkPipeline textured, VkPipeline ellipse, VkPipeline page, VkPipeline stroke,
        VkPipeline sdf,
        VkPipeline flatMultiply, VkPipeline flatScreen, VkPipeline flatDarken, VkPipeline flatLighten)
    {
        _deviceApi = deviceApi;
        FlatPipeline = flat;
        TexturedPipeline = textured;
        EllipsePipeline = ellipse;
        PagePipeline = page;
        StrokePipeline = stroke;
        SdfPipeline = sdf;
        FlatMultiplyPipeline = flatMultiply;
        FlatScreenPipeline = flatScreen;
        FlatDarkenPipeline = flatDarken;
        FlatLightenPipeline = flatLighten;
    }

    public static VkPipelineSet Create(VulkanContext ctx)
    {
        var deviceApi = ctx.DeviceApi;

        // Shader modules from pre-baked SPIR-V embedded resources -- no runtime shaderc.
        var flatVert = LoadEmbeddedModule(deviceApi, "flat.vert");
        var flatFrag = LoadEmbeddedModule(deviceApi, "flat.frag");
        var texVert = LoadEmbeddedModule(deviceApi, "tex.vert");
        var texFrag = LoadEmbeddedModule(deviceApi, "tex.frag");
        var pageFrag = LoadEmbeddedModule(deviceApi, "page.frag");
        var ellipseVert = LoadEmbeddedModule(deviceApi, "ellipse.vert");
        var ellipseFrag = LoadEmbeddedModule(deviceApi, "ellipse.frag");
        var strokeVert = LoadEmbeddedModule(deviceApi, "stroke.vert");
        var strokeFrag = LoadEmbeddedModule(deviceApi, "stroke.frag");
        var sdfFrag = LoadEmbeddedModule(deviceApi, "sdf.frag");

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

            // SDF pipeline: same vertex layout as textured, SDF fragment shader
            var sdf = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, texVert, sdfFrag,
                &texBinding, 1, texAttrs, 2, msaaSamples: msaa);

            // Blend mode variants of the flat pipeline
            var flatMultiply = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.DstColor, VkBlendFactor.OneMinusSrcAlpha, msaaSamples: msaa);
            var flatScreen = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.One, VkBlendFactor.OneMinusSrcColor, msaaSamples: msaa);
            var flatDarken = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.One, VkBlendFactor.One, VkBlendOp.Min, msaa);
            var flatLighten = CreatePipeline(deviceApi, ctx.RenderPass, ctx.PipelineLayout, flatVert, flatFrag,
                &flatBinding, 1, &flatAttr, 1, VkBlendFactor.One, VkBlendFactor.One, VkBlendOp.Max, msaa);

            return new VkPipelineSet(deviceApi, flat, textured, ellipse, page, stroke, sdf,
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
            deviceApi.vkDestroyShaderModule(sdfFrag);
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
        _deviceApi.vkDestroyPipeline(SdfPipeline);
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

        var blendAttachments = stackalloc VkPipelineColorBlendAttachmentState[1];
        blendAttachments[0] = new VkPipelineColorBlendAttachmentState
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
            pColorBlendState = &colorBlend,
            pDynamicState = &dynamicState,
            layout = layout,
            renderPass = renderPass,
            subpass = 0
        };

        deviceApi.vkCreateGraphicsPipeline(pipelineCI, out var pipeline).CheckResult();
        return pipeline;
    }

    private static VkShaderModule LoadEmbeddedModule(VkDeviceApi deviceApi, string shaderName)
    {
        // Read the pre-baked SPIR-V once per process (cache hit on every renderer after the first),
        // then create the per-device shader module from the cached bytecode. shaderName is the stable
        // source file name ("flat.vert"); the embedded logical name is set in the .csproj.
        var spirv = SpirvCache.GetOrAdd(shaderName, static name =>
        {
            var resource = $"SdlVulkan.Renderer.Shaders.{name}.spv";
            using var stream = typeof(VkPipelineSet).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Embedded shader '{resource}' not found -- run tools/BakeShaders and commit Shaders/spirv/*.spv.");
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        });

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
