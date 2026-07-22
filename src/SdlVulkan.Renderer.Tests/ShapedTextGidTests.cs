using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// H5c: the renderer's shaped-text path must fetch atlas glyphs by the substituted glyph id
/// (<see cref="ShapedGlyph.Glyph"/>) that a real <see cref="ITextShaper"/> produces — GSUB
/// ligatures, Arabic joined forms, contextual alternates — and NOT by the source codepoint. These
/// tests plug in a stub shaper that rewrites every glyph id to a fixed target ('M'); if the renderer
/// honors the substituted id, measuring/drawing an 'i' yields the 'M' glyph. A regression to
/// source-codepoint keying would render/measure the source 'i' instead and fail loudly.
///
/// Uses only the DIR.Lib core shaping seam (ITextShaper / ShapedGlyph / GlyphIdentity), so there is
/// no dependency on the DIR.Lib.Shaping satellite. Skips when no Vulkan ICD is available on the host.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class ShapedTextGidTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 128;
    private const uint Height = 64;

    private static string FontPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    /// <summary>
    /// A stub shaper: one glyph per rune (like <see cref="AdvanceShaper"/>) but every glyph identity
    /// is rewritten to a fixed target id. The source codepoints are preserved (so the renderer's
    /// colour-glyph routing, which keys off <see cref="ShapedGlyph.Source"/>, is unaffected) while
    /// the authoritative substituted id points elsewhere — the exact shape of a GSUB substitution.
    /// </summary>
    private sealed class FixedGidShaper(uint gid) : ITextShaper
    {
        public void Shape(ReadOnlySpan<char> text, string fontPath, float fontSize,
            ManagedFontRasterizer rasterizer, List<ShapedGlyph> output)
        {
            output.Clear();
            var cluster = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                output.Add(new ShapedGlyph(rune, new GlyphIdentity(gid, null), cluster, 0f, 0f, 0f));
                cluster += rune.Utf16SequenceLength;
            }
        }
    }

    [Fact]
    public void MeasureText_UsesShapedGlyphAdvance_NotSourceCodepoint()
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
            const float size = 40f;

            // 'M' is a wide glyph, 'i' a narrow one. Resolve M's glyph id through the same font file
            // (the id is a property of the font's cmap, so a fresh rasterizer yields the same value
            // the renderer's atlas will).
            var gidM = new ManagedFontRasterizer()
                .ResolveGlyphIdentity(FontPath, new Rune('M'), -1, GlyphMapHint.Auto).Gid;

            // Warm both source glyphs and the substituted target so no measure rasterizes mid-call.
            renderer.OnPreFlush = () =>
            {
                renderer.PreWarmSdfGlyph(FontPath, size, new Rune('i'));
                renderer.PreWarmSdfGlyph(FontPath, size, new Rune('M'));
                renderer.PreWarmSdfGlyphByGid(FontPath, size, gidM);
            };
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();

            // Baseline (default per-rune shaper): the font distinguishes the two widths.
            var wI = renderer.MeasureText("i", FontPath, size).Width;
            var wM = renderer.MeasureText("M", FontPath, size).Width;
            wM.ShouldBeGreaterThan(wI);

            // Rewrite every glyph id to 'M'. Measuring "i" now reports M's advance — the renderer
            // measured the SUBSTITUTED glyph, not the source codepoint 'i'.
            renderer.TextShaper = new FixedGidShaper(gidM);
            var wShaped = renderer.MeasureText("i", FontPath, size).Width;

            wShaped.ShouldBe(wM, tolerance: 0.5f);
            wShaped.ShouldBeGreaterThan(wI); // definitely not measured as the source 'i'

            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }
    }

    [Fact]
    public void DrawText_RendersShapedGlyphId_NotSourceCodepoint()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        // All three renders below cycle frames on that one context (creating and destroying
        // offscreen instances back-to-back segfaults software ICDs like Mesa lavapipe).
        {
            using var renderer = new VkRenderer(ctx, Width, Height);
            const float size = 44f;
            var gidM = new ManagedFontRasterizer()
                .ResolveGlyphIdentity(FontPath, new Rune('M'), -1, GlyphMapHint.Auto).Gid;

            // Warm all three glyphs once; they stay cached across the frames below.
            renderer.OnPreFlush = () =>
            {
                renderer.PreWarmSdfGlyph(FontPath, size, new Rune('i'));
                renderer.PreWarmSdfGlyph(FontPath, size, new Rune('M'));
                renderer.PreWarmSdfGlyphByGid(FontPath, size, gidM);
            };

            // 'i' shaped so every glyph id becomes 'M' → the draw path must render the wide M.
            renderer.TextShaper = new FixedGidShaper(gidM);
            var litShapedI = DrawAndCountLit(renderer, ctx, "i", size);

            // Baselines with the default per-rune shaper.
            renderer.TextShaper = AdvanceShaper.Default;
            var litM = DrawAndCountLit(renderer, ctx, "M", size);
            var litI = DrawAndCountLit(renderer, ctx, "i", size);

            litI.ShouldBeGreaterThan(0, "the source glyph 'i' must actually render (else the test proves nothing)");
            // GID-direct: shaping 'i' to the 'M' id renders the wide M — far more coverage than 'i'...
            litShapedI.ShouldBeGreaterThan(litI * 2);
            // ...and matches drawing 'M' directly (same glyph, same centred position).
            litShapedI.ShouldBeInRange((int)(litM * 0.85f), (int)(litM * 1.15f));
        }
    }

    // Draws <paramref name="text"/> centred in one offscreen frame on the shared context, reads the
    // framebuffer back, and counts lit (inked) texels. The renderer's TextShaper / OnPreFlush are
    // configured by the caller before each call.
    private static int DrawAndCountLit(VkRenderer renderer, VulkanContext ctx, string text, float size)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var white = new RGBAColor32(255, 255, 255, 255);
        var layout = new RectInt(new PointInt((int)Width, (int)Height), new PointInt(0, 0));
        renderer.DrawText(text, FontPath, size, white, layout, TextAlign.Center, TextAlign.Center);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        var lit = 0;
        var pixels = (int)(Width * Height);
        for (var i = 0; i < pixels; i++)
            if (rgba[i * 4] > 24) lit++;
        return lit;
    }

}
