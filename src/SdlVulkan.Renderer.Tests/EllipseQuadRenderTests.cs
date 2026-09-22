using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Render coverage for the quad overrides of <c>FillEllipse</c> and <c>DrawEllipse</c>, which take
/// the four corners of a parallelogram instead of an axis-aligned <see cref="RectInt"/> and so can
/// express a rotated or sheared ellipse. Both are declared on <c>Renderer</c> with a CPU default
/// (see DIR.Lib's own AffineEllipseTests); what these cover is the Vulkan override of them.
/// <para>
/// The discriminating test is the 45° one. A rotation by a right angle is only a swap of width and
/// height, so an implementation that quietly took the bounding box of the corners would still pass
/// it; at 45° the bounding box is a circle enclosing the ellipse, and the four diagonal probes below
/// separate them — the two on the minor axis lie inside that circle and outside the real shape.
/// </para>
/// Tests skip when Vulkan isn't loadable on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class EllipseQuadRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 64;
    private const uint Height = 64;

    private static readonly RGBAColor32 Backdrop = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 Ink = new RGBAColor32(255, 255, 255, 255);

    // A long thin ellipse lying on the x axis: centre (32,32), semi-axes 30 and 8.
    private static RectInt FlatRect => new RectInt(new PointInt(62, 40), new PointInt(2, 24));

    // The same ellipse turned 45° clockwise about its centre: semi-major 28 down-right, semi-minor 7
    // up-right. Corners are the images of local (-1,-1), (+1,-1), (+1,+1), (-1,+1).
    private const float U = 19.7990f;   // (28/√2) — the semi-major vector is (U, U)
    private const float V = 4.9497f;    // (7/√2)  — the semi-minor vector is (-V, V)
    private static (float X, float Y) C00 => (32f - U + V, 32f - U - V);
    private static (float X, float Y) C10 => (32f + U + V, 32f + U - V);
    private static (float X, float Y) C11 => (32f + U - V, 32f + U + V);
    private static (float X, float Y) C01 => (32f - U - V, 32f - U + V);

    private static (byte R, byte G, byte B) PixelAt(byte[] rgba, int x, int y)
    {
        var at = (y * (int)Width + x) * 4;
        return (rgba[at], rgba[at + 1], rgba[at + 2]);
    }

    private static void ShouldBeInk(byte[] rgba, int x, int y, string because)
        => PixelAt(rgba, x, y).ShouldBe(((byte)255, (byte)255, (byte)255), because);

    private static void ShouldBeBackdrop(byte[] rgba, int x, int y, string because)
        => PixelAt(rgba, x, y).ShouldBe(((byte)0, (byte)0, (byte)0), because);

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
    public void FillEllipse_ByRect_IsUnchangedByTheQuadRefactor()
    {
        var rgba = RenderToPixels(r => r.FillEllipse(FlatRect, Ink));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ShouldBeInk(rgba, 32, 32, "the centre");
        ShouldBeInk(rgba, 58, 32, "26px along the 30px semi-major axis");
        ShouldBeBackdrop(rgba, 32, 58, "26px along the 8px semi-minor axis is well outside");
        ShouldBeBackdrop(rgba, 3, 25, "the rect's corner is outside the inscribed ellipse");
    }

    /// <summary>
    /// The rect overload is now a thin wrapper that expands the rect to its four corners, so the two
    /// entry points must agree to the byte. This is the guard on that delegation: any divergence in
    /// vertex order, local coordinates or push constants shows up as a whole-framebuffer difference.
    /// </summary>
    [Fact]
    public void FillEllipse_RectAndItsOwnCornersRenderIdentically()
    {
        var viaRect = RenderToPixels(r => r.FillEllipse(FlatRect, Ink));
        if (viaRect is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var viaQuad = RenderToPixels(r => r.FillEllipse((2f, 24f), (62f, 24f), (62f, 40f), (2f, 40f), Ink));
        viaQuad.ShouldNotBeNull();
        viaQuad.ShouldBe(viaRect);
    }

    [Fact]
    public void FillEllipse_ByQuad_TurnsWithTheParallelogramRatherThanItsBoundingBox()
    {
        var rgba = RenderToPixels(r => r.FillEllipse(C00, C10, C11, C01, Ink));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        // On the major axis, at 0.8 of it — inside whichever way this is implemented.
        ShouldBeInk(rgba, 48, 48, "down-right along the semi-major axis");
        ShouldBeInk(rgba, 16, 16, "up-left along the semi-major axis");

        // On the minor axis, 22.6px out where the semi-minor is 7. Both of these sit INSIDE the
        // bounding box of the four corners, so they fail on an axis-aligned reading of them.
        ShouldBeBackdrop(rgba, 48, 16, "up-right is across the 7px semi-minor axis");
        ShouldBeBackdrop(rgba, 16, 48, "down-left is across the 7px semi-minor axis");
    }

    /// <summary>
    /// The positive control for the test above, and the reason those two probes were chosen. The
    /// bounding box of the rotated corners spans (7,7)-(57,57), whose inscribed ellipse is a circle
    /// of radius 25 — and that circle DOES cover both minor-axis probes. So the previous test fails
    /// on a bounding-box implementation rather than merely looking as though it would.
    /// </summary>
    [Fact]
    public void TheBoundingBoxOfThoseCornersCoversTheProbesTheRotatedEllipseRejects()
    {
        var rgba = RenderToPixels(r => r.FillEllipse(new RectInt(new PointInt(57, 57), new PointInt(7, 7)), Ink));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ShouldBeInk(rgba, 48, 16, "inside the corners' bounding circle");
        ShouldBeInk(rgba, 16, 48, "inside the corners' bounding circle");
    }

    [Fact]
    public void DrawEllipse_ByQuad_LeavesTheCentreHollow()
    {
        var rgba = RenderToPixels(r => r.DrawEllipse(C00, C10, C11, C01, Ink, innerRadius: 0.5f));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ShouldBeBackdrop(rgba, 32, 32, "the centre is inside the hole");
        ShouldBeInk(rgba, 48, 48, "0.8 along the major axis is between the hole and the rim");
        ShouldBeBackdrop(rgba, 48, 16, "the ring is still an ellipse, not its bounding box");
    }

    /// <summary>An innerRadius of 0 is the fill, which is what lets the two entry points share one draw.</summary>
    [Fact]
    public void DrawEllipse_ByQuad_WithNoHoleMatchesTheFill()
    {
        var filled = RenderToPixels(r => r.FillEllipse(C00, C10, C11, C01, Ink));
        if (filled is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var ring = RenderToPixels(r => r.DrawEllipse(C00, C10, C11, C01, Ink, innerRadius: 0f));
        ring.ShouldNotBeNull();
        ring.ShouldBe(filled);
    }
}
