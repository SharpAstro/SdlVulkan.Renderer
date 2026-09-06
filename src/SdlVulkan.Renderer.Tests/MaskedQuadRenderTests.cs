using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// End-to-end verification that <see cref="VkRenderer.DrawMaskedQuad"/> removes exactly what its
/// coverage mask says and nothing else.
///
/// <para>A solid red texture is drawn over a black target through a mask that is opaque on its left
/// half and zero on its right. The left half of the target must come out red and the right half must
/// stay black. Drawn WITHOUT the mask the same texture covers the whole target, and that control
/// draw is what makes the result mean something: a masked draw that silently ignored binding 1 would
/// look exactly like the control, and one that sampled the mask in the wrong space would cut the
/// wrong side.</para>
///
/// <para>The mask is deliberately a QUARTER of the texture's resolution. Sampling it in the
/// texture's own UV space is what lets it be, and that is the whole economic argument for the
/// pipeline: a mask that had to match the texture pixel for pixel would cost what baking the same
/// result into the texture's alpha costs.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class MaskedQuadRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Quarter = (int)Size / 4;
    private const int ThreeQuarters = 3 * (int)Size / 4;
    private const int TexDim = 8;
    private const int MaskDim = 2;   // deliberately coarser than the texture

    [Fact]
    public void DrawMaskedQuad_RemovesOnlyWhatTheMaskCovers()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);

        // The offscreen context belongs to the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Size, Size);
        using var texture = VkTexture.CreateFromBgra(ctx, SolidRed(), TexDim, TexDim);
        using var mask = VkTexture.CreateFromBgra(ctx, LeftHalfCoverage(), MaskDim, MaskDim);

        // Control: no mask, so the texture covers the target end to end.
        var unmasked = Render(renderer, ctx, r => r.DrawTexturedQuad(texture.DescriptorSet,
            0f, 0f, Size, 0f, 0f, Size, Size, Size));
        ChannelsAt(unmasked, Quarter, Quarter).ShouldBe((255, 0, 0), "the control draw should be red on the left");
        ChannelsAt(unmasked, ThreeQuarters, Quarter).ShouldBe((255, 0, 0), "the control draw should be red on the right");

        var maskedSet = texture.CreateMaskedDescriptorSet(mask);
        try
        {
            var masked = Render(renderer, ctx, r => r.DrawMaskedQuad(maskedSet,
                0f, 0f, Size, 0f, 0f, Size, Size, Size));

            ChannelsAt(masked, Quarter, Quarter)
                .ShouldBe((255, 0, 0), "the covered half must be drawn unchanged");
            ChannelsAt(masked, Quarter, ThreeQuarters)
                .ShouldBe((255, 0, 0), "coverage is horizontal, so the lower left stays drawn too");

            // The decisive pair: both were red in the control draw.
            ChannelsAt(masked, ThreeQuarters, Quarter)
                .ShouldBe((0, 0, 0), "the uncovered half must be gone, not merely dimmer");
            ChannelsAt(masked, ThreeQuarters, ThreeQuarters)
                .ShouldBe((0, 0, 0), "the uncovered half must be gone along its whole height");
        }
        finally
        {
            ctx.FreeMaskedDescriptorSet(maskedSet);
        }
    }

    /// <summary>An opaque red texture, so any surviving pixel is unambiguous.</summary>
    private static byte[] SolidRed()
    {
        var data = new byte[TexDim * TexDim * 4];
        for (var i = 0; i < TexDim * TexDim; i++)
        {
            data[i * 4] = 0;            // B
            data[i * 4 + 1] = 0;        // G
            data[i * 4 + 2] = 255;      // R
            data[i * 4 + 3] = 255;      // A
        }
        return data;
    }

    /// <summary>Coverage: full on the left column, zero on the right. Grey rather than red, so the
    /// test does not depend on which channel the shader reads out of a BGRA upload.</summary>
    private static byte[] LeftHalfCoverage()
    {
        var data = new byte[MaskDim * MaskDim * 4];
        for (var y = 0; y < MaskDim; y++)
        for (var x = 0; x < MaskDim; x++)
        {
            var v = (byte)(x < MaskDim / 2 ? 255 : 0);
            var i = (y * MaskDim + x) * 4;
            data[i] = v; data[i + 1] = v; data[i + 2] = v; data[i + 3] = 255;
        }
        return data;
    }

    private static byte[] Render(VkRenderer renderer, VulkanContext ctx, Action<VkRenderer> draw)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        rgba.Length.ShouldBe((int)(Size * Size * 4));
        return rgba;
    }

    private static (int R, int G, int B) ChannelsAt(byte[] rgba, int x, int y)
    {
        var i = (y * (int)Size + x) * 4;
        // Sample points sit at quadrant centres, far from the mask's own edge, so linear filtering
        // across that edge never reaches them and every sample is at one extreme or the other.
        static int Snap(byte v) => v > 127 ? 255 : 0;
        return (Snap(rgba[i]), Snap(rgba[i + 1]), Snap(rgba[i + 2]));
    }
}
