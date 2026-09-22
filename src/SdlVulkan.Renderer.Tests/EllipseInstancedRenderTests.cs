using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Render coverage for <see cref="VkRenderer.DrawEllipseInstances"/>, the one-draw bulk form of the
/// ellipse primitive: each instance carries its own centre, semi-axis vectors, stroke width and
/// colour.
/// <para>
/// The load-bearing test is <see cref="OneInstanceMatchesTheSingleDrawPath"/>. Two pipelines draw
/// the same shape by the same pixel-distance rule — one with the padded corners computed on the
/// CPU and the colour in the push block, one with both derived in the vertex shader — and nothing
/// but a whole-framebuffer comparison would notice them drifting apart.
/// </para>
/// <para>
/// Its axis vectors are exact in binary — (20,20) and (-5,5) about (32,32) — but the padding each
/// path adds for the anti-aliased rim divides a pixel count by the axis LENGTH, a square root the
/// CPU and the GPU need not round identically. So the demand is one level of one channel, not
/// byte equality: a vertex a few ulp off can move an edge pixel's coverage by 1/255 and nothing
/// else, and anything larger is the two pipelines disagreeing about the shape.
/// </para>
/// Tests skip when Vulkan isn't loadable on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class EllipseInstancedRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 64;
    private const uint Height = 64;

    private static readonly RGBAColor32 Backdrop = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 Ink = new RGBAColor32(255, 255, 255, 255);
    private static readonly RGBAColor32 Red = new RGBAColor32(255, 0, 0, 255);
    private static readonly RGBAColor32 Green = new RGBAColor32(0, 255, 0, 255);
    private static readonly RGBAColor32 Blue = new RGBAColor32(0, 0, 255, 255);

    // A 45° ellipse about (32,32): semi-major 28.28 down-right, semi-minor 7.07 up-right.
    private static (float X, float Y) Centre => (32f, 32f);
    private static (float X, float Y) AxisU => (20f, 20f);
    private static (float X, float Y) AxisV => (-5f, 5f);

    private static (byte R, byte G, byte B) PixelAt(byte[] rgba, int x, int y)
    {
        var at = (y * (int)Width + x) * 4;
        return (rgba[at], rgba[at + 1], rgba[at + 2]);
    }

    private byte[]? RenderToPixels(Action<VkRenderer> draw)
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

    private static float[] Instances(params (( float X, float Y) Centre, (float X, float Y) U,
                                             (float X, float Y) V, float StrokeWidth, RGBAColor32 Color)[] items)
    {
        var buf = new float[items.Length * VkRenderer.EllipseInstanceFloats];
        for (var i = 0; i < items.Length; i++)
        {
            var (c, u, v, stroke, color) = items[i];
            VkRenderer.WriteEllipseInstance(
                buf.AsSpan(i * VkRenderer.EllipseInstanceFloats, VkRenderer.EllipseInstanceFloats),
                c, u, v, stroke, color);
        }

        return buf;
    }

    private static void ShouldMatchWithinOneLevel(byte[] actual, byte[] expected)
    {
        actual.Length.ShouldBe(expected.Length);
        var worst = 0;
        var differing = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            var diff = Math.Abs(actual[i] - expected[i]);
            if (diff == 0) continue;
            differing++;
            if (diff > worst) worst = diff;
        }

        worst.ShouldBeLessThanOrEqualTo(1, "a rounding difference in the rim padding moves an edge pixel by one level at most");
        differing.ShouldBeLessThanOrEqualTo(actual.Length / 100, "and touches only the rim");
    }

    [Fact]
    public void OneInstanceMatchesTheSingleDrawPath()
    {
        var single = RenderToPixels(r => r.FillEllipse(Centre, AxisU, AxisV, Ink));
        if (single is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var instanced = RenderToPixels(r => r.DrawEllipseInstances(
            Instances((Centre, AxisU, AxisV, 0f, Ink))));

        instanced.ShouldNotBeNull();
        ShouldMatchWithinOneLevel(instanced, single);
    }

    /// <summary>The same agreement for a stroke, which exercises the padding both paths add for it.</summary>
    [Fact]
    public void OneStrokedInstanceMatchesTheSingleDrawPath()
    {
        var single = RenderToPixels(r => r.DrawEllipse(Centre, AxisU, AxisV, Ink, strokeWidth: 3f));
        if (single is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var instanced = RenderToPixels(r => r.DrawEllipseInstances(
            Instances((Centre, AxisU, AxisV, 3f, Ink))));

        instanced.ShouldNotBeNull();
        ShouldMatchWithinOneLevel(instanced, single);
    }

    [Fact]
    public void ManyEllipsesInOneCallEachKeepTheirOwnPlaceAndColour()
    {
        var rgba = RenderToPixels(r => r.DrawEllipseInstances(Instances(
            (((float)16, (float)16), ((float)10, (float)0), ((float)0, (float)10), 0f, Red),
            (((float)48, (float)16), ((float)10, (float)0), ((float)0, (float)10), 0f, Green),
            (((float)32, (float)48), ((float)10, (float)0), ((float)0, (float)10), 0f, Blue))));

        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        PixelAt(rgba, 16, 16).ShouldBe(((byte)255, (byte)0, (byte)0), "the first instance");
        PixelAt(rgba, 48, 16).ShouldBe(((byte)0, (byte)255, (byte)0), "the second");
        PixelAt(rgba, 32, 48).ShouldBe(((byte)0, (byte)0, (byte)255), "the third");
        PixelAt(rgba, 32, 16).ShouldBe(((byte)0, (byte)0, (byte)0), "the gap between the first two");
    }

    /// <summary>
    /// The stroke is per-instance, not per-draw, which is the difference that made this a second
    /// pipeline rather than a second entry point on the first one.
    /// </summary>
    [Fact]
    public void AFillAndARingCanShareOneCall()
    {
        var rgba = RenderToPixels(r => r.DrawEllipseInstances(Instances(
            (((float)16, (float)32), ((float)12, (float)0), ((float)0, (float)12), 0f, Red),
            (((float)48, (float)32), ((float)12, (float)0), ((float)0, (float)12), 3f, Green))));

        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        PixelAt(rgba, 16, 32).ShouldBe(((byte)255, (byte)0, (byte)0), "the filled instance is solid");
        PixelAt(rgba, 48, 32).ShouldBe(((byte)0, (byte)0, (byte)0), "the stroked instance is empty at its centre");
        PixelAt(rgba, 55, 32).ShouldBe(((byte)0, (byte)0, (byte)0), "and empty 7 px out, inside a 3 px stroke on a 12 px radius");
        PixelAt(rgba, 59, 32).ShouldBe(((byte)0, (byte)255, (byte)0), "and drawn on its boundary");
    }

    [Fact]
    public void ARaggedInstanceBufferIsRejectedRatherThanDrawnCrooked()
    {
        if (gpu.Context is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        Should.Throw<ArgumentException>(() => RenderToPixels(r =>
            r.DrawEllipseInstances(new float[VkRenderer.EllipseInstanceFloats + 1])));
    }

    [Fact]
    public void AnEmptyInstanceBufferDrawsNothingAndDoesNotThrow()
    {
        var rgba = RenderToPixels(r => r.DrawEllipseInstances(ReadOnlySpan<float>.Empty));
        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        PixelAt(rgba, 32, 32).ShouldBe(((byte)0, (byte)0, (byte)0));
    }
}
