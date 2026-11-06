using System;
using System.IO;
using System.Linq;
using Shouldly;
using SlugProbe;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <c>tools/SlugProbe</c>'s page and measuring (SlugProbePage.cs, compiled into this project) run for a handful of frames
/// on the offscreen device, under the validation layer when the host has it: the Slug page is one draw per colour, the
/// worst curve count of the band walk on the densest CJK glyph is positive, the burst of new glyphs is taken into the store
/// (growing it), and the validation layer reports no error. The numbers themselves are not asserted: they are the
/// probe's to print, on a machine a person chose, and a software device's would mean nothing as a bound.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class SlugProbeSmokeTests(OffscreenGpuFixture gpu)
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void ProbePage_SlugIsOneDrawPerColour_AndTheCurveWalkIsCounted()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");

        // The validation layer when the host has it, a plain offscreen context when not (the shared fixture's).
        var validated = ValidatedOffscreen.TryCreate(SlugProbePage.PageWidth, SlugProbePage.PageHeight,
            out var vctx, out var messenger, out var api, out _);
        SlugProbeReport report;
        try
        {
            if (validated) ValidatedOffscreen.Messages.Clear();
            report = SlugProbePage.Run(validated ? vctx! : gpu.Context!, Path.Combine(Fixtures, "DejaVuSans.ttf"),
                Path.Combine(Fixtures, "NotoSansJP-Subset.otf"), frames: 5, sizePx: 15f, burst: 300, msaa: 1);

            if (validated)
            {
                var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
                Assert.True(errors.Length == 0, $"validation errors:\n{string.Join("\n\n", errors)}");
            }
        }
        finally
        {
            if (validated) ValidatedOffscreen.Destroy(vctx, messenger, api);
        }

        // One Slug draw a colour: three colours (body, heading, CJK block), however many glyphs and two sizes.
        report.Slug.DrawsPerFrame.ShouldBe(SlugProbePage.Colors.Length);
        report.Slug.Glyphs.ShouldBeGreaterThan(1000, "a page of reading-size text");
        // MTSDF draws the same glyphs, a draw per colour and atlas page: never fewer than Slug's.
        report.Mtsdf.Glyphs.ShouldBe(report.Slug.Glyphs);
        report.Mtsdf.DrawsPerFrame.ShouldBeGreaterThanOrEqualTo(report.Slug.DrawsPerFrame);

        // The CJK block fills its cells with the face's distinct glyphs, as many as it has (the three-glyph fixture repeats
        // them, and the report says so; a full CJK face, which --cjk-font takes, would not).
        report.CjkDistinct.ShouldBeGreaterThanOrEqualTo(3);
        report.CjkCells.ShouldBeGreaterThanOrEqualTo(report.CjkDistinct);
        SlugProbePage.Format(report).ShouldContain($"{report.CjkDistinct} distinct glyphs");

        // The densest CJK glyph, at reading size and at the cold size.
        report.Curves.Count.ShouldBe(2);
        foreach (var c in report.Curves)
        {
            c.Curves.ShouldBeGreaterThan(0);
            c.WorstPerPixel.ShouldBeGreaterThan(0);
            c.WorstPerRay.ShouldBeGreaterThan(0);
            c.WorstPerPixel.ShouldBeGreaterThanOrEqualTo(c.WorstPerRay);
            // A ray walks no more than the longest list it could be given.
            c.WorstPerRay.ShouldBeLessThanOrEqualTo(c.LongestBandList);
        }

        // The burst was taken in, and a glyph the face could not give is the exception.
        report.Burst.GlyphsRequested.ShouldBe(300);
        report.Burst.Frames.ShouldBeGreaterThan(0);
        report.Burst.LongestWallMs.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void MeasureCurves_CountsMoreOnADenseGlyphThanOnAPlainOne()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");

        var rasterizer = new DIR.Lib.ManagedFontRasterizer();
        DIR.Lib.SlugGlyphBlob Encode(string font, int codepoint)
        {
            var gid = rasterizer.ResolveGlyphIdentity(font, new System.Text.Rune(codepoint), -1, DIR.Lib.GlyphMapHint.Auto).Gid;
            new DIR.Lib.SlugGlyphEncoder().TryEncode(rasterizer, font, new DIR.Lib.GlyphIdentity(gid, null), out var blob)
                .ShouldBeTrue();
            return blob;
        }

        var dense = SlugProbePage.MeasureCurves("dense", Encode(Path.Combine(Fixtures, "NotoSansJP-Subset.otf"), 0x9F8D), 120f);
        var plain = SlugProbePage.MeasureCurves("l", Encode(Path.Combine(Fixtures, "DejaVuSans.ttf"), 'l'), 120f);
        dense.WorstPerPixel.ShouldBeGreaterThan(plain.WorstPerPixel);
        dense.Curves.ShouldBeGreaterThan(plain.Curves);
    }
}
