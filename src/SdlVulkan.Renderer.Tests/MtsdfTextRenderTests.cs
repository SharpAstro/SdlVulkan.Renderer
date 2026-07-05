using System;
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
public sealed unsafe class MtsdfTextRenderTests
{
    private const uint Width = 128;
    private const uint Height = 64;

    private static string FontPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    [Fact]
    public void MtsdfText_RendersCoherentCoverage()
    {
        if (!TryCreateOffscreenContext(out _, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var renderer = new VkRenderer(ctx!, Width, Height);
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
            ctx!.WaitOffscreenFrameComplete();

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
        finally
        {
            ctx?.Dispose();
        }
    }

    /// <summary>
    /// Best-effort offscreen Vulkan setup. Returns false when the host has no Vulkan ICD.
    /// Mirrors the helper in <see cref="BlendOpRegressionTests"/>.
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
