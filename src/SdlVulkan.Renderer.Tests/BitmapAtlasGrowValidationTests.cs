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
    public async Task A_grow_while_a_frame_records_after_a_drawn_glyph_leaves_the_frame_valid()
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
            var run = Task.Run(Run(ctx!), TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(run, Task.Delay(Deadman, TestContext.Current.CancellationToken)) == run;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: the bitmap atlas flood did not finish within {Deadman.TotalSeconds:0}s. " +
                            $"Validation messages so far:\n{ValidatedOffscreen.DumpMessages()}");
            }
            await run;

            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0,
                $"the validation layer reported {errors.Length} error(s) while the bitmap atlas grew under a " +
                $"recording frame:\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            if (!wedged)
            {
                ValidatedOffscreen.Destroy(ctx, messenger, api);
            }
        }
    }

    private static Action Run(VulkanContext ctx) => () =>
    {
        var font = FontPath;
        var black = new RGBAColor32(0, 0, 0, 255);
        var white = new RGBAColor32(255, 255, 255, 255);
        var first = new Rune('A');
        // Distinct glyphs the atlas has not seen, more than its first size holds at this raster size.
        var flood = Enumerable.Range('B', 25).Concat(Enumerable.Range('a', 26)).Concat(Enumerable.Range('0', 10))
            .Select(c => new Rune((char)c)).ToArray();

        using var renderer = new VkRenderer(ctx, Width, Height);

        // Frame 1 warms 'A' before the atlas flush, so it is uploaded this frame and its draw below binds
        // the atlas's descriptor set and records a draw that samples the current image.
        renderer.OnPreFlush = () => renderer.PreWarmGlyph(font, Size, first);

        for (var frame = 0; frame < 4; frame++)
        {
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
            ctx.WaitOffscreenFrameComplete();
        }

        // The sequence only means something if the atlas did grow under it.
        Assert.True(renderer.FontAtlas!.AtlasWidth > 512, $"the flood never grew the atlas (width {renderer.FontAtlas.AtlasWidth})");
    };
}
