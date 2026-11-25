using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="VkRenderer.DrawTexturedQuadRegions"/> draws many quads off one texture in one draw, and
/// must paint exactly what the same quads drawn one <see cref="VkRenderer.DrawTexturedQuadRegion"/> call
/// at a time paint: the same triangles, in the same order.
///
/// <para>The texture is four solid quadrants (red, green, blue, white), and each quad maps one quadrant,
/// so a quad that took another's UVs, a corner, or nothing at all shows as the wrong colour. The last quad
/// is opaque and overlaps the three before it, so the order inside the one draw is checked too: the later
/// quad must win where they overlap, as it does across separate draws.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class TexturedQuadRegionsRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int TexDim = 8;

    [Fact]
    public void DrawTexturedQuadRegions_PaintsWhatTheQuadsDrawnOneByOnePaint()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);

        // The offscreen context belongs to the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Size, Size);
        using var texture = VkTexture.CreateFromBgra(ctx, Quadrants(), TexDim, TexDim);

        // Inset half a texel from each quadrant's edge, so linear sampling never reaches the next one.
        const float h = 0.5f / TexDim;
        TexturedQuad[] quads =
        [
            Rect(0, 0, 32, 32, 0f + h, 0f + h, 0.5f - h, 0.5f - h),      // red, top left
            Rect(32, 0, 32, 32, 0.5f + h, 0f + h, 1f - h, 0.5f - h),     // green, top right
            Rect(0, 32, 32, 32, 0f + h, 0.5f + h, 0.5f - h, 1f - h),     // blue, bottom left
            Rect(16, 16, 32, 32, 0.5f + h, 0.5f + h, 1f - h, 1f - h),    // white, centre, over the three
        ];

        var oneByOne = Render(renderer, ctx, r =>
        {
            foreach (var q in quads)
                r.DrawTexturedQuadRegion(texture.DescriptorSet, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3,
                    q.U0, q.V0, q.U1, q.V1);
        });
        var batched = Render(renderer, ctx, r => r.DrawTexturedQuadRegions(texture.DescriptorSet, quads));

        ChannelsAt(batched, 8, 8).ShouldBe((255, 0, 0), "the first quad maps the red quadrant");
        ChannelsAt(batched, 56, 8).ShouldBe((0, 255, 0), "the second quad maps the green quadrant");
        ChannelsAt(batched, 8, 56).ShouldBe((0, 0, 255), "the third quad maps the blue quadrant");
        ChannelsAt(batched, 56, 56).ShouldBe((0, 0, 0), "nothing was drawn in the bottom right corner");
        ChannelsAt(batched, 24, 24).ShouldBe((255, 255, 255), "the last quad paints over the first inside the one draw");
        ChannelsAt(batched, 40, 40).ShouldBe((255, 255, 255), "the last quad covers the centre");

        batched.ShouldBe(oneByOne, "one draw must paint exactly what the four draws paint");
    }

    [Fact]
    public void DrawTexturedQuadRegions_WithNoQuadsDrawsNothing()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);
        using var texture = VkTexture.CreateFromBgra(ctx, Quadrants(), TexDim, TexDim);

        var empty = Render(renderer, ctx, r => r.DrawTexturedQuadRegions(texture.DescriptorSet, ReadOnlySpan<TexturedQuad>.Empty));

        ChannelsAt(empty, 16, 16).ShouldBe((0, 0, 0));
        ChannelsAt(empty, 48, 48).ShouldBe((0, 0, 0));
    }

    private static TexturedQuad Rect(float x, float y, float w, float h, float u0, float v0, float u1, float v1)
        => new(x, y, x + w, y, x, y + h, x + w, y + h, u0, v0, u1, v1);

    /// <summary>Four opaque quadrants, BGRA: red top left, green top right, blue bottom left, white
    /// bottom right.</summary>
    private static byte[] Quadrants()
    {
        var data = new byte[TexDim * TexDim * 4];
        for (var y = 0; y < TexDim; y++)
        for (var x = 0; x < TexDim; x++)
        {
            var (r, g, b) = (x < TexDim / 2, y < TexDim / 2) switch
            {
                (true, true) => (255, 0, 0),
                (false, true) => (0, 255, 0),
                (true, false) => (0, 0, 255),
                _ => (255, 255, 255),
            };
            var i = (y * TexDim + x) * 4;
            data[i] = (byte)b; data[i + 1] = (byte)g; data[i + 2] = (byte)r; data[i + 3] = 255;
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
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }
}
