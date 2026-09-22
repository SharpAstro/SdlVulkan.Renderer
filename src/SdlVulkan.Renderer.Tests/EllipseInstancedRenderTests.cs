using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Render coverage for <see cref="VkRenderer.DrawEllipseInstances"/>, the one-draw bulk form of the
/// ellipse primitive: each instance carries its own centre, semi-axis vectors, hole and colour.
/// <para>
/// The load-bearing test is <see cref="OneInstanceMatchesTheSingleDrawPathExactly"/>. Two pipelines
/// now draw the same shape from the same six corners and the same unit-disc predicate — one with the
/// corners computed on the CPU and the colour in the push block, one with both derived in the vertex
/// shader — and nothing but a whole-framebuffer comparison would notice them drifting apart.
/// </para>
/// <para>
/// Its axis vectors are deliberately exact in binary — (20,20) and (-5,5) about (32,32), so every
/// corner lands on an integer. The CPU computes <c>centre - U - V</c> and the shader computes
/// <c>centre + (-1)U + (-1)V</c>; with representable inputs both are exact, so byte equality is a
/// fair demand rather than a flake waiting for a fused multiply-add to round differently.
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

    // A 45° ellipse about (32,32): semi-major 28.28 down-right, semi-minor 7.07 up-right. Every
    // corner is an integer, which is what makes the byte-equality test above legitimate.
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
                                             (float X, float Y) V, float Inner, RGBAColor32 Color)[] items)
    {
        var buf = new float[items.Length * VkRenderer.EllipseInstanceFloats];
        for (var i = 0; i < items.Length; i++)
        {
            var (c, u, v, inner, color) = items[i];
            VkRenderer.WriteEllipseInstance(
                buf.AsSpan(i * VkRenderer.EllipseInstanceFloats, VkRenderer.EllipseInstanceFloats),
                c, u, v, inner, color);
        }

        return buf;
    }

    [Fact]
    public void OneInstanceMatchesTheSingleDrawPathExactly()
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
        instanced.ShouldBe(single);
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
    /// The hole is per-instance, not per-draw, which is the difference that made this a second
    /// pipeline rather than a second entry point on the first one.
    /// </summary>
    [Fact]
    public void AFillAndARingCanShareOneCall()
    {
        var rgba = RenderToPixels(r => r.DrawEllipseInstances(Instances(
            (((float)16, (float)32), ((float)12, (float)0), ((float)0, (float)12), 0f, Red),
            (((float)48, (float)32), ((float)12, (float)0), ((float)0, (float)12), 0.6f, Green))));

        if (rgba is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        PixelAt(rgba, 16, 32).ShouldBe(((byte)255, (byte)0, (byte)0), "the filled instance has no hole");
        PixelAt(rgba, 48, 32).ShouldBe(((byte)0, (byte)0, (byte)0), "the ringed instance is hollow at its centre");
        PixelAt(rgba, 58, 32).ShouldBe(((byte)0, (byte)255, (byte)0), "and drawn between its hole and its rim");
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
