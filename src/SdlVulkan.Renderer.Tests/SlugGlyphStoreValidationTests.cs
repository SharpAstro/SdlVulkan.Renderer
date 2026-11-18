using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Guards the Slug glyph store (<see cref="VkSlugGlyphStore"/>) against the frames that read it, under the
/// validation layer. The atlas rule (CLAUDE.md, "When an atlas may touch the GPU"): a growth copies into a NEW
/// buffer with a NEW descriptor set inside the frame's pre-pass upload and hands the old buffer, memory and set to
/// the context's deferred destroy; a draw only reads <see cref="VkSlugGlyphStore.Buffer"/> and
/// <see cref="VkSlugGlyphStore.DescriptorSet"/> as they stand after <c>RecordUploads</c>.
///
/// The store starts at 512 words (about one glyph), so a flood of distinct glyphs grows it many times, while an
/// already drawn glyph and earlier ones are drawn in the same frames through both the batch and the persistent
/// (instanced) paths. Skips when the validation layer is unavailable.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class SlugGlyphStoreValidationTests
{
    private const uint Width = 256;
    private const uint Height = 256;
    private const int MaxFrames = 600;

    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(120);

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);

    [Fact]
    public Task Growth_by_copy_under_batch_and_persistent_draws_leaves_every_frame_valid()
        => RunValidated(RunGrowth, "the Slug store grew by copy under drawing frames");

#if DEBUG
    [Fact]
    public Task A_dropped_frame_that_recorded_a_growth_leaves_the_next_frames_valid()
        => RunValidated(RunDroppedFrame, "a frame that recorded a Slug store growth was dropped");
#endif

    private static async Task RunValidated(Func<VulkanContext, Action> run, string what)
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        var wedged = false;
        try
        {
            var task = Task.Run(run(ctx!), TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(task, Task.Delay(Deadman, TestContext.Current.CancellationToken)) == task;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: the Slug flood did not finish within {Deadman.TotalSeconds:0}s. " +
                            $"Validation messages so far:\n{ValidatedOffscreen.DumpMessages()}");
            }
            await task;

            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0,
                $"the validation layer reported {errors.Length} error(s) while {what}:\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            if (!wedged)
            {
                ValidatedOffscreen.Destroy(ctx, messenger, api);
            }
        }
    }

    // Distinct glyphs DejaVu Sans has: Basic Latin, Latin-1, Latin Extended-A, Greek, Cyrillic.
    private static readonly Rune[] Pool =
        Enumerable.Range(0x21, 0x5E)
            .Concat(Enumerable.Range(0xA1, 0xDF))
            .Concat(Enumerable.Range(0x100, 0x80))
            .Concat(Enumerable.Range(0x391, 0x39))
            .Concat(Enumerable.Range(0x410, 0x40))
            .Select(c => new Rune(c)).ToArray();

    private sealed class Driver(VulkanContext ctx, VkRenderer renderer)
    {
        public int Frames;

        // One frame: the warmed 'A' and the window through the batch path, then the same window through one
        // persistent instanced draw. A glyph not in the store yet is skipped (batch) or blank (instance) until a
        // later frame has drained it, which is what makes the store grow while earlier glyphs are drawn.
        public void Frame(IReadOnlyList<Rune> window)
        {
            var rasterizer = renderer.GlyphRasterizer!;
            var instances = new float[(window.Count + 1) * VkRenderer.SlugInstanceFloats];
            var all = window.Prepend(new Rune('A')).ToArray();
            for (var i = 0; i < all.Length; i++)
            {
                var gid = rasterizer.ResolveGlyphIdentity(FontPath, all[i], -1, GlyphMapHint.Auto).Gid;
                renderer.WriteSlugGlyphInstance(FontPath, gid, null, 4f + i % 12 * 18f, 150f + i / 12 * 18f, 14f, 0f, 1f,
                    instances.AsSpan(i * VkRenderer.SlugInstanceFloats, VkRenderer.SlugInstanceFloats));
            }
            var (buffer, memory) = renderer.CreatePersistentVertexBuffer(instances);
            try
            {
                if (!renderer.BeginOffscreenFrame(Black))
                {
                    return;
                }

                renderer.BeginSlugGlyphBatch(White, 14f);
                for (var i = 0; i < all.Length; i++)
                {
                    renderer.AddBatchedSlugGlyphAtBaseline(FontPath, all[i], -1, 4f + i % 12 * 18f, 20f + i / 12 * 18f);
                }
                renderer.EndGlyphBatch();

                renderer.DrawPersistentSlugGlyphs(buffer, 0, (uint)all.Length, White, 0f, 0f, 1f);

                renderer.EndOffscreenFrame();
                ctx.WaitOffscreenFrameComplete();
                Frames++;
            }
            finally
            {
                renderer.DestroyBuffer(buffer, memory);
            }
            // Encodes land on worker threads over a few frames; the frame loop is the clock, bounded by MaxFrames.
            Thread.Sleep(2);
        }
    }

    private static VkRenderer NewRenderer(VulkanContext ctx)
        => new(ctx, Width, Height) { SlugInitialCapacityWords = 512 };

    private static Action RunGrowth(VulkanContext ctx) => () =>
    {
        using var renderer = NewRenderer(ctx);
        var drv = new Driver(ctx, renderer);
        var cursor = 0;

        // 'A' is drawn first, alone, so it is an already drawn glyph when the flood starts.
        while (drv.Frames < MaxFrames && renderer.SlugStoreStats.Glyphs == 0 || drv.Frames < 3)
        {
            drv.Frame([]);
        }

        while (drv.Frames < MaxFrames && renderer.SlugStoreStats.GrowCount < 3)
        {
            drv.Frame(Enumerable.Range(0, 12).Select(k => Pool[(cursor + k) % Pool.Length]).ToArray());
            cursor += 5;
        }
        var stats = renderer.SlugStoreStats;
        Assert.True(stats.GrowCount >= 2, $"the store never grew twice ({stats})");

        // A few more frames drawing everything so far, then the pending deferred destroys drain.
        for (var f = 0; f < 6; f++)
        {
            drv.Frame(Pool.Take(cursor).TakeLast(40).ToArray());
        }
        Assert.True(Lit(ctx.ReadbackOffscreenRgba()) > 50, "nothing drew after the store grew");
    };

#if DEBUG
    // A frame that drained new glyphs, grew the buffer and recorded the copy is refused at submit and takes its
    // recording with it; the next frame records the growth again, and every later frame must validate and draw.
    private static Action RunDroppedFrame(VulkanContext ctx) => () =>
    {
        using var renderer = NewRenderer(ctx);
        var drv = new Driver(ctx, renderer);
        var fault = ctx.FaultInjection;
        var cursor = 0;

        IReadOnlyList<Rune> Window() => Enumerable.Range(0, 12).Select(k => Pool[(cursor + k) % Pool.Length]).ToArray();

        try
        {
            while (drv.Frames < MaxFrames && renderer.SlugStoreStats.GrowCount < 2)
            {
                drv.Frame(Window());
                cursor += 5;
            }
            Assert.True(renderer.SlugStoreStats.GrowCount >= 2, $"the setup never grew ({renderer.SlugStoreStats})");

            var store = renderer.SlugGlyphStore;
            var dropped = 0;
            var grewInDropped = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                // Fill the buffer to within a few glyphs of full, so the next big drain has to grow it.
                while (drv.Frames < MaxFrames && store.CapacityWords - store.UsedWords > 8000)
                {
                    drv.Frame(Window());
                    cursor += 5;
                }

                // A big fresh window: requested by a clean frame, then waited for until every encode is done, so the
                // frame refused at submit finds all of it waiting and has to grow to take it.
                var big = Enumerable.Range(0, 40).Select(k => Pool[(cursor + k) % Pool.Length]).ToArray();
                cursor += 40;
                drv.Frame(big);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (store.EncodesInFlight > 0)
                {
                    Assert.True(DateTime.UtcNow < deadline, "the encodes did not finish within 60 s");
                    Thread.Sleep(2);
                }

                var before = store.GrowCount;
                fault.RejectSubmits();
                Assert.ThrowsAny<Exception>(() => drv.Frame(big));
                fault.Clear();
                dropped++;
                if (store.GrowCount > before) grewInDropped++;
                // The next frame records the dropped frame's growth and uploads again.
                drv.Frame(big);
            }
            Assert.True(grewInDropped >= 1, $"no dropped frame recorded a growth ({renderer.SlugStoreStats}, {dropped} dropped)");

            for (var f = 0; f < 8; f++)
            {
                drv.Frame(Window());
                cursor += 5;
            }
            Assert.True(Lit(ctx.ReadbackOffscreenRgba()) > 50, "nothing drew after the dropped frames");
        }
        finally
        {
            fault.Clear();
        }
    };
#endif

    private static int Lit(byte[] rgba)
    {
        var lit = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 32) lit++;
        }
        return lit;
    }
}
