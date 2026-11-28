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
        // The SDF atlas lays glyph cells out in the order the async encodes land, and a cell's neighbours change how an
        // edge samples by at most one level (a second fresh renderer with no trim, same context, differs from the first
        // in exactly these bytes). So "the same pixels" is: same coverage everywhere and no byte off by more than one.
        var differing = 0;
        var maxDelta = 0;
        for (var i = 0; i < before.Length; i++)
        {
            var d = Math.Abs(before[i] - after[i]);
            if (d != 0) differing++;
            maxDelta = Math.Max(maxDelta, d);
        }
        maxDelta.ShouldBeLessThanOrEqualTo(1, "the frame after a trim draws the pixels of the one before");
        differing.ShouldBeLessThan(before.Length / 100, "only edge bytes of the SDF text may differ");
        var back = r.MemoryStats;
        back.BitmapAtlasBytes.ShouldBeGreaterThan(0);
        back.SdfBaseBytes.ShouldBeGreaterThan(0);
        back.SlugStoreBytes.ShouldBeGreaterThan(0);
        back.VertexRingBytes.ShouldBeGreaterThan(0);
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
}
