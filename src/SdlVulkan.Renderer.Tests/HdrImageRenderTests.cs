using System;
using System.Linq;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="VkRenderer.DrawHdrImage"/> on an SDR (8-bit) offscreen target: float textures of known values are drawn
/// and the read-back pixels must equal the CPU result of <see cref="HdrColor.ToSdrEncoded"/> to within one 8-bit step,
/// which is what holds <c>hdrimage.frag</c> to being a transcription of the tested maths.
/// <para>Each case is a 1x1 float texture drawn over its own 16x16 cell, sampled at the cell's centre, so no filtering
/// is involved. Skips when there is no Vulkan ICD, like the other render tests.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class HdrImageRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Cell = 16;
    private const float SourcePeakNits = 1000f;

    private static readonly (string Name, float R, float G, float B, HdrPrimaries Primaries)[] Cases =
    [
        ("black", 0f, 0f, 0f, HdrPrimaries.Bt709),
        ("reference white", 1f, 1f, 1f, HdrPrimaries.Bt709),
        ("highlight above 1.0", 4f, 2f, 0.5f, HdrPrimaries.Bt709),
        ("midtone under the knee", 0.25f, 0.5f, 0.125f, HdrPrimaries.Bt709),
        ("BT.2020 green primary", 0f, 1f, 0f, HdrPrimaries.Bt2020),
    ];

    [Fact]
    public void DrawHdrImage_OnAnSdrTarget_EqualsTheCpuToneMap_WithinOneStep()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var width = Cell * (uint)Cases.Length;
        ctx.ResizeOffscreen(width, Cell);
        using var renderer = new VkRenderer(ctx, width, Cell);

        // Nothing HDR is built until an HDR image is drawn.
        renderer.Pipelines!.HasHdrImagePipeline.ShouldBeFalse();

        var textures = Cases.Select(c => VkTexture.CreateFromRgbaFloat(ctx, [c.R, c.G, c.B, 1f], 1, 1)).ToArray();
        try
        {
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            for (var i = 0; i < Cases.Length; i++)
                renderer.DrawHdrImage(textures[i], i * Cell, 0f, Cell, Cell, Cases[i].Primaries, SourcePeakNits);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            var rgba = ctx.ReadbackOffscreenRgba();

            renderer.Pipelines.HasHdrImagePipeline.ShouldBeTrue();
            var peak = HdrColor.Headroom(SourcePeakNits);

            for (var i = 0; i < Cases.Length; i++)
            {
                var c = Cases[i];
                // The texture holds halves, so the CPU side takes the same rounded inputs.
                var (er, eg, eb) = HdrColor.ToSdrEncoded(c.Primaries, Half(c.R), Half(c.G), Half(c.B), peak);
                var px = (int)(i * Cell + Cell / 2);
                var py = (int)(Cell / 2);
                var o = (py * (int)width + px) * 4;
                Check(c.Name, "R", rgba[o], er);
                Check(c.Name, "G", rgba[o + 1], eg);
                Check(c.Name, "B", rgba[o + 2], eb);
                rgba[o + 3].ShouldBe((byte)255, $"{c.Name} alpha");
            }

            // Sanity that the cases are not all trivially equal: black is black, the highlight is a bright pixel.
            var blackAt = (int)(Cell / 2 * width + Cell / 2) * 4;
            var highlightAt = (int)(Cell / 2 * width + 2 * Cell + Cell / 2) * 4;
            rgba[blackAt].ShouldBe((byte)0);
            rgba[highlightAt].ShouldBeGreaterThan((byte)200);
        }
        finally
        {
            foreach (var t in textures) t.Dispose();
        }
    }

    private static float Half(float v) => (float)(System.Half)v;

    private static void Check(string name, string channel, byte actual, float expectedEncoded)
    {
        var expected = (int)MathF.Round(Math.Clamp(expectedEncoded, 0f, 1f) * 255f);
        Math.Abs(actual - expected).ShouldBeLessThanOrEqualTo(1, $"{name} {channel}: GPU {actual}, CPU {expected}");
    }
}
