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
        InkPixels(before).ShouldBeGreaterThan(0, "the scene drew something");
        var cellsBefore = SdfCells(r, ctx);
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
        var cellsAfter = SdfCells(r, ctx);
        // The SDF atlas places a cell where the glyph's async encode happens to land, so the same glyphs may take other
        // cells after the trim, from one run to the next (a fresh renderer as the reference cannot tell: it rolls the
        // same dice). So the cells themselves decide: the same cells, the same pixels; another cell, edge bytes within
        // one level, from the filtering there.
        var (differing, maxDelta) = Diff(before, after);
        var cells = $"SDF cells before [{string.Join(",", cellsBefore)}], after [{string.Join(",", cellsAfter)}]";
        if (cellsBefore.AsSpan().SequenceEqual(cellsAfter))
        {
            differing.ShouldBe(0, $"the same atlas cells draw the same pixels after the trim (max delta {maxDelta}); {cells}");
        }
        else
        {
            maxDelta.ShouldBeLessThanOrEqualTo(1, $"a glyph sampled from another cell moves an edge by a level at most; {cells}");
            differing.ShouldBeLessThan(before.Length / 100, $"only edge bytes of the SDF text may differ; {cells}");
        }
        var back = r.MemoryStats;
        back.BitmapAtlasBytes.ShouldBeGreaterThan(0);
        back.SdfBaseBytes.ShouldBeGreaterThan(0);
        back.SlugStoreBytes.ShouldBeGreaterThan(0);
        back.VertexRingBytes.ShouldBeGreaterThan(0);
    }

    // Where the scene's SDF glyphs sit in the atlas once settled: per glyph its page and its cell (instance floats 6 to
    // 9), read through LayoutSdfGlyph, which never rasterizes, in an empty frame of its own.
    private static float[] SdfCells(VkRenderer r, VulkanContext ctx)
    {
        var rasterizer = r.GlyphRasterizer!;
        var cells = new float[4 * 5];
        var instance = new float[VkRenderer.SdfInstanceFloats];
        r.BeginOffscreenFrame(Black).ShouldBeTrue();
        for (var i = 0; i < 4; i++)
        {
            var gid = rasterizer.ResolveGlyphIdentity(FontPath, new Rune('a' + i), -1, default).Gid;
            r.LayoutSdfGlyph(FontPath, gid, null, 0f, 0f, 24f, 0f, 1f, largeTier: false, instance, out var page, out _)
                .ShouldBe(VkRenderer.SdfGlyphLayout.Quad, "the settled scene's SDF glyphs are in the atlas");
            cells[i * 5] = page;
            instance.AsSpan(6, 4).CopyTo(cells.AsSpan(i * 5 + 1));
        }
        r.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return cells;
    }

    // Pixels in rows [yFrom, yTo) whose colour differs from the black clear. The readback is R, G, B, A and the clear is
    // opaque, so every pixel's alpha byte is 255 and says nothing: only R, G and B count as ink.
    private static int InkPixels(byte[] rgba, int yFrom = 0, int yTo = (int)Height)
    {
        var ink = 0;
        for (var y = Math.Max(0, yFrom); y < Math.Min((int)Height, yTo); y++)
            for (var x = 0; x < (int)Width; x++)
            {
                var i = (y * (int)Width + x) * 4;
                if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0) ink++;
            }
        return ink;
    }

    // The rows the laid-out quads cover, from each instance's corner y (float 1) and its down edge's y (float 5); the
    // glyphs here are not rotated, so that is the quad's whole height.
    private static (int Top, int Bottom) QuadRows(float[] instances, int stride)
    {
        var top = float.MaxValue;
        var bottom = float.MinValue;
        for (var i = 0; i < instances.Length; i += stride)
        {
            top = Math.Min(top, instances[i + 1]);
            bottom = Math.Max(bottom, instances[i + 1] + instances[i + 5]);
        }
        return ((int)MathF.Floor(top), (int)MathF.Ceiling(bottom));
    }

    private static System.Collections.Generic.IEnumerable<string> ChangedAt(float[] a, float[] b) =>
        Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).Select(i => $"{i}:{a[i]}->{b[i]}");

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
        var helloBefore = r.MeasureText("Hello trim", "mem:kept", 18f);
        helloBefore.Width.ShouldBeGreaterThan(0f);
        var stampBefore = r.SdfPageStamp(0, false);
        var epochBefore = r.TrimEpoch;

        // The reference for a text this renderer first measures while trimmed: a fresh renderer, never trimmed, with the
        // same font registered on its own rasterizer.
        (float Width, float Height) anotherFresh;
        using (var fresh = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128))
        {
            fresh.GlyphRasterizer!.RegisterFontFromMemory("mem:kept", File.ReadAllBytes(FontPath)).ShouldBeTrue();
            anotherFresh = fresh.MeasureText("another text", "mem:kept", 18f);
        }
        anotherFresh.Width.ShouldBeGreaterThan(0f);
        anotherFresh.Height.ShouldBeGreaterThan(0f);

        r.Trim().ShouldBeTrue();
        r.TrimEpoch.ShouldBe(epochBefore + 1);
        // The same rasterizer object, not disposed, with the registered font; and MeasureText answers while trimmed:
        // a text already measured from the cache, rebuilding nothing, and one never measured by rebuilding the
        // atlases, to the size a renderer that was never trimmed gives it.
        r.GlyphRasterizer.ShouldBeSameAs(rasterizer);
        r.MeasureText("Hello trim", "mem:kept", 18f).ShouldBe(helloBefore);
        r.MemoryStats.TotalBytes.ShouldBe(0, "a measured text is answered from the cache, with nothing rebuilt");
        var another = r.MeasureText("another text", "mem:kept", 18f);
        another.Width.ShouldBe(anotherFresh.Width, "the rebuilt atlases measure the width a fresh renderer does");
        another.Height.ShouldBe(anotherFresh.Height, "the rebuilt atlases measure the height a fresh renderer does");

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
        InkPixels(ctx.ReadbackOffscreenRgba()).ShouldBeGreaterThan(0, "text in the registered font drew after the trim");
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

    // Lays out SDF glyphs and writes Slug instances, a frame each attempt (the first frame after a trim is the one that
    // rebuilds the atlases), until every one is a quad. Returns the SDF page and its stamp.
    private static (int Page, long Stamp) LayOutPersistent(VkRenderer r, VulkanContext ctx, uint[] sdfGids,
        uint[] slugGids, float[] sdf, float[] slug)
    {
        for (var attempt = 0; attempt < MaxFrames; attempt++)
        {
            r.BeginOffscreenFrame(Black).ShouldBeTrue();
            var page = -1;
            long stamp = -1;
            var done = true;
            for (var i = 0; i < sdfGids.Length; i++)
            {
                var laid = r.LayoutSdfGlyph(FontPath, sdfGids[i], null, 6f + i * 18f, 30f, 24f, 0f, 1f, largeTier: false,
                    sdf.AsSpan(i * VkRenderer.SdfInstanceFloats, VkRenderer.SdfInstanceFloats), out var p, out var st);
                if (laid != VkRenderer.SdfGlyphLayout.Quad) { done = false; continue; }
                if (page >= 0 && p != page) throw new Xunit.Sdk.XunitException("the SDF glyphs landed on two pages");
                page = p;
                stamp = st;
            }
            for (var i = 0; i < slugGids.Length; i++)
                if (r.WriteSlugGlyphInstance(FontPath, slugGids[i], null, 6f + i * 16f, 70f, 20f, 0f, 1f,
                        slug.AsSpan(i * VkRenderer.SlugInstanceFloats, VkRenderer.SlugInstanceFloats))
                    != VkRenderer.SlugGlyphLayout.Quad)
                    done = false;
            r.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            if (done) return (page, stamp);
            Thread.Sleep(2);
        }
        throw new Xunit.Sdk.XunitException("the persistent glyphs never laid out");
    }

    private static byte[] DrawPersistent(VkRenderer r, VulkanContext ctx, Vortice.Vulkan.VkBuffer sdfBuffer, int sdfCount,
        int page, Vortice.Vulkan.VkBuffer slugBuffer, int slugCount)
    {
        r.BeginOffscreenFrame(Black).ShouldBeTrue();
        r.DrawPersistentSdfGlyphs(sdfBuffer, 0, (uint)sdfCount, page, largeTier: false, White, 0f, 0f, 1f);
        r.DrawPersistentSlugGlyphs(slugBuffer, 0, (uint)slugCount, White, 0f, 0f, 1f);
        r.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return ctx.ReadbackOffscreenRgba();
    }

    private static void RunPersistent(VulkanContext ctx)
    {
        ctx.ResizeOffscreen(Width, Height);
        using var r = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128);
        var rasterizer = r.GlyphRasterizer!;
        var sdfGids = "Hab".Select(c => rasterizer.ResolveGlyphIdentity(FontPath, new Rune(c), -1, default).Gid).ToArray();
        var slugGids = "wxy".Select(c => rasterizer.ResolveGlyphIdentity(FontPath, new Rune(c), -1, default).Gid).ToArray();
        var sdf = new float[sdfGids.Length * VkRenderer.SdfInstanceFloats];
        var slug = new float[slugGids.Length * VkRenderer.SlugInstanceFloats];

        // (a, b) Lay out, and keep the SDF page stamp and the trim epoch the instances were written in.
        var (page, stampBefore) = LayOutPersistent(r, ctx, sdfGids, slugGids, sdf, slug);
        stampBefore.ShouldBe(r.SdfPageStamp(page, false));
        var epochBefore = r.TrimEpoch;
        // The SDF row lies above the Slug row, so the ink of each is counted on its own.
        var sdfRows = QuadRows(sdf, VkRenderer.SdfInstanceFloats);
        var slugRows = QuadRows(slug, VkRenderer.SlugInstanceFloats);
        sdfRows.Bottom.ShouldBeLessThanOrEqualTo(slugRows.Top, $"the SDF rows {sdfRows} lie above the Slug rows {slugRows}");

        // (c) Draw both from persistent buffers. Each kind must have drawn ink of its own, or the comparison after the
        // trim would hold a blank row against a blank row.
        var sdfBuf = r.CreatePersistentVertexBuffer(sdf);
        var slugBuf = r.CreatePersistentVertexBuffer(slug);
        byte[] first;
        try
        {
            first = DrawPersistent(r, ctx, sdfBuf.Buffer, sdfGids.Length, page, slugBuf.Buffer, slugGids.Length);
            InkPixels(first, sdfRows.Top, sdfRows.Bottom).ShouldBeGreaterThan(0, $"the persistent SDF glyphs drew in rows {sdfRows}");
            InkPixels(first, slugRows.Top, slugRows.Bottom).ShouldBeGreaterThan(0, $"the persistent Slug glyphs drew in rows {slugRows}");
        }
        finally
        {
            r.DestroyBuffer(sdfBuf.Buffer, sdfBuf.Memory);
            r.DestroyBuffer(slugBuf.Buffer, slugBuf.Memory);
        }

        // (d, e) Trim: the saved stamp and epoch no longer match, so a caller holding them sees its instances are stale.
        r.Trim().ShouldBeTrue();
        r.TrimEpoch.ShouldNotBe(epochBefore, "a trim changes the epoch a Slug instance was written in");
        r.SdfPageStamp(page, false).ShouldNotBe(stampBefore, "a trim changes the stamp an SDF instance was laid out with");

        // (f) Lay out and write again, in the rebuilt atlas and store, and draw.
        var sdfFirst = sdf.ToArray();
        var slugFirst = slug.ToArray();
        Array.Clear(sdf);
        Array.Clear(slug);
        var (pageAfter, stampAfter) = LayOutPersistent(r, ctx, sdfGids, slugGids, sdf, slug);
        stampAfter.ShouldNotBe(stampBefore);
        stampAfter.ShouldBe(r.SdfPageStamp(pageAfter, false));
        sdfBuf = r.CreatePersistentVertexBuffer(sdf);
        slugBuf = r.CreatePersistentVertexBuffer(slug);
        byte[] again;
        try
        {
            again = DrawPersistent(r, ctx, sdfBuf.Buffer, sdfGids.Length, pageAfter, slugBuf.Buffer, slugGids.Length);
        }
        finally
        {
            r.DestroyBuffer(sdfBuf.Buffer, sdfBuf.Memory);
            r.DestroyBuffer(slugBuf.Buffer, slugBuf.Memory);
        }

        // (g) The same pixels as the first draw. Where each glyph lands is the same arithmetic before and after, so the
        // quad (floats 0 to 5) and the size and field scale (10, 11) match exactly. The SDF atlas, though, places a cell
        // where the glyph's async encode happens to land in the first layout, while after the trim the glyphs come back
        // in request order, so a glyph may sample another cell (floats 6 to 9), and the filtering there can move an edge
        // byte by a level. So: the same cells, the same pixels; another cell, edge bytes within one level.
        var sdfDiff = ChangedAt(sdfFirst, sdf).ToArray();
        var cellMoved = Enumerable.Range(0, sdfFirst.Length).Any(i => i % VkRenderer.SdfInstanceFloats is >= 6 and <= 9 && sdfFirst[i] != sdf[i]);
        Enumerable.Range(0, sdfFirst.Length).Where(i => i % VkRenderer.SdfInstanceFloats is < 6 or > 9 && sdfFirst[i] != sdf[i])
            .ShouldBeEmpty($"the SDF quads lie where they lay before the trim [{string.Join(",", sdfDiff)}]");
        var (differing, maxDelta) = Diff(first, again);
        var detail = $"SDF instance floats changed at [{string.Join(",", sdfDiff)}], Slug at [{string.Join(",", ChangedAt(slugFirst, slug))}], "
            + $"bytes differ at [{string.Join(",", Enumerable.Range(0, first.Length).Where(i => first[i] != again[i]).Take(32).Select(i => $"({i / 4 % Width},{i / 4 / Width}).{i % 4}"))}]";
        if (!cellMoved)
        {
            differing.ShouldBe(0, $"the same atlas cells draw the same pixels after the trim (max delta {maxDelta}); {detail}");
        }
        else
        {
            maxDelta.ShouldBeLessThanOrEqualTo(1, $"a glyph sampled from another cell moves an edge by a level at most; {detail}");
            differing.ShouldBeLessThan(first.Length / 100, $"only edge bytes of the SDF glyphs may differ; {detail}");
        }
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
            Assert.True(errors.Length == 0, $"the validation layer reported {errors.Length} error(s):\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }

    [Fact]
    public void Persistent_glyphs_are_detectably_stale_after_a_trim_and_draw_the_same_once_laid_out_again()
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }
        ValidatedOffscreen.Messages.Clear();
        try
        {
            RunPersistent(ctx!);
            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0, $"the validation layer reported {errors.Length} error(s):\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }
}
