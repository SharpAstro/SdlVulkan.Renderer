using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Slug text end to end on the offscreen device: a glyph drawn through <c>BeginSlugGlyphBatch</c> /
/// <c>AddBatchedSlugGlyphAtBaseline(ByGid)</c> / <c>EndGlyphBatch</c> (the store, slug.vert, slug.frag, the Slug
/// pipeline), read back and compared pixel by pixel with <see cref="SlugCoverageOracle"/>, the CPU port of the same
/// coverage function reading the same blob. Glyphs: DejaVu's O (a counter), two DejaVu glyphs whose contours overlap
/// (gid 6178, 1430: nonzero winding must fill where they overlap) and three dense CJK glyphs from a committed subset of
/// Noto Sans JP, each at 12 px (a stem a pixel wide) and 200 px (long curves, many bands), in the 4x multisampled main
/// pass and in <c>SingleSampleMainPass</c>. Coverage is the shader's own, so the two passes must give the same picture, and
/// the picture must be the oracle's. Every scenario runs under the validation layer when the host has it and fails on any
/// validation ERROR.
/// </summary>
[Collection("OffscreenGpu")]
public sealed unsafe class SlugTextRenderTests(OffscreenGpuFixture gpu, ITestOutputHelper output)
{
    // ---- the stated tolerance ----------------------------------------------------------------------------------------
    // The GPU runs the oracle's arithmetic (same root-eligibility table, same solve, same weights, the footprint from
    // the length of the screen gradient of the interpolated em coordinate rather than the exact pixels per em), so the pictures agree to float
    // noise, and the one thing that can differ visibly is the framebuffer: coverage is written as an 8-bit alpha, which
    // rounds by half a step (0.5 / 255 = 0.002), and the shader skips a fragment below that step. On top of this a root
    // solve at a tangent (discriminant near 0) is ill-conditioned in single precision and differs between a CPU and a
    // GPU's fused multiply-adds by a small part of a pixel, i.e. a few hundredths of coverage on the odd edge pixel.
    // So: no pixel more than MaxDifference (8/255, about 0.031) from the oracle, and the mean over the frame within
    // MeanDifference (0.002, the 8-bit rounding alone), which a wrong band, list or winding would break by orders of
    // magnitude (a glyph's worth of pixels is 0 or 1 instead of 1 or 0). Measured on lavapipe: the worst pixel of any
    // case is 5.6/255 (one edge pixel of the 12 px U+9F8D, a dense CJK glyph), the usual worst 1 to 3/255, every mean
    // below 0.07/255; the bound leaves room for another driver's rounding of the same arithmetic.
    private const float MaxDifference = 8f / 255f;
    private const float MeanDifference = 0.002f;

    // The pen sits at a fractional pixel position so the edges fall at every sub-pixel phase.
    private const float PenFractionX = 0.37f;
    private const float PenFractionY = 0.61f;
    private const int Margin = 6;

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);

    private static string DejaVu => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");
    private static string NotoJp => Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotoSansJP-Subset.otf");

    // A glyph under test: by rune (the codepoint overload) or, with a rune of default, by glyph id.
    private sealed record Case(string Name, Func<string> Font, Rune Rune, uint Gid)
    {
        public string FontPath => Font();
        public override string ToString() => Name;
    }

    private static readonly Case[] Cases =
    [
        new("DejaVu O", () => DejaVu, new Rune('O'), 0),
        new("DejaVu gid 6178 (overlapping contours)", () => DejaVu, default, 6178),
        new("DejaVu gid 1430 (overlapping contours)", () => DejaVu, default, 1430),
        new("Noto JP U+9B31", () => NotoJp, new Rune(0x9B31), 0),
        new("Noto JP U+9F8D", () => NotoJp, new Rune(0x9F8D), 0),
        new("Noto JP U+9A5A", () => NotoJp, new Rune(0x9A5A), 0),
    ];

    private static readonly float[] Sizes = [12f, 200f];

    public static IEnumerable<TheoryDataRow<int>> CaseIndexes() => Enumerable.Range(0, Cases.Length).Select(i => new TheoryDataRow<int>(i));

    // ---- scenario host ----------------------------------------------------------------------------------------------

    private static VulkanContext? MultisampledContext()
    {
        try
        {
            vkInitialize().CheckResult();
            VkApplicationInfo appInfo = new() { apiVersion = SdlVulkanWindow.InstanceApiVersion() };
            VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, 64, 64, msaaSamples: VkSampleCountFlags.Count4);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A 4x context, under the validation layer when the host has it, and a renderer on it.
    private void Run(Action<VulkanContext, VkRenderer> scenario)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        if (ValidatedOffscreen.TryCreate(64, 64, out var vctx, out var messenger, out var api, out _,
                msaaSamples: VkSampleCountFlags.Count4))
        {
            ValidatedOffscreen.Messages.Clear();
            try
            {
                using (var renderer = new VkRenderer(vctx!, 64, 64))
                    scenario(vctx!, renderer);
                var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
                Assert.True(errors.Length == 0, $"validation errors:\n{string.Join("\n\n", errors)}");
            }
            finally
            {
                ValidatedOffscreen.Destroy(vctx, messenger, api);
            }
            return;
        }

        using var ctx = MultisampledContext();
        Assert.SkipWhen(ctx is null, "no multisampled Vulkan device on this host");
        using var plain = new VkRenderer(ctx!, 64, 64);
        scenario(ctx!, plain);
    }

    private static void WaitUntilSingleSampleReady(VkRenderer renderer)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!renderer.SingleSampleMainPassReady && clock.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(5);
        renderer.SingleSampleMainPassReady.ShouldBeTrue("the single-sample pipelines never came in");
    }

    private static GlyphIdentity IdentityOf(VkRenderer renderer, Case c)
        => c.Rune.Value != 0
            ? renderer.GlyphRasterizer!.ResolveGlyphIdentity(c.FontPath, c.Rune, -1, GlyphMapHint.Auto)
            : new GlyphIdentity(c.Gid, null);

    private static SlugGlyphBlob Encode(VkRenderer renderer, Case c)
    {
        new SlugGlyphEncoder().TryEncode(renderer.GlyphRasterizer!, c.FontPath, IdentityOf(renderer, c), out var blob)
            .ShouldBeTrue($"{c.Name} must encode");
        blob.IsEmpty.ShouldBeFalse();
        return blob;
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    // The frame size, and the pen (a pixel position plus the fraction), that put the glyph's ink Margin pixels in.
    private static (uint W, uint H, float PenX, float PenY) Layout(SlugGlyphBlob blob, float ppem)
    {
        var penX = (int)MathF.Ceiling(-blob.MinXEm * ppem) + Margin + PenFractionX;
        var penY = (int)MathF.Ceiling(blob.MaxYEm * ppem) + Margin + PenFractionY;
        var w = (uint)MathF.Ceiling(penX + blob.MaxXEm * ppem) + Margin;
        var h = (uint)MathF.Ceiling(penY - blob.MinYEm * ppem) + Margin;
        return (w, h, penX, penY);
    }

    // Draws the glyph white on black through the Slug batch until the store has it (a miss skips the glyph and keeps the
    // frame dirty, as it must), and reads the last frame back. Red is the coverage: white over black.
    private static byte[] DrawGlyph(VulkanContext ctx, VkRenderer renderer, Case c, float ppem, float penX, float penY,
        float rotation = 0f)
    {
        byte[] rgba = [];
        var id = IdentityOf(renderer, c);
        for (var attempt = 0; attempt < 400; attempt++)
        {
            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            renderer.BeginSlugGlyphBatch(White, ppem);
            if (c.Rune.Value != 0)
                renderer.AddBatchedSlugGlyphAtBaseline(c.FontPath, c.Rune, -1, penX, penY, rotation);
            else
                renderer.AddBatchedSlugGlyphAtBaselineByGid(c.FontPath, id.Gid, id.Type1Name, penX, penY, rotation);
            renderer.EndGlyphBatch();
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            rgba = ctx.ReadbackOffscreenRgba();
            if (!renderer.FontAtlasDirty && attempt > 0) return rgba;
            Thread.Sleep(5);
        }
        Assert.Fail($"{c.Name}: the store never delivered the glyph");
        return rgba;
    }

    private static (float Max, float Mean, int Worst) Compare(byte[] rgba, float[] oracle, int w, int h)
    {
        rgba.Length.ShouldBe(w * h * 4);
        float max = 0, sum = 0;
        var worst = 0;
        for (var i = 0; i < w * h; i++)
        {
            var d = MathF.Abs(rgba[i * 4] / 255f - oracle[i]);
            sum += d;
            if (d > max) { max = d; worst = i; }
        }
        return (max, sum / (w * h), worst);
    }

    // ---- the comparison ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void AGlyphMatchesTheCoverageOracleInBothPasses(int caseIndex)
    {
        var c = Cases[caseIndex];
        Run((ctx, renderer) =>
        {
            var blob = Encode(renderer, c);
            foreach (var ppem in Sizes)
            {
                var (w, h, penX, penY) = Layout(blob, ppem);
                renderer.ResizeOffscreen(w, h);

                // The oracle's pixel (i, j) has its centre at em (originX + (i + .5) / ppe, originY - (j + .5) / ppe); the
                // device's at (i + .5, j + .5) with the pen at (penX, penY), y down.
                var oracle = SlugCoverageOracle.Render(blob, ppem, -penX / ppem, penY / ppem, (int)w, (int)h);
                oracle.Max().ShouldBeGreaterThan(0.99f, $"{c.Name} at {ppem}: the glyph must have solid ink");

                byte[]? first = null;
                foreach (var single in new[] { false, true })
                {
                    if (single) WaitUntilSingleSampleReady(renderer);
                    renderer.SingleSampleMainPass = single;
                    var rgba = DrawGlyph(ctx, renderer, c, ppem, penX, penY);
                    ctx.LastFrameSingleSampled.ShouldBe(single, "the pass the picture was drawn in");
                    renderer.SingleSampleMainPass = false;

                    var (max, mean, worst) = Compare(rgba, oracle, (int)w, (int)h);
                    output.WriteLine($"{c.Name} @ {ppem} px, {(single ? "single-sample" : "4x MSAA")}: " +
                                     $"max {max * 255f:F2}/255, mean {mean * 255f:F3}/255, worst pixel ({worst % w}, {worst / w}), " +
                                     $"{(int)w}x{(int)h}");
                    max.ShouldBeLessThanOrEqualTo(MaxDifference,
                        $"{c.Name} at {ppem} px ({(single ? "single-sample" : "4x MSAA")}): worst pixel ({worst % w}, {worst / w})");
                    mean.ShouldBeLessThanOrEqualTo(MeanDifference, $"{c.Name} at {ppem} px: mean difference");

                    // Coverage is analytic: the two passes draw the same picture, to the byte.
                    if (first is null) first = rgba;
                    else
                    {
                        var apart = 0;
                        for (var i = 0; i < rgba.Length; i += 4)
                            apart = Math.Max(apart, Math.Abs(rgba[i] - first[i]));
                        apart.ShouldBeLessThanOrEqualTo(1,
                            $"{c.Name} at {ppem} px: MSAA and single-sample must agree, coverage is the shader's own");
                    }
                }
            }
        });
    }

    [Fact]
    public void ARotatedGlyphMatchesTheOracleSampledAtTheRotatedPixels()
    {
        // A quarter turn: the pixel's footprint along the glyph's x is then along the screen's y, which only the
        // derivative-based footprint (the gradient's length, of the em coordinate) gets right; a footprint taken from the font size along
        // the screen's x would be the same here, so a wrong axis would not show, but a footprint taken from dFdx alone
        // would read the glyph's x as flat and break.
        var c = Cases[0];
        Run((ctx, renderer) =>
        {
            var blob = Encode(renderer, c);
            const float ppem = 64f;
            var (w0, h0, _, _) = Layout(blob, ppem);
            var size = Math.Max(w0, h0) + 2 * Margin;
            renderer.ResizeOffscreen(size, size);

            // The pen at the middle; rotation pi/2 turns the baseline direction (1, 0) to (0, 1) (down the screen).
            float penX = size / 2f + PenFractionX, penY = size / 2f + PenFractionY;
            var rgba = DrawGlyph(ctx, renderer, c, ppem, penX, penY, rotation: MathF.PI / 2f);

            // Screen offset d from the pen: em x = d . (0, 1) / ppem = d.y / ppem, em y = d . (up = (1, 0)) / ppem.
            var oracle = new float[size * size];
            for (var j = 0; j < size; j++)
                for (var i = 0; i < size; i++)
                {
                    float dx = i + 0.5f - penX, dy = j + 0.5f - penY;
                    oracle[j * size + i] = SlugCoverageOracle.Coverage(blob, dy / ppem, dx / ppem, ppem);
                }
            oracle.Max().ShouldBeGreaterThan(0.99f);
            var (max, mean, worst) = Compare(rgba, oracle, (int)size, (int)size);
            output.WriteLine($"rotated O: max {max * 255f:F2}/255, mean {mean * 255f:F3}/255");
            max.ShouldBeLessThanOrEqualTo(MaxDifference, $"worst pixel ({worst % size}, {worst / size})");
            mean.ShouldBeLessThanOrEqualTo(MeanDifference);
        });
    }

    [Fact]
    public void AGlyphTheStoreHasNotEncodedYetIsSkippedAndTheRestOfTheBatchDraws()
    {
        var o = Cases[0];
        var x = new Case("DejaVu X", () => DejaVu, new Rune('X'), 0);
        Run((ctx, renderer) =>
        {
            renderer.ResizeOffscreen(96, 48);

            // O is in the store; X is asked for in the same batch for the first time.
            DrawGlyph(ctx, renderer, o, 24f, 8.5f, 36.5f);
            renderer.SlugGlyphsPending.ShouldBeFalse();

            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            renderer.BeginSlugGlyphBatch(White, 24f);
            renderer.AddBatchedSlugGlyphAtBaseline(o.FontPath, o.Rune, -1, 8.5f, 36.5f);
            renderer.AddBatchedSlugGlyphAtBaseline(x.FontPath, x.Rune, -1, 52.5f, 36.5f);
            renderer.EndGlyphBatch();
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            var rgba = ctx.ReadbackOffscreenRgba();

            InkIn(rgba, 96, 0, 48).ShouldBeGreaterThan(20, "the glyph already in the store is drawn");
            InkIn(rgba, 96, 48, 96).ShouldBe(0, "the glyph that missed is skipped, this frame only");
            renderer.FontAtlasDirty.ShouldBeTrue("the miss asks for another frame");

            // The next frames deliver it.
            var drawn = DrawGlyph(ctx, renderer, x, 24f, 52.5f, 36.5f);
            InkIn(drawn, 96, 48, 96).ShouldBeGreaterThan(20, "and the glyph arrives a frame or two later");
        });
    }

    [Fact]
    public void ManyGlyphsOfOneColourAreOneDrawAndTheStoreAnswersEveryFrameAfter()
    {
        // A line of text, one batch: the vertex ring carries 6 vertices a glyph and nothing else is written for it, so a
        // batch of any size is one vkCmdDraw (VkRenderer.FlushSlugBatch). What can be seen from outside is that the line
        // draws whole, repeats exactly on the next frame, and a second colour is a second batch.
        const string text = "Slug text, one draw";
        Run((ctx, renderer) =>
        {
            renderer.ResizeOffscreen(300, 40);
            byte[] Line(RGBAColor32 color)
            {
                byte[] rgba = [];
                for (var attempt = 0; attempt < 400; attempt++)
                {
                    renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
                    renderer.BeginSlugGlyphBatch(color, 20f);
                    var pen = 6.25f;
                    foreach (var ch in text)
                    {
                        renderer.AddBatchedSlugGlyphAtBaseline(DejaVu, new Rune(ch), -1, pen, 28.5f);
                        pen += 10.5f;
                    }
                    renderer.EndGlyphBatch();
                    renderer.EndOffscreenFrame();
                    ctx.WaitOffscreenFrameComplete();
                    rgba = ctx.ReadbackOffscreenRgba();
                    if (!renderer.FontAtlasDirty && attempt > 0) break;
                    Thread.Sleep(5);
                }
                return rgba;
            }

            var white = Line(White);
            var again = Line(White);
            again.ShouldBe(white, "a settled line is the same picture every frame");
            InkIn(white, 300, 0, 300).ShouldBeGreaterThan(300);

            // Premultiplied over the backdrop: a half-transparent colour halves the coverage everywhere.
            var half = Line(new RGBAColor32(255, 255, 255, 128));
            var worst = 0;
            for (var i = 0; i < white.Length; i += 4)
                worst = Math.Max(worst, Math.Abs(half[i] - (int)MathF.Round(white[i] * 128f / 255f)));
            worst.ShouldBeLessThanOrEqualTo(2, "the colour's alpha scales the coverage");
        });
    }

    // The red channel summed over the columns [from, to) of every row: how much ink is there.
    private static int InkIn(byte[] rgba, int width, int from, int to)
    {
        var sum = 0;
        for (var i = 0; i < rgba.Length / 4; i++)
        {
            var x = i % width;
            if (x >= from && x < to) sum += rgba[i * 4];
        }
        return sum / 255;
    }
}
