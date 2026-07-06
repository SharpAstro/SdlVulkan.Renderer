using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

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
public sealed unsafe class ShapedTextGidTests
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
        if (!TryCreateOffscreenContext(out _, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var renderer = new VkRenderer(ctx!, Width, Height);
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
            ctx!.WaitOffscreenFrameComplete();
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    [Fact]
    public void DrawText_RendersShapedGlyphId_NotSourceCodepoint()
    {
        // Probe once; if there's no ICD, skip (the sub-renders below all assume one is present).
        if (!TryCreateOffscreenContext(out _, out var probe))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }
        probe?.Dispose();

        const float size = 44f;
        var gidM = new ManagedFontRasterizer()
            .ResolveGlyphIdentity(FontPath, new Rune('M'), -1, GlyphMapHint.Auto).Gid;

        // 'i' shaped so every glyph id becomes 'M' → the draw path must render the wide M.
        var litShapedI = RenderLitPixels("i", size, r =>
        {
            r.TextShaper = new FixedGidShaper(gidM);
            r.OnPreFlush = () => r.PreWarmSdfGlyphByGid(FontPath, size, gidM);
        });
        // Baselines with the default per-rune shaper.
        var litM = RenderLitPixels("M", size, r =>
        {
            r.OnPreFlush = () => r.PreWarmSdfGlyph(FontPath, size, new Rune('M'));
        });
        var litI = RenderLitPixels("i", size, r =>
        {
            r.OnPreFlush = () => r.PreWarmSdfGlyph(FontPath, size, new Rune('i'));
        });

        litI.ShouldBeGreaterThan(0, "the source glyph 'i' must actually render (else the test proves nothing)");
        // GID-direct: shaping 'i' to the 'M' id renders the wide M — far more coverage than 'i'...
        litShapedI.ShouldBeGreaterThan(litI * 2);
        // ...and matches drawing 'M' directly (same glyph, same centred position).
        litShapedI.ShouldBeInRange((int)(litM * 0.85f), (int)(litM * 1.15f));
    }

    // Runs one offscreen frame: applies <paramref name="configure"/> to the renderer, draws
    // <paramref name="text"/> centred, reads the framebuffer back, and counts lit (inked) texels.
    // Assumes an ICD is present (callers probe + skip first).
    private static int RenderLitPixels(string text, float size, Action<VkRenderer> configure)
    {
        TryCreateOffscreenContext(out _, out var ctx).ShouldBeTrue();
        try
        {
            using var renderer = new VkRenderer(ctx!, Width, Height);
            configure(renderer);

            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            var white = new RGBAColor32(255, 255, 255, 255);
            var layout = new RectInt(new PointInt((int)Width, (int)Height), new PointInt(0, 0));
            renderer.DrawText(text, FontPath, size, white, layout, TextAlign.Center, TextAlign.Center);
            renderer.EndOffscreenFrame();
            ctx!.WaitOffscreenFrameComplete();

            var rgba = ctx.ReadbackOffscreenRgba();
            var lit = 0;
            var pixels = (int)(Width * Height);
            for (var i = 0; i < pixels; i++)
                if (rgba[i * 4] > 24) lit++;
            return lit;
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    /// <summary>
    /// Best-effort offscreen Vulkan setup; returns false when the host has no Vulkan ICD. Mirrors
    /// the helper in <see cref="MtsdfTextRenderTests"/> / <see cref="BlendOpRegressionTests"/>.
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
