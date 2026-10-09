using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Glyphs laid out once by <see cref="VkRenderer.LayoutSdfGlyph"/> in a caller's own space and drawn by
/// <see cref="VkRenderer.DrawPersistentSdfGlyphs"/> at some origin and scale must draw what the batch path
/// (<see cref="VkRenderer.AddBatchedSdfGlyphAtBaselineByGid"/>) draws for the same glyphs at the same place
/// and on-screen size: the same quad, the same texels, and the same coverage path and band, which the
/// instanced shader derives per glyph from the size it carries where a batch pushes them per draw. The
/// sizes straddle the one-sample threshold (64 px a em), the layout scale is not 1 (so the glyph is laid
/// out at one size and drawn at another), and one glyph is rotated and compressed.
///
/// Skips when no Vulkan ICD is available on the host (same policy as <see cref="BatchedGlyphByGidTests"/>).
/// </summary>
[Collection("OffscreenGpu")]
public sealed class PersistentSdfGlyphTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 256;
    private const uint Height = 128;

    private static string FontPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    [Theory]
    [InlineData(3f, 2f, 0f, 1f)]
    [InlineData(12f, 2f, 0f, 1f)]
    [InlineData(44f, 0.5f, 0f, 1f)]
    [InlineData(90f, 2f, 0f, 1f)]
    [InlineData(30f, 2f, 0.3f, 0.8f)]
    public void PersistentGlyphs_DrawWhatTheBatchDraws(float sizePx, float scale, float rotation, float xScale)
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }
        ctx.ResizeOffscreen(Width, Height);

        using var renderer = new VkRenderer(ctx, Width, Height);
        var rasterizer = new ManagedFontRasterizer();
        var gids = new List<uint>();
        foreach (var ch in "Hamb")
            gids.Add(rasterizer.ResolveGlyphIdentity(FontPath, new Rune(ch), -1, GlyphMapHint.Auto).Gid);
        renderer.OnPreFlush = () =>
        {
            foreach (var gid in gids) renderer.PreWarmSdfGlyphByGid(FontPath, sizePx, gid);
        };

        // Screen placement, and the same placement in a space `scale` times smaller, drawn at `scale`.
        const float originX = 10f, originY = 6f;
        var baselines = new (float X, float Y)[gids.Count];
        for (var i = 0; i < gids.Count; i++)
            baselines[i] = (20f + i * sizePx * 0.7f, Height * 0.7f);

        // Warm the atlas through a couple of batch frames before laying anything out.
        for (var f = 0; f < 3; f++)
            DrawBatch(renderer, ctx, gids, baselines, sizePx, rotation, xScale);
        var batch = DrawBatch(renderer, ctx, gids, baselines, sizePx, rotation, xScale);

        var instances = new float[gids.Count * VkRenderer.SdfInstanceFloats];
        var page = -1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var laidOut = 0;
        while (laidOut < gids.Count && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            laidOut = 0;
            for (var i = 0; i < gids.Count; i++)
            {
                var laid = renderer.LayoutSdfGlyph(FontPath, gids[i], null,
                    (baselines[i].X - originX) / scale, (baselines[i].Y - originY) / scale, sizePx / scale,
                    rotation, xScale, largeTier: false,
                    instances.AsSpan(i * VkRenderer.SdfInstanceFloats, VkRenderer.SdfInstanceFloats),
                    out var glyphPage, out _);
                if (laid != VkRenderer.SdfGlyphLayout.Quad) break;
                page = glyphPage;
                laidOut++;
            }
            if (laidOut < gids.Count)
            {
                DrawBatch(renderer, ctx, gids, baselines, sizePx, rotation, xScale);
                Thread.Sleep(10);
            }
        }
        laidOut.ShouldBe(gids.Count, "every glyph should lay out once the atlas holds it");

        var (buffer, memory) = renderer.CreatePersistentVertexBuffer(instances);
        try
        {
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            renderer.DrawPersistentSdfGlyphs(buffer, 0, (uint)gids.Count, page, largeTier: false,
                new RGBAColor32(255, 255, 255, 255), originX, originY, scale);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            var persistent = ctx.ReadbackOffscreenRgba();

            var lit = 0;
            var differing = 0;
            var maxDiff = 0;
            for (var i = 0; i < (int)(Width * Height); i++)
            {
                if (batch[i * 4] > 24) lit++;
                var d = Math.Abs(batch[i * 4] - persistent[i * 4]);
                if (d > 0) differing++;
                maxDiff = Math.Max(maxDiff, d);
            }
            lit.ShouldBeGreaterThan(0, "the batch must actually draw the glyphs, or the comparison proves nothing");
            // The two place the same quad by different arithmetic (the batch multiplies by the zoom on the
            // CPU, the instance in the vertex shader), so an edge can land a hair apart. Anything beyond
            // that, a wrong band or a wrong size, moves far more.
            maxDiff.ShouldBeLessThanOrEqualTo(8, $"{differing} texels differ");
            differing.ShouldBeLessThan(Math.Max(8, lit / 50), $"of {lit} lit texels");
        }
        finally
        {
            renderer.DestroyBuffer(buffer, memory);
        }
    }

    [Fact]
    public void LayoutSdfGlyph_SaysPendingUntilTheGlyphLands_AndBlankForASpace()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }
        ctx.ResizeOffscreen(Width, Height);

        using var renderer = new VkRenderer(ctx, Width, Height);
        var rasterizer = new ManagedFontRasterizer();
        var gidQ = rasterizer.ResolveGlyphIdentity(FontPath, new Rune('Q'), -1, GlyphMapHint.Auto).Gid;
        var gidSpace = rasterizer.ResolveGlyphIdentity(FontPath, new Rune(' '), -1, GlyphMapHint.Auto).Gid;
        var instance = new float[VkRenderer.SdfInstanceFloats];

        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        renderer.LayoutSdfGlyph(FontPath, gidQ, null, 10f, 40f, 20f, 0f, 1f, largeTier: false, instance, out _, out _)
            .ShouldBe(VkRenderer.SdfGlyphLayout.Pending, "a glyph never asked for is queued, not drawable");
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var laid = VkRenderer.SdfGlyphLayout.Pending;
        long stamp = -1;
        var page = -1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (laid == VkRenderer.SdfGlyphLayout.Pending && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            laid = renderer.LayoutSdfGlyph(FontPath, gidQ, null, 10f, 40f, 20f, 0f, 1f, largeTier: false,
                instance, out page, out stamp);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            if (laid == VkRenderer.SdfGlyphLayout.Pending) Thread.Sleep(10);
        }
        laid.ShouldBe(VkRenderer.SdfGlyphLayout.Quad);
        stamp.ShouldBe(renderer.SdfPageStamp(page, largeTier: false));
        instance[10].ShouldBe(20f, "the instance carries the size it was laid out at");

        laid = VkRenderer.SdfGlyphLayout.Pending;
        clock.Restart();
        while (laid == VkRenderer.SdfGlyphLayout.Pending && clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            laid = renderer.LayoutSdfGlyph(FontPath, gidSpace, null, 10f, 40f, 20f, 0f, 1f, largeTier: false,
                instance, out _, out _);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            if (laid == VkRenderer.SdfGlyphLayout.Pending) Thread.Sleep(10);
        }
        laid.ShouldBe(VkRenderer.SdfGlyphLayout.Blank, "a space has nothing to draw, and that is final");
    }

    private static byte[] DrawBatch(VkRenderer renderer, VulkanContext ctx, List<uint> gids,
        (float X, float Y)[] baselines, float sizePx, float rotation, float xScale)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        renderer.BeginSdfGlyphBatch(new RGBAColor32(255, 255, 255, 255), sizePx);
        for (var i = 0; i < gids.Count; i++)
            renderer.AddBatchedSdfGlyphAtBaselineByGid(FontPath, gids[i], null, baselines[i].X, baselines[i].Y,
                rotation, xScale: xScale);
        renderer.EndGlyphBatch();
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return ctx.ReadbackOffscreenRgba();
    }
}
