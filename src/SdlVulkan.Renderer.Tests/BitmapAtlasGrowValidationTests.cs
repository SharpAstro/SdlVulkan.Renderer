using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Guards the bitmap (colour-glyph) atlas's growth against the frame being recorded. <see cref="VkFontAtlas"/>
/// grows by destroying its image and view and rewriting the context's descriptor set. When that ran on a
/// cache miss inside the draw calls, a glyph drawn earlier in the same frame had already bound that set
/// and recorded a draw that samples the old view: rewriting a set bound in a recording command buffer
/// (the pool has no update-after-bind) and destroying a view a recorded draw uses each invalidate the
/// command buffer, so the frame's submit is undefined (tianwen#621). The atlas now defers a grow to the
/// next frame's start, before anything is recorded.
///
/// The validation layer reports the invalidated command buffer at submit on any ICD, lavapipe included,
/// so this reproduces deterministically where a vendor failure never would. Skips when the layer is
/// unavailable, like <see cref="SyncValidationGlyphFloodTests"/>.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class BitmapAtlasGrowValidationTests
{
    private const uint Width = 256;
    private const uint Height = 256;

    // The largest raster size: a glyph cell is about 128 px, so the atlas's initial 512 x 512 holds about
    // sixteen of them and the flood below must grow it.
    private const float Size = 128f;

    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(60);

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    [Fact]
    public Task A_grow_while_a_frame_records_after_a_drawn_glyph_leaves_the_frame_valid()
        => RunValidated(Run, "the bitmap atlas grew under a recording frame");

    [Fact]
    public Task Eviction_at_the_cap_over_several_frames_leaves_every_frame_valid()
        => RunValidated(RunEviction, "the bitmap atlas evicted at its cap");

#if DEBUG
    [Fact]
    public Task A_dropped_frame_that_grew_and_flushed_the_atlas_leaves_the_next_frames_valid()
        => RunValidated(RunDroppedFrame, "a frame that grew the bitmap atlas was dropped");
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
                Assert.Fail($"deadman: the bitmap atlas flood did not finish within {Deadman.TotalSeconds:0}s. " +
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

    private static Rune[] Flood() => Enumerable.Range('B', 25).Concat(Enumerable.Range('a', 26)).Concat(Enumerable.Range('0', 10))
        .Select(c => new Rune((char)c)).ToArray();

    // Eviction runs at a frame start, so a flood reaches the cap in one frame and the wipe happens in the next:
    // several frames, each drawing 'A' (warmed, so a draw bound the set and sampled the image before and after a
    // wipe) and a different slice of the flood. The cap is the initial size, so no grow intervenes.
    private static Action RunEviction(VulkanContext ctx) => () =>
    {
        var font = FontPath;
        var black = new RGBAColor32(0, 0, 0, 255);
        var white = new RGBAColor32(255, 255, 255, 255);
        var first = new Rune('A');
        var flood = Flood();

        using var renderer = new VkRenderer(ctx, Width, Height);
        renderer.FontAtlas!.MaxAtlasSize = 512;
        renderer.OnPreFlush = () => renderer.PreWarmGlyph(font, Size, first);

        for (var frame = 0; frame < 8; frame++)
        {
            if (!renderer.BeginOffscreenFrame(black))
            {
                continue;
            }

            renderer.DrawSingleGlyph(font, Size, first, -1, white, 8f, 8f);
            foreach (var r in flood.Skip(frame * 7 % flood.Length).Take(30))
            {
                renderer.DrawSingleGlyph(font, Size, r, -1, white, 8f, 8f);
            }
            renderer.DrawSingleGlyph(font, Size, first, -1, white, 40f, 8f);

            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }

        Assert.Equal(512, renderer.FontAtlas!.AtlasWidth);
        Assert.True(renderer.FontAtlas.EvictionCount >= 2, $"the flood evicted {renderer.FontAtlas.EvictionCount} time(s), expected several");
    };

#if DEBUG
    // A frame that grew the atlas (new image, new set, whole-atlas upload) is refused at submit and takes its
    // recording with it; the frames after must still validate and draw the glyph.
    private static Action RunDroppedFrame(VulkanContext ctx) => () =>
    {
        var font = FontPath;
        var black = new RGBAColor32(0, 0, 0, 255);
        var white = new RGBAColor32(255, 255, 255, 255);
        var first = new Rune('A');
        var flood = Flood();

        using var renderer = new VkRenderer(ctx, Width, Height);
        renderer.OnPreFlush = () => renderer.PreWarmGlyph(font, Size, first);
        var fault = ctx.FaultInjection;

        void Frame(bool flooding)
        {
            if (!renderer.BeginOffscreenFrame(black))
            {
                return;
            }
            renderer.DrawSingleGlyph(font, Size, first, -1, white, 8f, 8f);
            if (flooding)
            {
                foreach (var r in flood)
                {
                    renderer.DrawSingleGlyph(font, Size, r, -1, white, 8f, 8f);
                }
            }
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }

        try
        {
            Frame(flooding: true);                       // fills the atlas, flags the grow
            Assert.Equal(512, renderer.FontAtlas!.AtlasWidth);

            fault.RejectSubmits();                       // this frame grows, flushes the whole atlas, and is refused
            Assert.ThrowsAny<Exception>(() => Frame(flooding: false));
            fault.Clear();
            Assert.True(renderer.FontAtlas.AtlasWidth > 512, "the dropped frame never grew the atlas");

            Frame(flooding: false);                      // re-owes the upload and the first-use clear
            Frame(flooding: true);

            var rgba = ctx.ReadbackOffscreenRgba();
            var lit = 0;
            for (var i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] > 32) lit++;
            }
            Assert.True(lit > 50, $"the glyph did not draw after the dropped frame ({lit} lit pixels)");
        }
        finally
        {
            fault.Clear();
        }
    };
#endif

    private static Action Run(VulkanContext ctx) => () =>
    {
        var font = FontPath;
        var black = new RGBAColor32(0, 0, 0, 255);
        var white = new RGBAColor32(255, 255, 255, 255);
        var first = new Rune('A');
        // Distinct glyphs the atlas has not seen, more than its first size holds at this raster size.
        var flood = Flood();

        using var renderer = new VkRenderer(ctx, Width, Height);

        // Frame 1 warms 'A' before the atlas flush, so it is uploaded this frame and its draw below binds
        // the atlas's descriptor set and records a draw that samples the current image.
        renderer.OnPreFlush = () => renderer.PreWarmGlyph(font, Size, first);

        var grewFrames = 0;
        for (var frame = 0; frame < 4; frame++)
        {
            var widthBefore = renderer.FontAtlas!.AtlasWidth;
            var setBefore = renderer.FontAtlas.DescriptorSet;
            if (!renderer.BeginOffscreenFrame(black))
            {
                continue;
            }

            renderer.DrawSingleGlyph(font, Size, first, -1, white, 8f, 8f);
            // Then misses on every new glyph in the same frame, until the atlas is full and must grow.
            foreach (var r in flood)
            {
                renderer.DrawSingleGlyph(font, Size, r, -1, white, 8f, 8f);
            }

            renderer.EndOffscreenFrame();

            if (renderer.FontAtlas.AtlasWidth != widthBefore)
            {
                // The pins that tell a grow that defers from one that destroys at once, which the validation
                // layer cannot (every frame is waited below, so none is in flight when the atlas grows):
                // the grown atlas has a set of its own, never the context's shared one, and the old image,
                // view, memory and set sit in the deferred queue until their frames retire (read here,
                // before a later frame's start retires them; the grow runs inside this frame's start).
                grewFrames++;
                Assert.NotEqual(setBefore, renderer.FontAtlas.DescriptorSet);
                Assert.NotEqual(ctx.DescriptorSet, renderer.FontAtlas.DescriptorSet);
                // Not a count of the queue: an upload slot buffer replaced in the same frame also queues,
                // which would hide a destroy-at-once Grow. Ask for the grow's own old set instead.
                Assert.True(ctx.IsDeferredDestroyPending(setBefore),
                    "the grown atlas's old descriptor set is not in the deferred queue: the grow destroyed its old image, view, memory and set at once");
            }

            ctx.WaitOffscreenFrameComplete();
        }
        Assert.True(grewFrames > 0, "no frame grew the atlas");

        // The sequence only means something if the atlas did grow under it.
        Assert.True(renderer.FontAtlas!.AtlasWidth > 512, $"the flood never grew the atlas (width {renderer.FontAtlas.AtlasWidth})");
    };
}
