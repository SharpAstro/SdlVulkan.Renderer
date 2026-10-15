using System;
using System.IO;
using System.Text;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// A transparent window hands its framebuffer to the compositor as PREMULTIPLIED alpha
/// (<see cref="VulkanContext.PresentsAlpha"/>), and nothing converts it on the way: so what the ordinary pipelines
/// leave over a transparent clear has to be premultiplied already. They blend colour with SrcAlpha /
/// OneMinusSrcAlpha and alpha with One / OneMinusSrcAlpha, which over a (0,0,0,0) clear gives exactly that.
/// This pins it, for flat fills, overlapping translucent fills, antialiased edges and text.
/// <para>
/// The sharpest check is the invariant every premultiplied pixel obeys: no colour channel exceeds alpha. A pipeline
/// whose alpha blend squared coverage (SrcAlpha for alpha too, the common mistake) would break it at every
/// antialiased edge, where colour would come out at c*a and alpha at a*a.
/// </para>
/// Tests skip when Vulkan isn't loadable on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class PremultipliedOutputTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 64;
    private const uint Height = 64;

    private static readonly RGBAColor32 Transparent = new(0, 0, 0, 0);
    private static readonly RGBAColor32 OpaqueBlue = new(0, 0, 255, 255);
    private static readonly RGBAColor32 HalfRed = new(255, 0, 0, 128);
    private static readonly RGBAColor32 OpaqueRed = new(255, 0, 0, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static RectInt Rect(int x0, int y0, int x1, int y1) => new(new PointInt(x1, y1), new PointInt(x0, y0));

    private static (byte R, byte G, byte B, byte A) PixelAt(byte[] rgba, int x, int y)
    {
        var at = (y * (int)Width + x) * 4;
        return (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]);
    }

    [Fact]
    public void DrawingOverATransparentClear_LeavesPremultipliedPixels()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Width, Height);
        const float size = 24f;
        renderer.OnPreFlush = () => renderer.PreWarmSdfGlyph(FontPath, size, new Rune('A'));

        renderer.BeginOffscreenFrame(Transparent).ShouldBeTrue();
        renderer.FillRectangle(Rect(0, 0, 16, 16), OpaqueBlue);
        renderer.FillRectangle(Rect(16, 0, 40, 16), HalfRed);
        renderer.FillRectangle(Rect(32, 0, 48, 16), HalfRed);   // over the first from x = 32 to 40
        renderer.FillEllipse(Rect(2, 20, 38, 62), OpaqueRed);   // antialiased edge all round
        renderer.BeginSdfGlyphBatch(White, size);
        renderer.AddBatchedSdfGlyphAtBaseline(FontPath, new Rune('A'), -1, baselineX: 42f, baselineY: 56f);
        renderer.EndGlyphBatch();
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();

        PixelAt(rgba, 56, 8).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)0), "an untouched pixel stays fully transparent");
        PixelAt(rgba, 8, 8).ShouldBe(((byte)0, (byte)0, (byte)255, (byte)255), "an opaque fill is opaque");

        // Half red: colour times alpha, 0.502 * 255 = 128, for both.
        var (r, g, b, a) = PixelAt(rgba, 20, 8);
        ((int)r).ShouldBeInRange(126, 130, "half red's colour is premultiplied");
        ((int)a).ShouldBeInRange(126, 130, "half red's alpha is its own");
        g.ShouldBe((byte)0);
        b.ShouldBe((byte)0);

        // Two halves over each other: alpha 0.502 + 0.502 * (1 - 0.502) = 0.752, so 192, and colour the same.
        (r, _, _, a) = PixelAt(rgba, 36, 8);
        ((int)r).ShouldBeInRange(189, 194, "overlapping translucent fills compose as premultiplied 'over'");
        ((int)a).ShouldBeInRange(189, 194, "and their alphas compose the same way");

        // Every pixel, edges and text included: no colour channel above alpha, beyond a rounding step.
        var partial = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var (pr, pg, pb, pa) = PixelAt(rgba, x, y);
            ((int)Math.Max(pr, Math.Max(pg, pb))).ShouldBeLessThanOrEqualTo(pa + 1, $"pixel ({x}, {y}) = ({pr}, {pg}, {pb}, {pa}) is not premultiplied");
            if (pa is > 0 and < 255)
                partial++;
        }
        // The edges and the glyph must actually have produced partial coverage, or the loop above checked nothing.
        partial.ShouldBeGreaterThan(40, "the ellipse's and the glyph's antialiased edges give partly covered pixels");
    }
}
