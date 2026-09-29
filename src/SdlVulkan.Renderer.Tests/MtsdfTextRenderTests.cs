using System;
using System.IO;
using System.Text;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// End-to-end GPU coverage for the MTSDF glyph atlas: rasterize glyphs into the
/// RGBA MTSDF atlas, upload them through the (now 4-byte-per-texel) staging path,
/// draw them through the SdfPipeline (which reconstructs coverage from
/// median(r,g,b)), read the framebuffer back, and assert the rendered text is a
/// coherent glyph — a solid interior plus antialiased edges, covering a plausible
/// fraction of the frame. A byte-stride bug in the atlas upload, a wrong image
/// format, or a broken median shader would show up here as empty, garbled, or
/// full-frame output.
///
/// Skips when no Vulkan ICD is available on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class MtsdfTextRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 128;
    private const uint Height = 64;

    private static string FontPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    [Fact]
    public void MtsdfText_RendersCoherentCoverage()
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
            var font = FontPath;
            const float size = 36f;

            // Warm the glyphs synchronously in OnPreFlush — this runs before the atlas Flush inside
            // BeginOffscreenFrame, so the freshly rasterized MTSDF cells are uploaded this same frame.
            // (The draw path itself never rasterizes on the render thread; it would skip an unwarmed glyph.)
            renderer.OnPreFlush = () =>
            {
                renderer.PreWarmSdfGlyph(font, size, new Rune('A'));
                renderer.PreWarmSdfGlyph(font, size, new Rune('g'));
            };

            var black = new RGBAColor32(0, 0, 0, 255);
            renderer.BeginOffscreenFrame(black).ShouldBeTrue();

            var white = new RGBAColor32(255, 255, 255, 255);
            renderer.BeginSdfGlyphBatch(white, size);
            renderer.AddBatchedSdfGlyphAtBaseline(font, new Rune('A'), -1, baselineX: 12f, baselineY: 44f);
            renderer.AddBatchedSdfGlyphAtBaseline(font, new Rune('g'), -1, baselineX: 44f, baselineY: 44f);
            renderer.EndGlyphBatch();

            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();

            var rgba = ctx.ReadbackOffscreenRgba();
            rgba.Length.ShouldBe((int)(Width * Height * 4));

            var dump = Environment.GetEnvironmentVariable("MTSDF_DUMP");
            if (!string.IsNullOrEmpty(dump))
                File.WriteAllBytes(dump, rgba);

            // White text on black: coverage shows up in the red channel (all channels equal here).
            var pixels = (int)(Width * Height);
            var lit = 0;        // any coverage at all
            var solid = 0;      // near-fully-covered interior texels
            var partial = 0;    // antialiased edge texels (partial coverage)
            for (var i = 0; i < pixels; i++)
            {
                int r = rgba[i * 4];
                if (r > 24) lit++;
                if (r > 200) solid++;
                if (r is > 24 and < 200) partial++;
            }

            var litFraction = lit / (float)pixels;

            // Text actually rendered (not empty), but didn't flood the frame (not garbage / wrong format).
            litFraction.ShouldBeInRange(0.02f, 0.6f);
            // A real glyph has a solid interior (median reconstructs ~1.0 well inside the ink)...
            solid.ShouldBeGreaterThan(20, "expected a solid glyph interior (near-white texels)");
            // ...and antialiased edges (the smoothstep band produces intermediate coverage).
            partial.ShouldBeGreaterThan(20, "expected antialiased edge texels (partial coverage)");
        }
    }

    /// <summary>
    /// A stroke thinner than a pixel keeps its ink wherever it lands. The same hyphens are drawn at
    /// eight vertical sub-pixel phases, and the ink each phase puts on screen (summed coverage, i.e.
    /// area in pixels) must stay nearly the same: area coverage does not depend on where a shape
    /// sits against the pixel grid. Sampling the distance field once at the pixel centre does: two
    /// centres can straddle a thin stroke and both read it as outside, which drew a Times 'a' at
    /// reading size without the hairline top of its bowl. At 9 px/em DejaVu's hyphen is about
    /// 0.8 px thick, which is that case: one sample a pixel scores 0.38 here, two score 0.95.
    /// <para>9 px/em, not smaller, because below it the hyphen is mostly its two ends, and the
    /// two-sample pair (on a diagonal, so its x and y are coupled) makes an end pixel's coverage
    /// depend on the vertical phase: at 7 px/em it scores 0.83 where four samples score 0.91 and one
    /// sample 0.03. Two samples cost half what four do, and text that small is below reading size.</para>
    /// </summary>
    [Fact]
    public void MtsdfText_ThinStrokeKeepsItsInkAtEverySubPixelPhase()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        const uint w = 256, h = 48;
        const int phases = 8, column = 32, perColumn = 3;
        const float size = 9f;
        ctx.ResizeOffscreen(w, h);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, w, h);
        var font = FontPath;
        renderer.OnPreFlush = () => renderer.PreWarmSdfGlyph(font, size, new Rune('-'));

        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        renderer.BeginSdfGlyphBatch(new RGBAColor32(255, 255, 255, 255), size);
        for (var p = 0; p < phases; p++)
        for (var i = 0; i < perColumn; i++)
            renderer.AddBatchedSdfGlyphAtBaseline(font, new Rune('-'), -1,
                baselineX: p * column + 4f + i * 8f, baselineY: 24f + p / (float)phases);
        renderer.EndGlyphBatch();
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        var ink = new double[phases];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            ink[x / column] += rgba[(y * (int)w + x) * 4] / 255.0;

        var min = double.MaxValue;
        var max = 0.0;
        foreach (var a in ink) { min = Math.Min(min, a); max = Math.Max(max, a); }
        var inkPerPhase = string.Join(", ", Array.ConvertAll(ink, a => a.ToString("F2")));

        // Where each phase's ink sits, row by row, so a failure says whether the stroke itself or
        // something else in its column carries the difference.
        var profile = new StringBuilder();
        for (var p = 0; p < phases; p++)
        {
            profile.Append($"\n  phase {p}:");
            for (var y = 0; y < h; y++)
            {
                var row = 0.0;
                for (var x = p * column; x < (p + 1) * column; x++) row += rgba[(y * (int)w + x) * 4] / 255.0;
                if (row > 0.005) profile.Append($" y{y}={row:F2}");
            }
        }
        inkPerPhase += profile.ToString();

        max.ShouldBeGreaterThan(1.0, $"the hyphens drew nothing: ink per phase {inkPerPhase}");
        (min / max).ShouldBeGreaterThan(0.85,
            $"a thin stroke's ink depends on its sub-pixel phase: ink per phase {inkPerPhase}");
    }
}
