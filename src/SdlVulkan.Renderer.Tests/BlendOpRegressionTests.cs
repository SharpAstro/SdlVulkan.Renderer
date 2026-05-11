using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Regression coverage for the dangling-pointer bug in <c>VkPipelineSet.CreatePipeline</c>
/// fixed in SdlVulkan.Renderer 3.4.471. The old code used the single-argument
/// <c>new VkPipelineColorBlendStateCreateInfo(blendAttachment)</c> constructor, which copies
/// its argument onto its own stack frame and stores <c>pAttachments = &amp;attachment</c>
/// pointing at that frame. The constructor's stack is reclaimed when it returns, so the
/// subsequent <c>vkCreateGraphicsPipeline</c> read garbage blend ops out of stale memory.
///
/// On debug Mesa the assertion <c>vk_blend_op_to_pipe: Invalid blend op</c> at
/// <c>src/vulkan/runtime/vk_blend.c:66</c> trips. On release Mesa lavapipe x86_64 the
/// garbage decoded to zeroed fragment writes, producing fully-black framebuffers. On
/// hardware GPUs the garbage decoded to whatever <c>VkBlendOp</c> values happened to be on
/// the stack, so output was wrong but didn't crash.
///
/// The fix replaces the single-argument constructor with an explicit
/// <c>stackalloc VkPipelineColorBlendAttachmentState[1]</c> whose lifetime spans the
/// <c>vkCreateGraphicsPipeline</c> call.
///
/// Tests skip when Vulkan isn't loadable on the host (no ICD, no libvulkan, etc.).
/// </summary>
public sealed unsafe class BlendOpRegressionTests
{
    private const uint Width = 64;
    private const uint Height = 64;

    /// <summary>
    /// Smoke test: full <see cref="VkPipelineSet.Create"/> succeeds and every pipeline handle
    /// is non-null. <c>Create</c> internally builds 10 pipelines back-to-back, four of which
    /// pass non-default blend factor / blend op combinations -- exactly the path that used
    /// to corrupt across consecutive <c>CreatePipeline</c> calls. On debug Mesa the
    /// assertion fires before <c>Create</c> returns; on any driver, a non-null handle here
    /// proves <c>vkCreateGraphicsPipeline</c> accepted the blend state without complaint.
    /// </summary>
    [Fact]
    public void VkPipelineSet_AllPipelinesCreatedSuccessfully()
    {
        if (!TryCreateOffscreenContext(out var instance, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var pipelines = VkPipelineSet.Create(ctx!);
            pipelines.FlatPipeline.Handle.ShouldNotBe((ulong)0, "FlatPipeline must be created");
            pipelines.TexturedPipeline.Handle.ShouldNotBe((ulong)0, "TexturedPipeline must be created");
            pipelines.EllipsePipeline.Handle.ShouldNotBe((ulong)0, "EllipsePipeline must be created");
            pipelines.PagePipeline.Handle.ShouldNotBe((ulong)0, "PagePipeline must be created");
            pipelines.StrokePipeline.Handle.ShouldNotBe((ulong)0, "StrokePipeline must be created");
            pipelines.SdfPipeline.Handle.ShouldNotBe((ulong)0, "SdfPipeline must be created");
            // The four pipelines below pass non-default blend ops / factors -- the exact
            // arguments the dangling pAttachments bug used to corrupt across creation calls.
            pipelines.FlatMultiplyPipeline.Handle.ShouldNotBe((ulong)0, "FlatMultiplyPipeline (DstColor/OneMinusSrcAlpha, Add) must be created");
            pipelines.FlatScreenPipeline.Handle.ShouldNotBe((ulong)0, "FlatScreenPipeline (One/OneMinusSrcColor, Add) must be created");
            pipelines.FlatDarkenPipeline.Handle.ShouldNotBe((ulong)0, "FlatDarkenPipeline (One/One, Min) must be created");
            pipelines.FlatLightenPipeline.Handle.ShouldNotBe((ulong)0, "FlatLightenPipeline (One/One, Max) must be created");
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    /// <summary>
    /// Render verification: clear the offscreen framebuffer to a known dst color, draw a
    /// fullscreen quad with a known src color through <see cref="VkPipelineSet.FlatDarkenPipeline"/>
    /// (<c>VkBlendOp.Min</c>, factors <c>(One, One)</c>) and <see cref="VkPipelineSet.FlatLightenPipeline"/>
    /// (<c>VkBlendOp.Max</c>, same factors), and assert per-channel output matches
    /// <c>min(src, dst)</c> / <c>max(src, dst)</c>. If the dangling-pointer bug ever returned,
    /// the blend op for these pipelines would be garbage (typically <c>Add</c>) and the output
    /// would not match -- catches the regression on hardware drivers where pipeline creation
    /// silently passes garbage through.
    /// </summary>
    [Theory]
    // pipelineName , srcR, srcG, srcB, dstR, dstG, dstB, expR, expG, expB
    [InlineData("darken", 76,  178, 76,  128, 128, 128, 76,  128, 76)]   // min(src, dst) per channel
    [InlineData("lighten", 76, 178, 76,  128, 128, 128, 128, 178, 128)]  // max(src, dst) per channel
    public void VkPipelineSet_BlendOp_ProducesExpectedPixels(
        string pipelineName,
        int srcR, int srcG, int srcB,
        int dstR, int dstG, int dstB,
        int expR, int expG, int expB)
    {
        if (!TryCreateOffscreenContext(out var instance, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var pipelines = VkPipelineSet.Create(ctx!);
            using var renderer = new VkRenderer(ctx!, Width, Height);

            var blendPipeline = pipelineName switch
            {
                "darken" => pipelines.FlatDarkenPipeline,
                "lighten" => pipelines.FlatLightenPipeline,
                _ => throw new ArgumentOutOfRangeException(nameof(pipelineName)),
            };

            // Clear-color (dst) is the input the blend op pulls from the framebuffer. Drawn
            // src is the constant color the fragment shader emits via push constants.
            var dst = new RGBAColor32((byte)dstR, (byte)dstG, (byte)dstB, 255);
            renderer.BeginOffscreenFrame(dst).ShouldBeTrue();
            var cmd = renderer.CurrentCommandBuffer;
            var api = ctx!.DeviceApi;

            // Fullscreen-quad vertices in [0, W] x [0, H] pixel space. FlatPipeline reads
            // vec2 positions; the proj matrix below maps [0..W, 0..H] to NDC [-1..1].
            ReadOnlySpan<float> vertices =
            [
                0,         0,
                Width,     0,
                Width,     Height,
                0,         0,
                Width,     Height,
                0,         Height,
            ];
            var vboOffset = ctx.WriteVertices(vertices);
            vboOffset.ShouldNotBe(uint.MaxValue, "vertex frame allocator must have space for 6 vec2s");

            // Push constants: column-major mat4 proj (16 floats) + vec4 color (4 floats) +
            // 1 trailing float padding for the 84-byte push range the pipeline layout uses.
            Span<float> pc = stackalloc float[21];
            pc[0]  = 2f / Width;   // proj[0][0] (X scale)
            pc[5]  = 2f / Height;  // proj[1][1] (Y scale)
            pc[10] = -1f;          // proj[2][2] (Z, unused for 2D z=0)
            pc[12] = -1f;          // proj[3][0] (X translate: x=0 -> NDC -1)
            pc[13] = -1f;          // proj[3][1]
            pc[15] = 1f;           // proj[3][3] (W = 1)
            pc[16] = srcR / 255f;
            pc[17] = srcG / 255f;
            pc[18] = srcB / 255f;
            pc[19] = 1f;           // src.alpha = 1 so alpha blend (One/OneMinusSrcAlpha, Add) yields 1

            api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, blendPipeline);
            fixed (float* pPC = pc)
                api.vkCmdPushConstants(cmd, ctx.PipelineLayout,
                    VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

            var buffer = ctx.VertexBuffer;
            var vkOff = (ulong)vboOffset;
            api.vkCmdBindVertexBuffers(cmd, 0, 1, &buffer, &vkOff);
            api.vkCmdDraw(cmd, 6, 1, 0, 0);

            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();

            // ReadbackOffscreenRgba returns R, G, B, A per pixel (swizzles BGRA -> RGBA).
            var rgba = ctx.ReadbackOffscreenRgba();
            rgba.Length.ShouldBe((int)(Width * Height * 4));

            // Sample center pixel. Any pixel works -- the fullscreen quad fills the framebuffer
            // with the same blend result everywhere.
            var center = ((int)(Height / 2) * (int)Width + (int)(Width / 2)) * 4;
            var actR = rgba[center + 0];
            var actG = rgba[center + 1];
            var actB = rgba[center + 2];
            var actA = rgba[center + 3];

            // BGRA8Unorm round-trip through 0..1 floats: a one-byte rounding tolerance covers
            // the half-LSB error of clear-color quantisation and the source-color conversion.
            const int tol = 1;
            Math.Abs(actR - expR).ShouldBeLessThanOrEqualTo(tol,
                $"[{pipelineName}] R byte: expected ~{expR}, got {actR} (src=({srcR},{srcG},{srcB}), dst=({dstR},{dstG},{dstB}))");
            Math.Abs(actG - expG).ShouldBeLessThanOrEqualTo(tol,
                $"[{pipelineName}] G byte: expected ~{expG}, got {actG}");
            Math.Abs(actB - expB).ShouldBeLessThanOrEqualTo(tol,
                $"[{pipelineName}] B byte: expected ~{expB}, got {actB}");
            actA.ShouldBe((byte)255, $"[{pipelineName}] alpha should round-trip to 255 (One*1 + OneMinusSrcAlpha*1 = 1)");
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    /// <summary>
    /// Best-effort offscreen Vulkan setup. Returns false when the host has no Vulkan ICD
    /// (e.g. CI runner without mesa-vulkan-drivers, macOS without MoltenVK).
    /// </summary>
    private static bool TryCreateOffscreenContext(out VkInstance instance, out VulkanContext? ctx)
    {
        instance = default;
        ctx = null;
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out instance).CheckResult();
            ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
