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
/// Guards the MTSDF atlas (<see cref="VkSdfFontAtlas"/>, base and large tier) against the frames that
/// sample it, under the validation layer. The atlas rule (CLAUDE.md, "When an atlas may touch the GPU"):
/// a page append creates new objects only, a per-page LRU recycle rewrites staging that the frame's
/// pre-pass upload copies, and evict-all, which runs at a frame start, must not destroy a page the
/// previous frame may still sample (it used to destroy right after a bounded wait that can skip), so
/// it hands the page to the context's deferred destroy.
///
/// The pages are 128 px, so a few glyphs fill one and a short flood appends, recycles and, once every
/// page is in use, wipes the atlas. Skips when the validation layer is unavailable, like
/// <see cref="SyncValidationGlyphFloodTests"/>.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class SdfAtlasValidationTests
{
    private const uint Width = 256;
    private const uint Height = 256;
    private const int PageDim = 128;
    private const float BaseSize = 40f;
    private const float LargeSize = 100f;
    private const int MaxFrames = 500;

    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(120);

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);

    [Fact]
    public Task Page_append_lru_recycle_and_evict_all_over_several_frames_leave_every_frame_valid()
        => RunValidated(RunFlood, "the SDF atlas appended pages, recycled one and wiped itself");

#if DEBUG
    [Fact]
    public Task A_dropped_frame_that_appended_and_uploaded_a_page_leaves_the_next_frames_valid()
        => RunValidated(RunDroppedFrame, "a frame that appended an SDF atlas page was dropped");
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
                Assert.Fail($"deadman: the SDF atlas flood did not finish within {Deadman.TotalSeconds:0}s. " +
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

        // One frame: the given base-tier runes and a few large-tier ones, each requested through the
        // draw path (queued for background rasterization on a miss, drawn once resident), plus the
        // warmed 'A', which is inserted synchronously before the atlas flush like a host's pre-warm.
        public void Frame(IEnumerable<Rune> baseRunes, bool large = true)
        {
            if (!renderer.BeginOffscreenFrame(Black))
            {
                return;
            }

            renderer.BeginSdfGlyphBatch(White, BaseSize);
            var x = 4f;
            foreach (var r in baseRunes)
            {
                renderer.AddBatchedSdfGlyph(FontPath, r, -1, x, 8f);
                x = x + 3f > Width - 40f ? 4f : x + 3f;
            }
            renderer.AddBatchedSdfGlyph(FontPath, new Rune('A'), -1, 8f, 100f);
            renderer.EndGlyphBatch();

            if (large)
            {
                renderer.BeginSdfGlyphBatch(White, LargeSize);
                var lx = 4f;
                foreach (var r in Pool.Skip(33).Take(12))
                {
                    renderer.AddBatchedSdfGlyph(FontPath, r, -1, lx, 150f);
                    lx += 18f;
                }
                renderer.EndGlyphBatch();
            }

            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            Frames++;
            // Background rasterization lands over a few frames; the frame loop is the clock.
            Thread.Sleep(8);
        }
    }

    private static VkRenderer NewRenderer(VulkanContext ctx)
    {
        // Small pages for the base tier; the large tier is capped at two pages and refuses past that.
        var renderer = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: PageDim,
            sdfLargeTier: new SdfLargeTierOptions(RasterSize: 128f, MaxPages: 2));
        renderer.OnPreFlush = () => renderer.PreWarmSdfGlyph(FontPath, BaseSize, new Rune('A'));
        return renderer;
    }

    private static Action RunFlood(VulkanContext ctx) => () =>
    {
        using var renderer = NewRenderer(ctx);
        var atlas = renderer.BaseSdfAtlas!;
        var drv = new Driver(ctx, renderer);
        var cursor = 0;
        var stamps = new List<long>();
        var lruRecycles = 0;

        void Observe()
        {
            for (var i = 0; i < atlas.PageCount; i++)
            {
                var s = atlas.PageStamp(i);
                if (i < stamps.Count)
                {
                    if (stamps[i] != s && atlas.PagesDeferredDestroyed == 0) lruRecycles++;
                    stamps[i] = s;
                }
                else
                {
                    stamps.Add(s);
                }
            }
        }

        // Phase 1: a sliding window of fresh glyphs. Pages append until the cap; then the next fresh glyph
        // finds every page full and the coldest page is recycled in place.
        while (drv.Frames < MaxFrames && !(atlas.PageCount >= 16 && lruRecycles >= 2))
        {
            drv.Frame(Enumerable.Range(0, 10).Select(k => Pool[(cursor + k) % Pool.Length]));
            cursor += 4;
            Observe();
        }
        Assert.True(atlas.PageCount >= 16, $"the flood never appended up to the cap ({atlas.PageCount} pages; {atlas.FrameStats})");
        Assert.True(lruRecycles >= 2, $"the flood never recycled an LRU page ({lruRecycles}; {atlas.FrameStats})");

        // Phase 2: keep every resident glyph drawn (so every page stays hot) while new ones keep arriving:
        // nothing is safe to recycle, so the atlas wipes itself at the next frame start.
        var recent = new Queue<Rune>();
        while (drv.Frames < MaxFrames && atlas.PagesDeferredDestroyed == 0)
        {
            for (var k = 0; k < 6; k++)
            {
                recent.Enqueue(Pool[cursor++ % Pool.Length]);
                if (recent.Count > 60) recent.Dequeue();
            }
            drv.Frame(recent.ToArray());
        }
        Assert.True(atlas.PagesDeferredDestroyed > 0, $"the flood never evicted all pages ({atlas.FrameStats})");
        // The counter above is bumped beside the DeferDestroy call, so it survives a mutant that drops the
        // call, and the queue's length survives too (the upload-buffer deferral keeps it non-empty). What does
        // not survive is the pages themselves: the wipe ran on the last frame, so its pages are still waiting
        // for their frames to retire (an entry retires two frames after the one that queued it) and fewer have
        // been destroyed than were handed over.
        Assert.True(atlas.PagesDestroyed < atlas.PagesDeferredDestroyed && ctx.PendingDeferredDestroys > 0,
            $"the evict-all wipe destroyed its pages at once ({atlas.PagesDestroyed} destroyed of {atlas.PagesDeferredDestroyed}, {ctx.PendingDeferredDestroys} pending)");

        // And the atlas still works after the wipe: a few more frames of fresh glyphs, then a lit readback.
        for (var f = 0; f < 12; f++)
        {
            drv.Frame(Enumerable.Range(0, 10).Select(k => Pool[(cursor + k) % Pool.Length]));
            cursor += 4;
        }
        Assert.True(Lit(ctx.ReadbackOffscreenRgba()) > 50, "nothing drew after the atlas wiped itself");
        Assert.True(renderer.LargeSdfAtlas!.PageCount >= 1);
    };

#if DEBUG
    // A frame that drained new glyphs into a fresh page and recorded its upload is refused at submit and
    // takes its recording with it; the frames after must still validate and draw.
    private static Action RunDroppedFrame(VulkanContext ctx) => () =>
    {
        using var renderer = NewRenderer(ctx);
        var atlas = renderer.BaseSdfAtlas!;
        var drv = new Driver(ctx, renderer);
        var fault = ctx.FaultInjection;
        var cursor = 0;

        IEnumerable<Rune> Window() => Enumerable.Range(0, 10).Select(k => Pool[(cursor + k) % Pool.Length]);

        try
        {
            // Debug rasterizes slowly: the frame loop (and its pause) is the clock, bounded by MaxFrames.
            while (drv.Frames < MaxFrames && atlas.PageCount < 3)
            {
                drv.Frame(Window());
                cursor += 4;
            }
            Assert.True(atlas.PageCount >= 3, $"the setup never appended pages ({atlas.FrameStats})");

            var dropped = 0;
            var appendedInDropped = false;
            for (var attempt = 0; attempt < 40 && !(appendedInDropped && dropped >= 2); attempt++)
            {
                var before = atlas.PagesCreated;
                fault.RejectSubmits();
                Assert.ThrowsAny<Exception>(() => drv.Frame(Window()));
                fault.Clear();
                dropped++;
                if (atlas.PagesCreated > before) appendedInDropped = true;
                cursor += 4;
                // Let the background rasterizer land the next window before the next refused frame.
                drv.Frame(Window());
            }
            Assert.True(appendedInDropped, $"no dropped frame appended a page ({atlas.FrameStats})");

            for (var f = 0; f < 8; f++)
            {
                drv.Frame(Window());
                cursor += 4;
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
