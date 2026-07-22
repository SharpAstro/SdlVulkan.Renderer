using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Guards the batched <see cref="VkRenderer.DrawPolyline"/> / <see cref="VkRenderer.DrawPolylineDashed"/>
/// overrides. They collapse the base class's one-<c>DrawLine</c>-per-segment loop (N FlatPipeline draws)
/// into a single draw, but each segment's rotated-quad geometry is unchanged — so the offscreen
/// framebuffer must match the per-segment path byte-for-byte. Skips when no Vulkan ICD is available
/// (mirrors <see cref="MtsdfTextRenderTests"/>); runs on lavapipe in CI.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class DrawPolylineBatchingTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 96;
    private const uint Height = 96;

    private static readonly (float X, float Y)[] s_pts =
        [(8, 8), (80, 12), (40, 60), (88, 80), (12, 84)];

    [Fact]
    public void DrawPolyline_MatchesPerSegmentDrawLine()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        {
            using var renderer = new VkRenderer(ctx, Width, Height);
            var bg = new RGBAColor32(0, 0, 0, 255);
            var white = new RGBAColor32(255, 255, 255, 255);

            var batched = RenderToRgba(renderer, ctx, bg, r => r.DrawPolyline(s_pts, white, thickness: 3));
            var perSegment = RenderToRgba(renderer, ctx, bg, r =>
            {
                for (var i = 1; i < s_pts.Length; i++)
                    r.DrawLine(s_pts[i - 1].X, s_pts[i - 1].Y, s_pts[i].X, s_pts[i].Y, white, thickness: 3);
            });

            // The batched run collapses 4 draws into 1, but the pixels must be identical.
            batched.ShouldBe(perSegment);

            // Sanity: something was actually drawn (guards against a silently-empty frame passing the equality).
            LitPixels(batched).ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void DrawPolylineDashed_DrawsFewerPixelsThanSolid_ButNonEmpty()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        {
            using var renderer = new VkRenderer(ctx, Width, Height);
            var bg = new RGBAColor32(0, 0, 0, 255);
            var white = new RGBAColor32(255, 255, 255, 255);

            var solid = LitPixels(RenderToRgba(renderer, ctx, bg, r => r.DrawPolyline(s_pts, white, thickness: 3)));
            var dashed = LitPixels(RenderToRgba(renderer, ctx, bg,
                r => r.DrawPolylineDashed(s_pts, white, dashLength: 8f, gapLength: 8f, thickness: 3)));

            dashed.ShouldBeGreaterThan(0);      // dashes rendered (not a no-op)
            dashed.ShouldBeLessThan(solid);     // gaps really left holes
        }
    }

    private static byte[] RenderToRgba(VkRenderer renderer, VulkanContext ctx, RGBAColor32 bg, Action<VkRenderer> draw)
    {
        renderer.BeginOffscreenFrame(bg).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return ctx.ReadbackOffscreenRgba();
    }

    private static int LitPixels(byte[] rgba)
    {
        var lit = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] > 24) lit++;
        return lit;
    }

}
