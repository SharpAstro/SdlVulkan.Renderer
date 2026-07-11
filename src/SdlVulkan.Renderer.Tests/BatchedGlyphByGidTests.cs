using System;
using System.Text;
using System.Threading;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The GID-direct batched draw endpoints (<see cref="VkRenderer.AddBatchedSdfGlyphAtBaselineByGid"/>
/// and <see cref="VkRenderer.AddBatchedGlyphAtBaselineByGid"/>) must produce pixel-identical output
/// to the rune-resolved overloads for the same glyph: both paths resolve to the same atlas key, so
/// the quad math and sampled texels must match exactly. A divergence means the ByGid path skipped
/// or re-derived some of the baseline→ink bearing transform. Also covers the ByGid batch prewarm
/// (<see cref="VkRenderer.PreWarmSdfGlyphBatchByGid"/>), whose background rasterization must make a
/// cold glyph drawable within a bounded number of frames.
///
/// Skips when no Vulkan ICD is available on the host (same policy as <see cref="ShapedTextGidTests"/>).
/// </summary>
public sealed unsafe class BatchedGlyphByGidTests
{
    private const uint Width = 128;
    private const uint Height = 64;

    private static string FontPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    [Fact]
    public void BatchedSdfGlyphByGid_MatchesRunePath()
    {
        if (!TryCreateOffscreenContext(out _, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var renderer = new VkRenderer(ctx!, Width, Height);
            const float size = 44f;
            var gidM = new ManagedFontRasterizer()
                .ResolveGlyphIdentity(FontPath, new Rune('M'), -1, GlyphMapHint.Auto).Gid;

            // Warm through BOTH entry points; they resolve to the same atlas key, so the second
            // call is a cache hit — but a key mismatch would rasterize twice and the draws below
            // would still pass, so the real assertion is the pixel equality, not the warm.
            renderer.OnPreFlush = () =>
            {
                renderer.PreWarmSdfGlyph(FontPath, size, new Rune('M'));
                renderer.PreWarmSdfGlyphByGid(FontPath, size, gidM);
            };

            var litRune = DrawBatchedAndCountLit(renderer, ctx!,
                r => r.AddBatchedSdfGlyphAtBaseline(FontPath, new Rune('M'), -1, 20f, 48f), sdf: true, size);
            var litGid = DrawBatchedAndCountLit(renderer, ctx!,
                r => r.AddBatchedSdfGlyphAtBaselineByGid(FontPath, gidM, null, 20f, 48f), sdf: true, size);

            litRune.ShouldBeGreaterThan(0, "the rune-path glyph must actually render (else the test proves nothing)");
            litGid.ShouldBe(litRune);
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    [Fact]
    public void BatchedBitmapGlyphByGid_MatchesRunePath()
    {
        if (!TryCreateOffscreenContext(out _, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var renderer = new VkRenderer(ctx!, Width, Height);
            const float size = 20f; // small text is the bitmap-atlas regime
            var gidM = new ManagedFontRasterizer()
                .ResolveGlyphIdentity(FontPath, new Rune('M'), -1, GlyphMapHint.Auto).Gid;

            renderer.OnPreFlush = () =>
            {
                renderer.PreWarmGlyph(FontPath, size, new Rune('M'));
                renderer.PreWarmGlyphByGid(FontPath, size, gidM);
            };

            var litRune = DrawBatchedAndCountLit(renderer, ctx!,
                r => r.AddBatchedGlyphAtBaseline(FontPath, size, new Rune('M'), -1, 20f, 44f), sdf: false, size);
            var litGid = DrawBatchedAndCountLit(renderer, ctx!,
                r => r.AddBatchedGlyphAtBaselineByGid(FontPath, size, gidM, null, 20f, 44f), sdf: false, size);

            litRune.ShouldBeGreaterThan(0, "the rune-path glyph must actually render (else the test proves nothing)");
            litGid.ShouldBe(litRune);
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    [Fact]
    public void PreWarmSdfGlyphBatchByGid_MakesColdGlyphDrawable()
    {
        if (!TryCreateOffscreenContext(out _, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var renderer = new VkRenderer(ctx!, Width, Height);
            const float size = 44f;
            var rasterizer = new ManagedFontRasterizer();
            var gidW = rasterizer.ResolveGlyphIdentity(FontPath, new Rune('W'), -1, GlyphMapHint.Auto).Gid;

            // No OnPreFlush warm — the batch prewarm alone must get the glyph into the atlas.
            // Rasterization runs on the thread pool and inserts bounded-per-frame, so pump frames
            // until ink appears. Sleep between frames like a real redraw-while-dirty consumer:
            // on a software ICD a 128×64 frame takes well under a millisecond, and a no-delay
            // loop can burn every retry before the background rasterize task ever lands
            // (observed on the ubuntu-latest lavapipe lane; a real GPU's frame pacing hid it).
            renderer.PreWarmSdfGlyphBatchByGid(new[] { (FontPath, gidW, (string?)null) });

            var lit = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (lit == 0 && clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                lit = DrawBatchedAndCountLit(renderer, ctx!,
                    r => r.AddBatchedSdfGlyphAtBaselineByGid(FontPath, gidW, null, 20f, 48f), sdf: true, size);
                if (lit == 0) Thread.Sleep(10);
            }

            lit.ShouldBeGreaterThan(0, "batch-prewarmed glyph never became drawable");
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    // Runs one offscreen frame that draws via the supplied batched-glyph call between
    // Begin/EndGlyphBatch, reads the framebuffer back, and counts lit texels.
    private static int DrawBatchedAndCountLit(VkRenderer renderer, VulkanContext ctx,
        Action<VkRenderer> addGlyphs, bool sdf, float size)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var white = new RGBAColor32(255, 255, 255, 255);
        if (sdf) renderer.BeginSdfGlyphBatch(white, size);
        else renderer.BeginGlyphBatch(white);
        addGlyphs(renderer);
        renderer.EndGlyphBatch();
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        var lit = 0;
        var pixels = (int)(Width * Height);
        for (var i = 0; i < pixels; i++)
            if (rgba[i * 4] > 24) lit++;
        return lit;
    }

    /// <summary>
    /// Best-effort offscreen Vulkan setup; returns false when the host has no Vulkan ICD. Mirrors
    /// the helper in <see cref="ShapedTextGidTests"/> / <see cref="MtsdfTextRenderTests"/>.
    /// </summary>
    private static bool TryCreateOffscreenContext(out VkInstance instance, out VulkanContext? ctx)
    {
        instance = default;
        ctx = null;
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out instance).CheckResult();
            ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
