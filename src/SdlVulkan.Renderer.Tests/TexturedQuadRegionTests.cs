using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// End-to-end verification that <see cref="VkRenderer.DrawTexturedQuadRegion"/> maps the requested UV
/// sub-rectangle, and that <see cref="VkRenderer.DrawTexturedQuad"/> still maps the whole texture.
///
/// <para>
/// The source is an 8×8 texture divided into four solid 4×4 colour blocks, drawn to fill a 64×64
/// offscreen target. Drawing the whole texture must reproduce all four blocks in their own quadrants;
/// drawing the (0,0)-(0.5,0.5) sub-rect must fill the ENTIRE target with the top-left block's colour.
/// The two frames together pin the mapping down — a region draw that silently ignored its UVs would
/// look identical to the full draw, and one that mapped some other rect would show the wrong colour.
/// </para>
///
/// <para>
/// Sample points sit at quadrant centres, well inside each block, because the shared sampler filters
/// linearly: colours blend across a block boundary, so only the interiors are unambiguous.
/// </para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class TexturedQuadRegionTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Quarter = (int)Size / 4;
    private const int ThreeQuarters = 3 * (int)Size / 4;

    private const int TexDim = 8;

    [Fact]
    public void DrawTexturedQuadRegion_MapsOnlyTheRequestedSubRect()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);

        // The offscreen context belongs to the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Size, Size);
        using var texture = VkTexture.CreateFromBgra(ctx, QuadrantTexture(), TexDim, TexDim);

        // Whole texture: each 4x4 block lands in its own quadrant of the target.
        var full = RenderQuad(renderer, ctx, texture, 0f, 0f, 1f, 1f);
        ChannelsAt(full, Quarter, Quarter).ShouldBe((255, 0, 0), "top-left quadrant should be the red block");
        ChannelsAt(full, ThreeQuarters, Quarter).ShouldBe((0, 255, 0), "top-right quadrant should be the green block");
        ChannelsAt(full, Quarter, ThreeQuarters).ShouldBe((0, 0, 255), "bottom-left quadrant should be the blue block");
        ChannelsAt(full, ThreeQuarters, ThreeQuarters).ShouldBe((255, 255, 255), "bottom-right quadrant should be the white block");

        // Top-left quarter of the texture, stretched over the whole target: red everywhere.
        var region = RenderQuad(renderer, ctx, texture, 0f, 0f, 0.5f, 0.5f);
        ChannelsAt(region, Quarter, Quarter).ShouldBe((255, 0, 0));
        ChannelsAt(region, ThreeQuarters, Quarter).ShouldBe((255, 0, 0));
        ChannelsAt(region, Quarter, ThreeQuarters).ShouldBe((255, 0, 0));
        // The decisive one: this pixel showed WHITE when the whole texture was drawn.
        ChannelsAt(region, ThreeQuarters, ThreeQuarters)
            .ShouldBe((255, 0, 0), "the sub-rect must replace the whole quad, not just its own corner");
    }

    /// <summary>8×8 BGRA, four solid 4×4 blocks: red TL, green TR, blue BL, white BR.</summary>
    private static byte[] QuadrantTexture()
    {
        var data = new byte[TexDim * TexDim * 4];
        for (var y = 0; y < TexDim; y++)
        for (var x = 0; x < TexDim; x++)
        {
            var left = x < TexDim / 2;
            var top = y < TexDim / 2;
            // BGRA byte order.
            (byte b, byte g, byte r) = (top, left) switch
            {
                (true, true) => ((byte)0, (byte)0, (byte)255),      // red
                (true, false) => ((byte)0, (byte)255, (byte)0),     // green
                (false, true) => ((byte)255, (byte)0, (byte)0),     // blue
                (false, false) => ((byte)255, (byte)255, (byte)255) // white
            };
            var i = (y * TexDim + x) * 4;
            data[i] = b; data[i + 1] = g; data[i + 2] = r; data[i + 3] = 255;
        }
        return data;
    }

    /// <summary>Clears to opaque black and draws the texture over the whole target, mapping the given
    /// UV rect. Corner order matches DrawTexturedQuad: origin, right edge, bottom edge, far corner.</summary>
    private static byte[] RenderQuad(VkRenderer renderer, VulkanContext ctx, VkTexture texture,
        float u0, float v0, float u1, float v1)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        renderer.DrawTexturedQuadRegion(texture.DescriptorSet,
            0f, 0f,
            Size, 0f,
            0f, Size,
            Size, Size,
            u0, v0, u1, v1);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        rgba.Length.ShouldBe((int)(Size * Size * 4));
        return rgba;
    }

    private static (int R, int G, int B) ChannelsAt(byte[] rgba, int x, int y)
    {
        var i = (y * (int)Size + x) * 4;
        // Snap to the nearest extreme: the blocks are pure, and only filtering near a block edge
        // would land anywhere in between (the sample points deliberately avoid those).
        static int Snap(byte v) => v > 127 ? 255 : 0;
        return (Snap(rgba[i]), Snap(rgba[i + 1]), Snap(rgba[i + 2]));
    }
}
