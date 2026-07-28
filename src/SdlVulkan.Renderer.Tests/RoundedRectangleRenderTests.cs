using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Render coverage for <see cref="VkRenderer.FillRoundedRectangle"/>, the GPU override of DIR.Lib's
/// scanline fallback. Drives the real pipeline against the offscreen framebuffer and reads pixels back,
/// so a shader that compiles but draws the wrong shape is still caught.
/// <para>
/// The property worth protecting is that this stays <b>one quad</b>. A decomposition into overlapping
/// primitives looks right with an opaque colour and wrong with a translucent one, because the overlaps
/// blend twice and darken the corners -- which is exactly the trap DIR.Lib's CPU fallback is shaped to
/// avoid, and a translucent panel background is the case this primitive exists to serve.
/// </para>
/// Tests skip when Vulkan isn't loadable on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class RoundedRectangleRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 64;
    private const uint Height = 64;

    private static readonly RGBAColor32 Backdrop = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 Fill = new RGBAColor32(255, 255, 255, 255);

    private static RectInt FullRect => new RectInt(new PointInt((int)Width, (int)Height), new PointInt(0, 0));

    private static (byte R, byte G, byte B) PixelAt(byte[] rgba, int x, int y)
    {
        var at = (y * (int)Width + x) * 4;
        return (rgba[at], rgba[at + 1], rgba[at + 2]);
    }

    /// <summary>Renders one frame through <paramref name="draw"/> and reads the framebuffer back.</summary>
    private byte[]? RenderToPixels(System.Action<VkRenderer> draw)
    {
        if (gpu.Context is not { } ctx)
        {
            return null;
        }

        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Width, Height);
        renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        return ctx.ReadbackOffscreenRgba();
    }

    [Fact]
    public void FillRoundedRectangle_CutsTheCornersAndKeepsTheEdgesStraight()
    {
        var rgba = RenderToPixels(r => r.FillRoundedRectangle(FullRect, Fill, cornerRadius: 16f));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        PixelAt(rgba, 0, 0).ShouldBe(((byte)0, (byte)0, (byte)0), "the top-left corner is outside the arc");
        PixelAt(rgba, 63, 0).ShouldBe(((byte)0, (byte)0, (byte)0));
        PixelAt(rgba, 0, 63).ShouldBe(((byte)0, (byte)0, (byte)0));
        PixelAt(rgba, 63, 63).ShouldBe(((byte)0, (byte)0, (byte)0));

        PixelAt(rgba, 32, 32).ShouldBe(((byte)255, (byte)255, (byte)255), "the middle is filled");
        PixelAt(rgba, 32, 0).ShouldBe(((byte)255, (byte)255, (byte)255), "the top edge between the arcs is straight");
        PixelAt(rgba, 0, 32).ShouldBe(((byte)255, (byte)255, (byte)255), "and so is the left edge");
    }

    /// <summary>
    /// A zero radius must take the plain <see cref="VkRenderer.FillRectangle"/> path, so a caller can
    /// thread a radius through unconditionally and pay nothing when it is off.
    /// </summary>
    [Fact]
    public void FillRoundedRectangle_ZeroRadius_MatchesAPlainRectangle()
    {
        var rounded = RenderToPixels(r => r.FillRoundedRectangle(FullRect, Fill, cornerRadius: 0f));
        var square = RenderToPixels(r => r.FillRectangle(FullRect, Fill));
        if (rounded is null || square is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        rounded.ShouldBe(square);
    }

    /// <summary>
    /// The single-quad guarantee. Every pixel inside the shape is covered exactly once, so a translucent
    /// fill lands at the same value in the corners as in the middle. Overlapping draws would darken the
    /// four corners and pass every opaque test in this file.
    /// </summary>
    [Fact]
    public void FillRoundedRectangle_TranslucentFillIsEvenEverywhere()
    {
        var translucent = new RGBAColor32(255, 255, 255, 128);
        var rgba = RenderToPixels(r => r.FillRoundedRectangle(FullRect, translucent, cornerRadius: 20f));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var middle = PixelAt(rgba, 32, 32);
        middle.ShouldNotBe(((byte)0, (byte)0, (byte)0), "the fill must actually have blended");

        // Well inside each arc, where a second blend would show as a darker corner.
        foreach (var (x, y) in new[] { (10, 10), (53, 10), (10, 53), (53, 53) })
        {
            PixelAt(rgba, x, y).ShouldBe(middle, $"the corner at ({x},{y}) blended a different number of times");
        }
    }

    /// <summary>A radius larger than the shape must clamp to a circle, not invert the arc into a bow-tie.</summary>
    [Fact]
    public void FillRoundedRectangle_OverLargeRadius_ClampsToACircle()
    {
        var rgba = RenderToPixels(r => r.FillRoundedRectangle(FullRect, Fill, cornerRadius: 500f));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        PixelAt(rgba, 32, 32).ShouldBe(((byte)255, (byte)255, (byte)255), "the centre of a circle is filled");
        PixelAt(rgba, 32, 1).ShouldBe(((byte)255, (byte)255, (byte)255), "and so is the top of its vertical diameter");
        PixelAt(rgba, 0, 0).ShouldBe(((byte)0, (byte)0, (byte)0), "while the corners are fully cut away");
        PixelAt(rgba, 63, 63).ShouldBe(((byte)0, (byte)0, (byte)0));
    }
}
