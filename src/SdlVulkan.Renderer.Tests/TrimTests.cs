using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="VkRenderer.Trim"/>: the memory goes between frames and the next frame builds it again, drawing the same
/// pixels. Runs on the validation layer when it is there, and asserts it reported nothing.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class TrimTests
{
    private const uint Width = 192;
    private const uint Height = 96;
    private const int MaxFrames = 400;

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");
    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);
    private static readonly RGBAColor32 Red = new(220, 30, 30, 255);

    // Shapes, bitmap text, SDF text and a Slug run in one frame.
    private static void Scene(VkRenderer r, VulkanContext ctx)
    {
        r.BeginOffscreenFrame(Black).ShouldBeTrue();
        r.FillRectangle(new RectInt(new PointInt(60, 30), new PointInt(4, 4)), Red);
        r.BeginGlyphBatch(White);
        for (var i = 0; i < 4; i++) r.AddBatchedGlyph(FontPath, 18f, new Rune('A' + i), -1, 70f + i * 12f, 6f);
        r.EndGlyphBatch();
        r.BeginSdfGlyphBatch(White, 24f);
        for (var i = 0; i < 4; i++) r.AddBatchedSdfGlyph(FontPath, new Rune('a' + i), -1, 4f + i * 16f, 40f);
        r.EndGlyphBatch();
        r.BeginSlugGlyphBatch(White, 20f);
        for (var i = 0; i < 4; i++) r.AddBatchedSlugGlyphAtBaseline(FontPath, new Rune('w' + i), -1, 80f + i * 14f, 60f);
        r.EndGlyphBatch();
        r.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }

    // Draws until the async glyph encodes have landed and two frames in a row read back the same; the frame loop is the
    // clock, bounded by MaxFrames, with a timer wait (never a spin) between frames.
    private static byte[] Settle(VkRenderer r, VulkanContext ctx)
    {
        byte[]? previous = null;
        for (var i = 0; i < MaxFrames; i++)
        {
            Scene(r, ctx);
            var pixels = ctx.ReadbackOffscreenRgba();
            if (!r.FontAtlasDirty && previous is not null && pixels.AsSpan().SequenceEqual(previous)) return pixels;
            previous = pixels;
            Thread.Sleep(2);
        }
        throw new Xunit.Sdk.XunitException("the scene never settled");
    }

    private static void Run(VulkanContext ctx)
    {
        ctx.ResizeOffscreen(Width, Height);
        using var r = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128) { };
        var before = Settle(r, ctx);
        before.Any(b => b != 0).ShouldBeTrue("the scene drew something");
        var full = r.MemoryStats;
        full.BitmapAtlasBytes.ShouldBeGreaterThan(0);
        full.SdfBaseBytes.ShouldBeGreaterThan(0);
        full.SlugStoreBytes.ShouldBeGreaterThan(0);
        full.VertexRingBytes.ShouldBeGreaterThan(0);

        r.Trim().ShouldBeTrue();
        var trimmed = r.MemoryStats;
        trimmed.BitmapAtlasBytes.ShouldBe(0);
        trimmed.SdfBaseBytes.ShouldBe(0);
        trimmed.SdfLargeBytes.ShouldBe(0);
        trimmed.SlugStoreBytes.ShouldBe(0);
        trimmed.VertexRingBytes.ShouldBe(0);
        ctx.PendingDeferredDestroys.ShouldBe(0);

        // Twice in a row is safe, and still empty.
        r.Trim().ShouldBeTrue();
        r.MemoryStats.TotalBytes.ShouldBe(0);

        var after = Settle(r, ctx);
        // A second fresh renderer on the same context, no trim, is the reference: when it reproduces the first frame
        // exactly, so must the frame after a trim; when it differs (the SDF atlas lays cells out in the order the async
        // encodes land, which moves an edge by a level), the trim may differ by no more than that.
        byte[] fresh;
        using (var r2 = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128))
            fresh = Settle(r2, ctx);
        var (refDiff, refMax) = Diff(before, fresh);
        var (differing, maxDelta) = Diff(before, after);
        if (refDiff == 0)
        {
            differing.ShouldBe(0, "a fresh renderer reproduces the frame exactly, so must the one after a trim");
        }
        else
        {
            maxDelta.ShouldBeLessThanOrEqualTo(Math.Max(refMax, 1), "the frame after a trim draws the pixels of the one before");
            differing.ShouldBeLessThan(before.Length / 100, "only edge bytes of the SDF text may differ");
        }
        var back = r.MemoryStats;
        back.BitmapAtlasBytes.ShouldBeGreaterThan(0);
        back.SdfBaseBytes.ShouldBeGreaterThan(0);
        back.SlugStoreBytes.ShouldBeGreaterThan(0);
        back.VertexRingBytes.ShouldBeGreaterThan(0);
    }

    private static (int Differing, int MaxDelta) Diff(byte[] a, byte[] b)
    {
        var differing = 0;
        var maxDelta = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            if (d != 0) differing++;
            maxDelta = Math.Max(maxDelta, d);
        }
        return (differing, maxDelta);
    }

    private static void RunKept(VulkanContext ctx)
    {
        ctx.ResizeOffscreen(Width, Height);
        using var r = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128);
        // A font registered from memory under an id of its own lives on the rasterizer.
        var rasterizer = r.GlyphRasterizer!;
        rasterizer.RegisterFontFromMemory("mem:kept", File.ReadAllBytes(FontPath)).ShouldBeTrue();
        var widthBefore = r.MeasureText("Hello trim", "mem:kept", 18f).Width;
        widthBefore.ShouldBeGreaterThan(0f);
        var stampBefore = r.SdfPageStamp(0, false);
        var epochBefore = r.TrimEpoch;

        r.Trim().ShouldBeTrue();
        r.TrimEpoch.ShouldBe(epochBefore + 1);
        // The same rasterizer object, not disposed, with the registered font; and MeasureText answers while trimmed,
        // for a cached text and for one never measured.
        r.GlyphRasterizer.ShouldBeSameAs(rasterizer);
        r.MeasureText("Hello trim", "mem:kept", 18f).Width.ShouldBe(widthBefore);
        r.MeasureText("another text", "mem:kept", 18f).Width.ShouldBeGreaterThan(0f);

        // Drawing in the registered font after the trim, and a saved SDF page stamp no longer matches.
        for (var i = 0; i < MaxFrames; i++)
        {
            r.BeginOffscreenFrame(Black).ShouldBeTrue();
            r.BeginSdfGlyphBatch(White, 24f);
            for (var g = 0; g < 4; g++) r.AddBatchedSdfGlyph("mem:kept", new Rune('a' + g), -1, 4f + g * 16f, 40f);
            r.EndGlyphBatch();
            r.BeginSlugGlyphBatch(White, 20f);
            for (var g = 0; g < 4; g++) r.AddBatchedSlugGlyphAtBaseline("mem:kept", new Rune('w' + g), -1, 80f + g * 14f, 60f);
            r.EndGlyphBatch();
            r.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            if (!r.FontAtlasDirty) break;
            Thread.Sleep(2);
        }
        ctx.ReadbackOffscreenRgba().Any(b => b != 0).ShouldBeTrue("text in the registered font drew after the trim");
        var stampAfter = r.SdfPageStamp(0, false);
        stampAfter.ShouldNotBe(stampBefore, "a stamp saved before a trim never matches one taken after it");

        // A persistent Slug instance written after the trim is a real quad from the rebuilt store.
        var instance = new float[VkRenderer.SlugInstanceFloats];
        var layout = VkRenderer.SlugGlyphLayout.Pending;
        var gid = rasterizer.ResolveGlyphIdentity("mem:kept", new Rune('A'), -1, default).Gid;
        for (var i = 0; i < MaxFrames; i++)
        {
            layout = r.WriteSlugGlyphInstance("mem:kept", gid, null, 0f, 20f, 20f, 0f, 1f, instance);
            if (layout != VkRenderer.SlugGlyphLayout.Pending) break;
            r.BeginOffscreenFrame(Black).ShouldBeTrue();
            r.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            Thread.Sleep(2);
        }
        layout.ShouldBe(VkRenderer.SlugGlyphLayout.Quad);
    }

    private static void RunInFrame(VulkanContext ctx)
    {
        ctx.ResizeOffscreen(Width, Height);
        using var r = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128);
        r.BeginOffscreenFrame(Black).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => r.Trim());
        r.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        r.Trim().ShouldBeTrue();
    }

    [Fact]
    public void A_frame_after_a_trim_draws_the_same_pixels_and_the_counters_drop_and_return()
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }
        ValidatedOffscreen.Messages.Clear();
        try
        {
            Run(ctx!);
            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0, $"the validation layer reported {errors.Length} error(s):\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }

    [Fact]
    public void Trim_inside_a_frame_throws_and_between_frames_is_fine()
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }
        ValidatedOffscreen.Messages.Clear();
        try
        {
            RunInFrame(ctx!);
            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0, $"the validation layer reported {errors.Length} error(s):\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }

    [Fact]
    public void The_rasterizer_and_its_fonts_survive_a_trim_and_measure_text_keeps_answering()
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }
        ValidatedOffscreen.Messages.Clear();
        try
        {
            RunKept(ctx!);
            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0, $"the validation layer reported {errors.Length} error(s):
{string.Join("

", errors)}");
        }
        finally
        {
            ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }
}
