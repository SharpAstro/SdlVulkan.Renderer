using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Render coverage for <see cref="VkRenderer.DrawPersistentStrokes"/>'s <c>roundCaps</c>: the stroke shader
/// builds a half-disc fan at each end of every segment from the vertex index alone. One 16-pixel-wide
/// segment from (20,32) to (44,32) is checked at three points that tell a round cap from no cap and from a
/// square one: beside the end inside the half-disc, in the corner a square cap would fill, and on the line.
/// Tests skip when Vulkan isn't loadable on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class StrokeRoundCapRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 64;
    private const uint Height = 64;
    private const float HalfWidth = 8f;

    private static readonly RGBAColor32 Backdrop = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 Ink = new RGBAColor32(255, 255, 255, 255);

    private byte[]? Render(float[] segments, bool roundCaps)
    {
        if (gpu.Context is not { } ctx) return null;

        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Width, Height);
        var (buffer, memory) = renderer.CreatePersistentVertexBuffer(segments);
        try
        {
            renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
            renderer.DrawPersistentStrokes(buffer, 0, (uint)(segments.Length / 4), Ink, 0f, 0f, 1f, HalfWidth, roundCaps);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            return ctx.ReadbackOffscreenRgba();
        }
        finally
        {
            renderer.DestroyBuffer(buffer, memory);
        }
    }

    private static byte Red(byte[] rgba, int x, int y) => rgba[(y * (int)Width + x) * 4];

    [Fact]
    public void RoundCapsReachPastEachEndInAHalfDisc()
    {
        var capped = Render([20f, 32f, 44f, 32f], roundCaps: true);
        if (capped is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        Red(capped, 32, 32).ShouldBe((byte)255, "the line itself");
        Red(capped, 14, 32).ShouldBe((byte)255, "6px past the start, inside the cap's half-disc");
        Red(capped, 50, 32).ShouldBe((byte)255, "6px past the end, inside the cap's half-disc");
        Red(capped, 13, 25).ShouldBe((byte)0, "the corner a square cap would fill is outside the half-disc");
    }

    [Fact]
    public void WithoutRoundCapsASegmentIsItsQuadAlone()
    {
        var bare = Render([20f, 32f, 44f, 32f], roundCaps: false);
        if (bare is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        Red(bare, 32, 32).ShouldBe((byte)255, "the line itself");
        Red(bare, 14, 32).ShouldBe((byte)0, "nothing past the start");
        Red(bare, 50, 32).ShouldBe((byte)0, "nothing past the end");
    }

    /// <summary>A segment of no length has its two caps facing opposite ways: a disc of the line width.</summary>
    [Fact]
    public void AZeroLengthSegmentIsADisc()
    {
        var dot = Render([32f, 32f, 32f, 32f], roundCaps: true);
        if (dot is null)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        Red(dot, 32, 32).ShouldBe((byte)255, "the centre");
        Red(dot, 37, 32).ShouldBe((byte)255, "inside the disc to the right");
        Red(dot, 27, 32).ShouldBe((byte)255, "inside the disc to the left");
        Red(dot, 32, 37).ShouldBe((byte)255, "inside the disc below");
        Red(dot, 38, 38).ShouldBe((byte)0, "outside it at the corner of its bounding square");
    }
}
