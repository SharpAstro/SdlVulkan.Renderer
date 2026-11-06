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
/// The instanced Slug path (<c>WriteSlugGlyphInstance</c> into a persistent buffer, <c>DrawPersistentSlugGlyphs</c>,
/// sluginst.vert + slug.frag) on the offscreen device. A page's worth of glyphs is one instance each and one draw:
/// glyphs of three sizes in one draw match <see cref="SlugCoverageOracle"/> in the multisampled and single-sample
/// passes, with and without a depth attachment; a glyph turned 30 degrees and one stretched to 0.7 horizontally match
/// the oracle sampled through the inverse of the same transform; the instanced picture is the batch path's, at the same
/// size and at a zoom; and a glyph the store does not have yet is an empty instance, the rest of the draw standing.
/// Every scenario runs under the validation layer when the host has it and fails on any validation ERROR.
/// </summary>
[Collection("OffscreenGpu")]
public sealed unsafe class SlugInstancedRenderTests(OffscreenGpuFixture gpu, ITestOutputHelper output)
{
    // The tolerance is SlugTextRenderTests's (see there for how it was measured): no pixel more than 8/255 from the
    // oracle, the mean within the 8-bit rounding alone.
    private const float MaxDifference = 8f / 255f;
    private const float MeanDifference = 0.002f;

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);

    private static string DejaVu => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    // A glyph to draw: pen, size in the caller's space, turn, stretch.
    private readonly record struct Placed(char Ch, float PenX, float PenY, float Size, float Rotation = 0f, float XScale = 1f);

    // ---- scenario host (as SlugTextRenderTests's) ---------------------------------------------------------------------

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

    // ---- laying out and drawing -------------------------------------------------------------------------------------

    private static uint GidOf(VkRenderer renderer, char ch)
        => renderer.GlyphRasterizer!.ResolveGlyphIdentity(DejaVu, new Rune(ch), -1, GlyphMapHint.Auto).Gid;

    private static SlugGlyphBlob Encode(VkRenderer renderer, char ch)
    {
        var id = new GlyphIdentity(GidOf(renderer, ch), null);
        new SlugGlyphEncoder().TryEncode(renderer.GlyphRasterizer!, DejaVu, id, out var blob).ShouldBeTrue();
        blob.IsEmpty.ShouldBeFalse();
        return blob;
    }

    // One instance a glyph, in the caller's space with the origin at zero and the zoom at 1 unless said otherwise.
    // Returns how many were still pending.
    private static int WriteInstances(VkRenderer renderer, IReadOnlyList<Placed> glyphs, float[] instances,
        float originX = 0f, float originY = 0f, float zoom = 1f)
    {
        var pending = 0;
        for (var i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            var kind = renderer.WriteSlugGlyphInstance(DejaVu, GidOf(renderer, g.Ch), null,
                (g.PenX - originX) / zoom, (g.PenY - originY) / zoom, g.Size / zoom, g.Rotation, g.XScale,
                instances.AsSpan(i * VkRenderer.SlugInstanceFloats, VkRenderer.SlugInstanceFloats));
            if (kind == VkRenderer.SlugGlyphLayout.Pending) pending++;
        }
        return pending;
    }

    // Writes until the store has every glyph, running an empty frame between tries (the store takes its blobs into the
    // buffer in a frame).
    private static float[] LayOut(VulkanContext ctx, VkRenderer renderer, IReadOnlyList<Placed> glyphs,
        float originX = 0f, float originY = 0f, float zoom = 1f)
    {
        var instances = new float[glyphs.Count * VkRenderer.SlugInstanceFloats];
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (WriteInstances(renderer, glyphs, instances, originX, originY, zoom) == 0) return instances;
            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            Thread.Sleep(5);
        }
        Assert.Fail("the store never delivered the glyphs");
        return instances;
    }

    // One frame: the instances in one persistent buffer, one DrawPersistentSlugGlyphs, white on black, read back. Also
    // returns how many Slug draw calls that frame issued.
    private static (byte[] Rgba, int Draws) DrawInstances(VulkanContext ctx, VkRenderer renderer, float[] instances,
        float originX = 0f, float originY = 0f, float zoom = 1f)
    {
        var count = (uint)(instances.Length / VkRenderer.SlugInstanceFloats);
        var (buffer, memory) = renderer.CreatePersistentVertexBuffer(instances);
        try
        {
            var before = renderer.SlugDrawCalls;
            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            renderer.DrawPersistentSlugGlyphs(buffer, 0, count, White, originX, originY, zoom);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            return (ctx.ReadbackOffscreenRgba(), renderer.SlugDrawCalls - before);
        }
        finally
        {
            renderer.DestroyBuffer(buffer, memory);
        }
    }

    // The same glyphs through the batch path (one colour, one size a batch), until the store has them all.
    private static byte[] DrawBatch(VulkanContext ctx, VkRenderer renderer, IReadOnlyList<Placed> glyphs)
    {
        byte[] rgba = [];
        for (var attempt = 0; attempt < 400; attempt++)
        {
            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            foreach (var bySize in glyphs.GroupBy(g => g.Size))
            {
                renderer.BeginSlugGlyphBatch(White, bySize.Key);
                foreach (var g in bySize)
                    renderer.AddBatchedSlugGlyphAtBaselineByGid(DejaVu, GidOf(renderer, g.Ch), null,
                        g.PenX, g.PenY, g.Rotation, xScale: g.XScale);
                renderer.EndGlyphBatch();
            }
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            rgba = ctx.ReadbackOffscreenRgba();
            if (!renderer.FontAtlasDirty && attempt > 0) return rgba;
            Thread.Sleep(5);
        }
        Assert.Fail("the store never delivered the glyphs");
        return rgba;
    }

    // ---- the oracle, through a transform ----------------------------------------------------------------------------

    // The coverage of one glyph at every pixel centre: the pixel's offset from the pen is taken back through the inverse
    // of the glyph's transform to em space (em x along the writing direction (cos, sin) at size * xScale pixels an em, em
    // y up the glyph, (sin, -cos), at size). The shader takes a pixel's footprint along each ray as the length of the
    // screen gradient of that em coordinate ((dFdx, dFdy) of it), which is 1 / its size on screen whatever the rotation:
    // the pixels per em along x is size * xScale and along y is size. (Until fwidth, the sum of the two magnitudes and up
    // to sqrt(2) too wide at 45 degrees, was replaced by that length, this divided by |cos| + |sin| to match it.)
    private static float[] Oracle(SlugGlyphBlob blob, Placed g, int width, int height)
    {
        var cos = MathF.Cos(g.Rotation);
        var sin = MathF.Sin(g.Rotation);
        var sx = g.Size * g.XScale;
        var sy = g.Size;
        var img = new float[width * height];
        for (var j = 0; j < height; j++)
            for (var i = 0; i < width; i++)
            {
                float dx = i + 0.5f - g.PenX, dy = j + 0.5f - g.PenY;
                var emX = (dx * cos + dy * sin) / sx;
                var emY = (dx * sin - dy * cos) / sy;
                img[j * width + i] = SlugCoverageOracle.Coverage(blob, emX, emY, sx, sy);
            }
        return img;
    }

    private static (float Max, float Mean, int Worst) Compare(byte[] rgba, float[] expected, int w, int h)
    {
        rgba.Length.ShouldBe(w * h * 4);
        float max = 0, sum = 0;
        var worst = 0;
        for (var i = 0; i < w * h; i++)
        {
            var d = MathF.Abs(rgba[i * 4] / 255f - expected[i]);
            sum += d;
            if (d > max) { max = d; worst = i; }
        }
        return (max, sum / (w * h), worst);
    }

    private static int InkIn(byte[] rgba, int width, int fromX, int toX)
    {
        var sum = 0;
        for (var i = 0; i < rgba.Length / 4; i++)
        {
            var x = i % width;
            if (x >= fromX && x < toX) sum += rgba[i * 4];
        }
        return sum / 255;
    }

    // ---- the tests --------------------------------------------------------------------------------------------------

    [Fact]
    public void GlyphsOfThreeSizesAreOneDrawAndMatchTheOracleInEveryPassShape()
    {
        const int w = 200, h = 100;
        Placed[] glyphs =
        [
            new('a', 6.37f, 60.61f, 10f),
            new('B', 24.37f, 60.61f, 28f),
            new('g', 62.37f, 60.61f, 72f),
        ];
        Run((ctx, renderer) =>
        {
            renderer.ResizeOffscreen(w, h);
            var blobs = glyphs.Select(g => Encode(renderer, g.Ch)).ToArray();
            var expected = new float[w * h];
            for (var k = 0; k < glyphs.Length; k++)
            {
                var one = Oracle(blobs[k], glyphs[k], w, h);
                for (var i = 0; i < expected.Length; i++) expected[i] = MathF.Max(expected[i], one[i]);
            }
            expected.Max().ShouldBeGreaterThan(0.99f);

            var instances = LayOut(ctx, renderer, glyphs);
            byte[]? first = null;
            // The shapes a pass takes: no depth attachment (every target starts so), then with one, each at 4x MSAA and
            // single-sample. A pipeline of the wrong shape for its pass is a validation error, which fails the run.
            foreach (var depth in new[] { false, true })
            {
                if (depth) renderer.RequireMeshDepth();
                foreach (var single in new[] { false, true })
                {
                    if (single) WaitUntilSingleSampleReady(renderer);
                    renderer.SingleSampleMainPass = single;
                    var (rgba, draws) = DrawInstances(ctx, renderer, instances);
                    ctx.LastFrameSingleSampled.ShouldBe(single);
                    renderer.SingleSampleMainPass = false;

                    var label = $"{(single ? "single-sample" : "4x MSAA")}{(depth ? " with depth" : "")}";
                    draws.ShouldBe(1, $"{label}: three glyphs of three sizes are ONE draw");
                    var (max, mean, worst) = Compare(rgba, expected, w, h);
                    output.WriteLine($"{label}: max {max * 255f:F2}/255, mean {mean * 255f:F3}/255, worst ({worst % w}, {worst / w})");
                    max.ShouldBeLessThanOrEqualTo(MaxDifference, $"{label}: worst pixel ({worst % w}, {worst / w})");
                    mean.ShouldBeLessThanOrEqualTo(MeanDifference, label);

                    // Analytic: every pass shape draws the same picture.
                    if (first is null) first = rgba;
                    else
                        for (var i = 0; i < rgba.Length; i += 4)
                            Math.Abs(rgba[i] - first[i]).ShouldBeLessThanOrEqualTo(1, $"{label}: pixel {i / 4}");
                }
            }
        });
    }

    [Theory]
    [InlineData(30f, 1f)]
    [InlineData(0f, 0.7f)]
    [InlineData(30f, 0.7f)]
    public void ARotatedOrHorizontallyScaledGlyphMatchesTheOracleSampledThroughTheSameTransform(float degrees, float xScale)
    {
        const int size = 150;
        var g = new Placed('R', size / 2f - 18.63f, size / 2f + 24.39f, 72f, degrees * MathF.PI / 180f, xScale);
        Run((ctx, renderer) =>
        {
            renderer.ResizeOffscreen(size, size);
            var blob = Encode(renderer, g.Ch);
            var expected = Oracle(blob, g, size, size);
            expected.Max().ShouldBeGreaterThan(0.99f);

            var (rgba, draws) = DrawInstances(ctx, renderer, LayOut(ctx, renderer, [g]));
            draws.ShouldBe(1);
            var (max, mean, worst) = Compare(rgba, expected, size, size);
            output.WriteLine($"{degrees} deg, x scale {xScale}: max {max * 255f:F2}/255, mean {mean * 255f:F3}/255");
            max.ShouldBeLessThanOrEqualTo(MaxDifference, $"worst pixel ({worst % size}, {worst / size})");
            mean.ShouldBeLessThanOrEqualTo(MeanDifference);
        });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(0.5f)]
    public void TheInstancedPicturesIsTheBatchPathsAtAnyZoom(float zoom)
    {
        const int w = 260, h = 44;
        var glyphs = new List<Placed>();
        // A baseline off a pixel centre (as SlugTextRenderTests's pen fractions): a flat edge exactly through a centre is
        // a sign test on a ray at exactly zero, which any rounding of the two paths' positions decides differently.
        var pen = 5.25f;
        foreach (var ch in "Slug text, one draw")
        {
            glyphs.Add(new Placed(ch, pen, 30.61f, 20f));
            pen += 12.5f;
        }
        Run((ctx, renderer) =>
        {
            renderer.ResizeOffscreen(w, h);
            var batch = DrawBatch(ctx, renderer, glyphs);
            InkIn(batch, w, 0, w).ShouldBeGreaterThan(300, "the batch path must draw the line, or the comparison proves nothing");

            // The same line in a space `zoom` times smaller, with its origin moved, drawn at that zoom.
            const float originX = 3f, originY = 2f;
            var instances = LayOut(ctx, renderer, glyphs, originX, originY, zoom);
            var (rgba, draws) = DrawInstances(ctx, renderer, instances, originX, originY, zoom);
            draws.ShouldBe(1, "a line is one draw");

            var expected = batch.Where((_, i) => i % 4 == 0).Select(v => v / 255f).ToArray();
            var (max, mean, worst) = Compare(rgba, expected, w, h);
            output.WriteLine($"zoom {zoom}: max {max * 255f:F2}/255, mean {mean * 255f:F3}/255");
            max.ShouldBeLessThanOrEqualTo(MaxDifference, $"worst pixel ({worst % w}, {worst / w})");
            mean.ShouldBeLessThanOrEqualTo(MeanDifference);
        });
    }

    [Fact]
    public void AGlyphTheStoreDoesNotHaveYetIsAnEmptyInstanceAndTheRestOfTheDrawStands()
    {
        const int w = 96, h = 48;
        Placed o = new('O', 8.5f, 36.5f, 24f);
        Placed x = new('X', 52.5f, 36.5f, 24f);
        Run((ctx, renderer) =>
        {
            renderer.ResizeOffscreen(w, h);

            // O in the store; X asked for for the first time, in the same draw.
            LayOut(ctx, renderer, [o]);
            renderer.SlugGlyphsPending.ShouldBeFalse();

            var instances = new float[2 * VkRenderer.SlugInstanceFloats];
            var span = instances.AsSpan();
            renderer.WriteSlugGlyphInstance(DejaVu, GidOf(renderer, 'O'), null, o.PenX, o.PenY, o.Size, 0f, 1f,
                span[..VkRenderer.SlugInstanceFloats]).ShouldBe(VkRenderer.SlugGlyphLayout.Quad);
            // Fill the slot with noise first: a miss must not leave a stale or half-written instance behind.
            span[VkRenderer.SlugInstanceFloats..].Fill(float.NaN);
            renderer.WriteSlugGlyphInstance(DejaVu, GidOf(renderer, 'X'), null, x.PenX, x.PenY, x.Size, 0f, 1f,
                span[VkRenderer.SlugInstanceFloats..]).ShouldBe(VkRenderer.SlugGlyphLayout.Pending);
            instances.AsSpan(VkRenderer.SlugInstanceFloats).ToArray().ShouldAllBe(v => v == 0f,
                "a glyph that is not there is an instance of zeros: no area, no pixel");
            renderer.FontAtlasDirty.ShouldBeTrue("the miss asks for another frame");

            var (rgba, draws) = DrawInstances(ctx, renderer, instances);
            draws.ShouldBe(1, "the miss does not split or drop the draw");
            InkIn(rgba, w, 0, 48).ShouldBeGreaterThan(20, "the glyph already in the store is drawn");
            InkIn(rgba, w, 48, w).ShouldBe(0, "the one that missed is skipped, this frame only");

            // Rewritten once the store has it, the draw has both.
            var settled = LayOut(ctx, renderer, [o, x]);
            var (again, _) = DrawInstances(ctx, renderer, settled);
            InkIn(again, w, 48, w).ShouldBeGreaterThan(20, "and the glyph arrives a frame or two later");
            InkIn(again, w, 0, 48).ShouldBe(InkIn(rgba, w, 0, 48), "the first glyph is the same picture");
        });
    }

    [Fact]
    public void ASpaceIsBlankAndDrawsNothing()
    {
        Run((ctx, renderer) =>
        {
            var instance = new float[VkRenderer.SlugInstanceFloats];
            var kind = VkRenderer.SlugGlyphLayout.Pending;
            for (var attempt = 0; attempt < 400 && kind == VkRenderer.SlugGlyphLayout.Pending; attempt++)
            {
                kind = renderer.WriteSlugGlyphInstance(DejaVu, GidOf(renderer, ' '), null, 4f, 20f, 12f, 0f, 1f, instance);
                if (kind != VkRenderer.SlugGlyphLayout.Pending) break;
                renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
                renderer.EndOffscreenFrame();
                ctx.WaitOffscreenFrameComplete();
                Thread.Sleep(5);
            }
            kind.ShouldBe(VkRenderer.SlugGlyphLayout.Blank, "a space is final, not pending");
            instance.ShouldAllBe(v => v == 0f);
        });
    }
}
