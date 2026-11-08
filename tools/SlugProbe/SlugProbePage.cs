// Explicit usings: the probe has implicit ones, the test project this file is compiled into does not.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using DIR.Lib;
using SdlVulkan.Renderer;
using Vortice.Vulkan;

namespace SlugProbe;

// The page and the measuring of tools/SlugProbe. This file is compiled into BOTH the probe (Program.cs only parses
// options and prints) and the test project (SlugProbeSmokeTests runs it for a few frames), so it uses nothing but the
// renderer's public surface: the probe has no InternalsVisibleTo.

/// <summary>One glyph of the page: the font and glyph id, the pen on the baseline in page pixels (y down), the size in
/// pixels per em, and an index into <see cref="SlugProbePage.Colors"/>.</summary>
internal readonly record struct PlacedGlyph(string Font, uint Gid, float X, float Y, float Size, int Colour);

/// <summary>What one technique (MTSDF or Slug) did with the page.</summary>
/// <param name="Name">"MTSDF" or "Slug".</param>
/// <param name="DrawsPerFrame">Draw calls the page took: Slug one a colour, MTSDF one a colour and atlas page.</param>
/// <param name="Glyphs">Glyph quads drawn a frame (spaces are not drawn).</param>
/// <param name="GpuMedianMs">Median GPU time of a frame (timestamps), NaN when the queue has none.</param>
/// <param name="GpuP95Ms">95th percentile of the same.</param>
/// <param name="WallMedianMs">Median wall-clock time of record, submit and wait, which on a software device is the GPU.</param>
/// <param name="WallP95Ms">95th percentile of the same.</param>
internal sealed record TechniqueReport(string Name, int DrawsPerFrame, int Glyphs, double GpuMedianMs, double GpuP95Ms,
    double WallMedianMs, double WallP95Ms);

/// <summary>The worst number of curves a pixel's two rays walk on one glyph at one size, counted from the blob as the
/// shader walks it.</summary>
/// <param name="Glyph">Which glyph.</param>
/// <param name="PixelsPerEm">The size the pixels were taken at.</param>
/// <param name="Curves">Curves in the glyph.</param>
/// <param name="HBands">Horizontal bands.</param>
/// <param name="VBands">Vertical bands.</param>
/// <param name="WorstPerPixel">Most curves any one pixel reached, the horizontal and the vertical ray together.</param>
/// <param name="WorstPerRay">Most curves either ray alone reached at any pixel.</param>
/// <param name="LongestBandList">The longest curve list of any band: what a ray could walk with no early-out at all.</param>
/// <param name="Pixels">Pixels sampled.</param>
internal sealed record CurveReport(string Glyph, float PixelsPerEm, int Curves, int HBands, int VBands,
    int WorstPerPixel, int WorstPerRay, int LongestBandList, int Pixels);

/// <summary>The first draw of one large glyph, and what it costs once it has been drawn.</summary>
/// <param name="Name">"MTSDF" or "Slug".</param>
/// <param name="SizePx">Pixels per em.</param>
/// <param name="ColdGpuMs">GPU time of the frame that draws the glyph for the first time (its blob, or its atlas cell,
/// not read by any earlier frame), NaN when there are no timestamps.</param>
/// <param name="WarmGpuMedianMs">Median GPU time of the same frame afterwards.</param>
/// <param name="ColdWallMs">Wall time of the cold frame.</param>
/// <param name="WarmWallMedianMs">Median wall time of the warm frames.</param>
internal sealed record ColdReport(string Name, float SizePx, double ColdGpuMs, double WarmGpuMedianMs, double ColdWallMs,
    double WarmWallMedianMs);

/// <summary>What the frames did while a burst of new glyphs missed the Slug store and made it grow.</summary>
/// <param name="GlyphsRequested">New glyphs asked for.</param>
/// <param name="Frames">Frames the burst took to be taken into the buffer.</param>
/// <param name="GrowCount">Times the buffer was replaced by a bigger one during the burst.</param>
/// <param name="CapacityBeforeWords">Buffer capacity before.</param>
/// <param name="CapacityAfterWords">Buffer capacity after.</param>
/// <param name="SteadyWallMedianMs">Median wall time of the page's frames before the burst.</param>
/// <param name="LongestWallMs">Longest wall frame during the burst.</param>
/// <param name="LongestGpuMs">Longest GPU frame during the burst, NaN when there are no timestamps.</param>
/// <param name="Failed">Glyphs the face or the buffer could not take.</param>
internal sealed record BurstReport(int GlyphsRequested, int Frames, int GrowCount, int CapacityBeforeWords,
    int CapacityAfterWords, double SteadyWallMedianMs, double LongestWallMs, double LongestGpuMs, int Failed);

/// <summary>Everything one run measured.</summary>
internal sealed record SlugProbeReport(string Device, bool GpuTiming, uint Width, uint Height, float SizePx, int Frames,
    int Msaa, int Colours, TechniqueReport Mtsdf, TechniqueReport Slug, IReadOnlyList<CurveReport> Curves,
    string DensestGlyph, ColdReport ColdMtsdf, ColdReport ColdSlug, BurstReport Burst, int CjkCells, int CjkDistinct);

/// <summary>
/// One A4-like page of reading-size Latin text (DejaVu Sans) with a heading and a CJK block (the Noto Sans JP subset
/// the tests committed), drawn as persistent MTSDF glyphs and as persistent Slug glyphs on the same offscreen
/// <see cref="VulkanContext"/>, and the measurements issue #162 sets as its bars: draws and GPU time of that page, the
/// worst curve count a pixel walks on the densest CJK glyph, one dense CJK glyph drawn cold at about 300 px, and the
/// longest frame while a burst of new glyphs misses and forces the Slug store to grow. A measurement, not a load
/// test: a few hundred frames.
/// </summary>
internal static class SlugProbePage
{
    /// <summary>Page width in pixels: A4 at 96 dpi.</summary>
    public const uint PageWidth = 794;
    /// <summary>Page height in pixels: A4 at 96 dpi.</summary>
    public const uint PageHeight = 1123;

    /// <summary>Size of the cold large glyph, pixels per em.</summary>
    public const float ColdSizePx = 300f;

    // The page's three colours: body, heading, CJK block. One Slug draw each, however many glyphs and sizes.
    public static readonly RGBAColor32[] Colors =
    [
        new(30, 30, 30, 255),
        new(20, 60, 160, 255),
        new(160, 30, 30, 255),
    ];
    private static readonly RGBAColor32 Paper = new(255, 255, 255, 255);

    // The fixture's three CJK glyphs, dense ones (the tests render them too): what the block falls back to when a face
    // has no cmap entry in the ranges below.
    private static readonly int[] FixtureCjkCodepoints = [0x9B31, 0x9F8D, 0x9A5A];

    // Where the block looks for glyphs, in order: CJK Unified Ideographs, Extension A, then Hiragana and Katakana. The
    // first distinct glyphs the face maps in them fill the block, one a cell, so a face with enough of them gives a block
    // of different glyphs (what a real CJK page has: a store that misses on most of them) rather than a few repeated.
    private static readonly (int First, int Last)[] CjkRanges = [(0x4E00, 0x9FFF), (0x3400, 0x4DBF), (0x3040, 0x30FF)];

    private const string Paragraph =
        "Reading text is made of many small glyphs of a few sizes, each drawn from the same few hundred outlines again and " +
        "again, and a page of it is the load that decides whether a text path is fast enough. The quick brown fox jumps over " +
        "the lazy dog, pack my box with five dozen liquor jugs, and sphinx of black quartz, judge my vow. Numbers 0123456789 " +
        "and punctuation (like this; or that: - – —) fill the lines out the way real documents do. ";

    private sealed record Page(List<PlacedGlyph> Glyphs, int CjkGlyphs, IReadOnlyList<(int Codepoint, uint Gid)> CjkDistinct);

    // ---- the run ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs the whole probe on <paramref name="ctx"/>, an offscreen context (resized to the page). Throws when a technique
    /// never gets its glyphs ready.
    /// </summary>
    /// <param name="frames">Timed frames per technique (a few hundred for a number, a handful for a smoke run).</param>
    /// <param name="sizePx">Body text size in pixels per em; the heading is 1.6 times that, the CJK block the same.</param>
    /// <param name="burst">New glyphs asked for in the growth burst.</param>
    public static unsafe SlugProbeReport Run(VulkanContext ctx, string dejaVuPath, string cjkPath, int frames, float sizePx,
        int burst, int msaa)
    {
        ctx.ResizeOffscreen(PageWidth, PageHeight);
        using var renderer = new VkRenderer(ctx, PageWidth, PageHeight);
        var rasterizer = renderer.GlyphRasterizer ?? throw new InvalidOperationException("the renderer has no glyph rasterizer");

        ctx.InstanceApi.vkGetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var device = VkStringInterop.ConvertToManaged(props.deviceName) ?? "<unknown>";

        var page = LayOutPage(renderer, rasterizer, dejaVuPath, cjkPath, sizePx);

        // MTSDF first, then Slug: each on its own persistent buffers, the page the same.
        var sdfGroups = LayOutSdf(ctx, renderer, page);
        var mtsdf = Time(ctx, renderer, "MTSDF", frames, DrawSdf(renderer, sdfGroups, out var sdfBuffers),
            sdfBuffers);
        var slugLists = LayOutSlug(ctx, renderer, page);
        var slug = Time(ctx, renderer, "Slug", frames, DrawSlug(renderer, slugLists, out var slugBuffers),
            slugBuffers);

        // The worst curve count of the band walk on the densest CJK glyph.
        var (densestCodepoint, densestName, densestBlob) = Densest(rasterizer, cjkPath, page.CjkDistinct);
        var curves = new List<CurveReport>
        {
            MeasureCurves(densestName, densestBlob, sizePx),
            MeasureCurves(densestName, densestBlob, ColdSizePx),
        };

        var coldGlyph = new PlacedGlyph(cjkPath, ResolveGid(rasterizer, cjkPath, densestCodepoint),
            (PageWidth - ColdSizePx) / 2f, (PageHeight + ColdSizePx * 0.8f) / 2f, ColdSizePx, 2);
        var coldMtsdf = Cold(ctx, renderer, "MTSDF", coldGlyph, frames);
        var coldSlug = Cold(ctx, renderer, "Slug", coldGlyph, frames);

        var burstReport = Burst(ctx, renderer, dejaVuPath, slugLists, Math.Max(1, burst), slug.WallMedianMs);

        return new SlugProbeReport(device, ctx.GpuTimingSupported, PageWidth, PageHeight, sizePx, frames, msaa,
            Colors.Length, mtsdf, slug, curves, densestName, coldMtsdf, coldSlug, burstReport,
            page.CjkGlyphs, page.CjkDistinct.Count);
    }

    // ---- the page -----------------------------------------------------------------------------------------------

    private static Page LayOutPage(VkRenderer renderer, ManagedFontRasterizer rasterizer, string latin, string cjk, float size)
    {
        const float margin = 48f;
        var glyphs = new List<PlacedGlyph>();
        var advances = new Dictionary<(char, float), float>();
        float Advance(char ch, float px)
        {
            if (!advances.TryGetValue((ch, px), out var a))
                advances[(ch, px)] = a = renderer.MeasureText(ch.ToString(), latin, px).Width;
            return a;
        }
        uint Gid(char ch) => rasterizer.ResolveGlyphIdentity(latin, new Rune(ch), -1, GlyphMapHint.Auto).Gid;

        // The CJK block is the page's tail: whole rows of glyphs, each in a cell a little wider than the em.
        var cell = size * 1.1f;
        var cols = (int)((PageWidth - 2 * margin) / cell);
        var cjkRows = 12;
        var cjkTop = PageHeight - margin - cjkRows * cell;

        // A heading, then paragraphs wrapped to the measure, down to the CJK block.
        var headingSize = size * 1.6f;
        var y = margin + headingSize;
        PlaceLine("Slug and MTSDF on one page", margin, y, headingSize, 1);
        y += headingSize * 1.6f;

        var lineHeight = size * 1.35f;
        var words = Paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var spaceWidth = Advance(' ', size);
        var wordIndex = 0;
        while (y < cjkTop - lineHeight)
        {
            var x = margin;
            var line = new StringBuilder();
            while (true)
            {
                var word = words[wordIndex % words.Length];
                var width = word.Sum(c => Advance(c, size));
                if (x + width > PageWidth - margin && line.Length > 0) break;
                PlaceWord(word, x, y);
                x += width + spaceWidth;
                line.Append(word).Append(' ');
                wordIndex++;
            }
            y += lineHeight;
        }

        // Each cell gets the next distinct glyph the face has; a face with fewer than there are cells (the three-glyph
        // fixture) starts over, and the report says how many were distinct.
        var cjkGlyphs = 0;
        var distinct = CjkGlyphs(rasterizer, cjk, cjkRows * cols);
        for (var row = 0; row < cjkRows; row++)
            for (var col = 0; col < cols; col++)
            {
                var gid = distinct[(row * cols + col) % distinct.Count].Gid;
                glyphs.Add(new PlacedGlyph(cjk, gid, margin + col * cell, cjkTop + row * cell + size * 0.88f, size, 2));
                cjkGlyphs++;
            }
        return new Page(glyphs, cjkGlyphs, distinct);

        void PlaceWord(string word, float x, float baseline)
        {
            foreach (var ch in word)
            {
                glyphs.Add(new PlacedGlyph(latin, Gid(ch), x, baseline, size, 0));
                x += Advance(ch, size);
            }
        }
        void PlaceLine(string text, float x, float baseline, float px, int colour)
        {
            foreach (var ch in text)
            {
                if (ch != ' ') glyphs.Add(new PlacedGlyph(latin, Gid(ch), x, baseline, px, colour));
                x += Advance(ch, px);
            }
        }
    }

    private static uint ResolveGid(ManagedFontRasterizer rasterizer, string font, int codepoint)
        => rasterizer.ResolveGlyphIdentity(font, new Rune(codepoint), -1, GlyphMapHint.Auto).Gid;

    private static SlugGlyphBlob Encode(ManagedFontRasterizer rasterizer, string font, uint gid)
    {
        if (!new SlugGlyphEncoder().TryEncode(rasterizer, font, new GlyphIdentity(gid, null), out var blob))
            throw new InvalidOperationException($"glyph {gid} of {font} did not encode");
        return blob;
    }

    // Up to `wanted` different glyphs (different gids, so a codepoint two cmap entries share counts once) the face maps in
    // CjkRanges, in codepoint order; the fixture's three when it maps none (a face without CJK would leave no block).
    private static List<(int Codepoint, uint Gid)> CjkGlyphs(ManagedFontRasterizer rasterizer, string cjk, int wanted)
    {
        var found = new List<(int Codepoint, uint Gid)>();
        var seen = new HashSet<uint>();
        foreach (var (first, last) in CjkRanges)
            for (var cp = first; cp <= last && found.Count < wanted; cp++)
            {
                var gid = ResolveGid(rasterizer, cjk, cp);
                if (gid != 0 && seen.Add(gid)) found.Add((cp, gid));
            }
        if (found.Count == 0)
            foreach (var cp in FixtureCjkCodepoints)
                found.Add((cp, ResolveGid(rasterizer, cjk, cp)));
        return found;
    }

    // The block's glyph with the most curves.
    private static (int Codepoint, string Name, SlugGlyphBlob Blob) Densest(ManagedFontRasterizer rasterizer, string cjk,
        IReadOnlyList<(int Codepoint, uint Gid)> candidates)
    {
        var best = candidates
            .Select(c => (c.Codepoint, c.Gid, Blob: Encode(rasterizer, cjk, c.Gid)))
            .MaxBy(c => c.Blob.CurveCount);
        return (best.Codepoint, $"U+{best.Codepoint:X4}", best.Blob);
    }

    // ---- laying the page out per technique -----------------------------------------------------------------------

    // Runs an empty frame, which is when a store or an atlas takes what was asked of it.
    private static void EmptyFrame(VulkanContext ctx, VkRenderer renderer)
    {
        if (!renderer.BeginOffscreenFrame(Paper)) throw new InvalidOperationException("BeginOffscreenFrame refused");
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }

    // Slug: one instance a glyph, one list of instances a colour. Spaces and anything Blank are not drawn.
    private static List<float>[] LayOutSlug(VulkanContext ctx, VkRenderer renderer, Page page)
    {
        var scratch = new float[VkRenderer.SlugInstanceFloats];
        for (var attempt = 0; attempt < 2000; attempt++)
        {
            var lists = Colors.Select(_ => new List<float>()).ToArray();
            var pending = 0;
            foreach (var g in page.Glyphs)
            {
                var kind = renderer.WriteSlugGlyphInstance(g.Font, g.Gid, null, g.X, g.Y, g.Size, 0f, 1f, scratch);
                if (kind == VkRenderer.SlugGlyphLayout.Quad) lists[g.Colour].AddRange(scratch);
                else if (kind == VkRenderer.SlugGlyphLayout.Pending) pending++;
            }
            if (pending == 0) return lists;
            EmptyFrame(ctx, renderer);
            Thread.Sleep(2);
        }
        throw new TimeoutException("the Slug store never delivered the page's glyphs");
    }

    private readonly record struct SdfGroup(int Colour, int AtlasPage, bool Large);

    // MTSDF: one instance a glyph, grouped by colour, atlas page and tier, since one draw samples one page.
    private static Dictionary<SdfGroup, List<float>> LayOutSdf(VulkanContext ctx, VkRenderer renderer, Page page)
    {
        var distinct = page.Glyphs.Select(g => (g.Font, g.Gid, g.Size)).Distinct().ToArray();
        renderer.OnPreFlush = () =>
        {
            foreach (var (font, gid, size) in distinct) renderer.PreWarmSdfGlyphByGid(font, size, gid);
        };
        try
        {
            var scratch = new float[VkRenderer.SdfInstanceFloats];
            for (var attempt = 0; attempt < 2000; attempt++)
            {
                var groups = new Dictionary<SdfGroup, List<float>>();
                var pending = 0;
                foreach (var g in page.Glyphs)
                {
                    var large = renderer.UsesLargeSdfTier(g.Size);
                    var kind = renderer.LayoutSdfGlyph(g.Font, g.Gid, null, g.X, g.Y, g.Size, 0f, 1f, large, scratch,
                        out var atlasPage, out _);
                    if (kind == VkRenderer.SdfGlyphLayout.Refused && large)
                    {
                        large = false;
                        kind = renderer.LayoutSdfGlyph(g.Font, g.Gid, null, g.X, g.Y, g.Size, 0f, 1f, large, scratch,
                            out atlasPage, out _);
                    }
                    if (kind == VkRenderer.SdfGlyphLayout.Quad)
                    {
                        var key = new SdfGroup(g.Colour, atlasPage, large);
                        if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<float>();
                        list.AddRange(scratch);
                    }
                    else if (kind == VkRenderer.SdfGlyphLayout.Pending) pending++;
                }
                if (pending == 0) return groups;
                EmptyFrame(ctx, renderer);
                Thread.Sleep(2);
            }
            throw new TimeoutException("the MTSDF atlas never delivered the page's glyphs");
        }
        finally
        {
            renderer.OnPreFlush = null;
        }
    }

    // A recorded draw of the page: the persistent buffers made once, the draws issued each frame.
    private sealed class PageDraw(Action<VkRenderer> draw, int draws, int glyphs)
    {
        public int Draws { get; } = draws;
        public int Glyphs { get; } = glyphs;
        public void Record(VkRenderer renderer) => draw(renderer);
    }

    private static PageDraw DrawSdf(VkRenderer renderer, Dictionary<SdfGroup, List<float>> groups,
        out List<(Vortice.Vulkan.VkBuffer, VkDeviceMemory)> buffers)
    {
        var made = buffers = new List<(Vortice.Vulkan.VkBuffer, VkDeviceMemory)>();
        var draws = new List<(Vortice.Vulkan.VkBuffer Buffer, uint Count, SdfGroup Group)>();
        var glyphs = 0;
        foreach (var (group, list) in groups)
        {
            var (buffer, memory) = renderer.CreatePersistentVertexBuffer(list.ToArray());
            made.Add((buffer, memory));
            var count = (uint)(list.Count / VkRenderer.SdfInstanceFloats);
            draws.Add((buffer, count, group));
            glyphs += (int)count;
        }
        return new PageDraw(r =>
        {
            foreach (var (buffer, count, group) in draws)
                r.DrawPersistentSdfGlyphs(buffer, 0, count, group.AtlasPage, group.Large, Colors[group.Colour], 0f, 0f, 1f);
        }, draws.Count, glyphs);
    }

    private static PageDraw DrawSlug(VkRenderer renderer, List<float>[] lists,
        out List<(Vortice.Vulkan.VkBuffer, VkDeviceMemory)> buffers)
    {
        var made = buffers = new List<(Vortice.Vulkan.VkBuffer, VkDeviceMemory)>();
        var draws = new List<(Vortice.Vulkan.VkBuffer Buffer, uint Count, int Colour)>();
        var glyphs = 0;
        for (var colour = 0; colour < lists.Length; colour++)
        {
            if (lists[colour].Count == 0) continue;
            var (buffer, memory) = renderer.CreatePersistentVertexBuffer(lists[colour].ToArray());
            made.Add((buffer, memory));
            var count = (uint)(lists[colour].Count / VkRenderer.SlugInstanceFloats);
            draws.Add((buffer, count, colour));
            glyphs += (int)count;
        }
        return new PageDraw(r =>
        {
            foreach (var (buffer, count, colour) in draws)
                r.DrawPersistentSlugGlyphs(buffer, 0, count, Colors[colour], 0f, 0f, 1f);
        }, draws.Count, glyphs);
    }

    // ---- timing --------------------------------------------------------------------------------------------------

    // One frame of `draw`: wall time from before the record to after the completion wait, and the GPU time of that frame
    // (the wait collects its timestamps, so LastGpuFrameMs is this frame's).
    private static (double Wall, double Gpu) TimeFrame(VulkanContext ctx, VkRenderer renderer, Action<VkRenderer> draw)
    {
        var start = Stopwatch.GetTimestamp();
        if (!renderer.BeginOffscreenFrame(Paper)) throw new InvalidOperationException("BeginOffscreenFrame refused");
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return (wall, ctx.LastGpuFrameMs);
    }

    private static TechniqueReport Time(VulkanContext ctx, VkRenderer renderer, string name, int frames,
        PageDraw page, List<(Vortice.Vulkan.VkBuffer, VkDeviceMemory)> buffers)
    {
        try
        {
            var slugBefore = renderer.SlugDrawCalls;
            // Warm: pipelines, the first use of every page and descriptor, the vertex ring.
            for (var i = 0; i < 10; i++) TimeFrame(ctx, renderer, page.Record);
            var walls = new List<double>(frames);
            var gpus = new List<double>(frames);
            for (var i = 0; i < frames; i++)
            {
                var (wall, gpu) = TimeFrame(ctx, renderer, page.Record);
                walls.Add(wall);
                gpus.Add(gpu);
            }
            // The draws a frame: what the page recorded, and for Slug the renderer's own counter agrees.
            var draws = page.Draws;
            if (name == "Slug")
            {
                var counted = renderer.SlugDrawCalls - slugBefore;
                var frameCount = 10 + frames;
                draws = counted / frameCount;
            }
            return new TechniqueReport(name, draws, page.Glyphs, Median(gpus), Percentile(gpus, 0.95),
                Median(walls), Percentile(walls, 0.95));
        }
        finally
        {
            foreach (var (buffer, memory) in buffers) renderer.DestroyBuffer(buffer, memory);
        }
    }

    // NaN-aware: no timestamps means NaN throughout.
    private static double Median(List<double> v) => Percentile(v, 0.5);

    private static double Percentile(List<double> v, double p)
    {
        if (v.Count == 0 || v.Any(double.IsNaN)) return double.NaN;
        var sorted = v.Order().ToArray();
        // Nearest rank.
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    // ---- one cold large glyph --------------------------------------------------------------------------------------

    // The glyph alone on an empty page at ColdSizePx: it is made ready (store or atlas has it) by frames that do not draw
    // it, then the FIRST frame that draws it is timed, then warm ones. That frame is as cold as a GPU lets one be made:
    // nothing has read this glyph's blob (or cell) before it.
    private static ColdReport Cold(VulkanContext ctx, VkRenderer renderer, string name, PlacedGlyph glyph, int frames)
    {
        var one = new Page([glyph], 1, []);
        PageDraw draw;
        List<(Vortice.Vulkan.VkBuffer, VkDeviceMemory)> buffers;
        if (name == "Slug") draw = DrawSlug(renderer, LayOutSlug(ctx, renderer, one), out buffers);
        else draw = DrawSdf(renderer, LayOutSdf(ctx, renderer, one), out buffers);
        try
        {
            var (coldWall, coldGpu) = TimeFrame(ctx, renderer, draw.Record);
            var walls = new List<double>();
            var gpus = new List<double>();
            for (var i = 0; i < Math.Max(5, frames / 4); i++)
            {
                var (wall, gpu) = TimeFrame(ctx, renderer, draw.Record);
                walls.Add(wall);
                gpus.Add(gpu);
            }
            return new ColdReport(name, glyph.Size, coldGpu, Median(gpus), coldWall, Median(walls));
        }
        finally
        {
            foreach (var (buffer, memory) in buffers) renderer.DestroyBuffer(buffer, memory);
        }
    }

    // ---- the growth burst ----------------------------------------------------------------------------------------

    // Each frame draws the page (already in the store) and asks for `perFrame` glyphs the store has never seen, DejaVu's
    // from gid `FirstBurstGid` up. The store encodes them off this thread and takes them into its buffer a budget a frame,
    // growing (a copy, the old buffer released through DeferDestroy) when they do not fit. The longest frame is the stall.
    private const uint FirstBurstGid = 40;
    private const int BurstPerFrame = 48;

    private static BurstReport Burst(VulkanContext ctx, VkRenderer renderer, string latin, List<float>[] pageLists,
        int burst, double steadyWallMedianMs)
    {
        var draw = DrawSlug(renderer, pageLists, out var buffers);
        try
        {
            var before = renderer.SlugStoreStats;
            var scratch = new float[VkRenderer.SlugInstanceFloats];
            var longestWall = 0.0;
            var longestGpu = 0.0;
            var gpuKnown = true;
            var asked = 0;
            var frames = 0;
            while (frames < 4000)
            {
                for (var i = 0; i < BurstPerFrame && asked < burst; i++, asked++)
                    renderer.WriteSlugGlyphInstance(latin, FirstBurstGid + (uint)asked, null, 0f, 0f, 14f, 0f, 1f, scratch);
                var (wall, gpu) = TimeFrame(ctx, renderer, draw.Record);
                frames++;
                longestWall = Math.Max(longestWall, wall);
                if (double.IsNaN(gpu)) gpuKnown = false;
                else longestGpu = Math.Max(longestGpu, gpu);
                if (asked >= burst && !renderer.SlugStoreStats.Pending) break;
                Thread.Sleep(1); // the workers encode on other threads; give them the frame gap a real app has
            }
            var after = renderer.SlugStoreStats;
            return new BurstReport(asked, frames, after.GrowCount - before.GrowCount, before.CapacityWords, after.CapacityWords,
                steadyWallMedianMs, longestWall, gpuKnown ? longestGpu : double.NaN, after.FailedGlyphs - before.FailedGlyphs);
        }
        finally
        {
            foreach (var (buffer, memory) in buffers) renderer.DestroyBuffer(buffer, memory);
        }
    }

    // ---- the curve count --------------------------------------------------------------------------------------------

    /// <summary>
    /// The most curves a pixel reaches on <paramref name="blob"/> drawn at <paramref name="pixelsPerEm"/>, counted from the
    /// blob as slug.frag walks it (and SlugCoverageOracle in the tests): each pixel centre of the glyph's em box, padded a
    /// pixel as the quad is, picks its band on both axes, takes the band's descending or ascending list by its split and
    /// walks it until the first curve wholly behind (ahead of) the pixel's footprint. A curve reached costs one root
    /// solve, so that count is the work the fragment does; the horizontal and the vertical ray together are a pixel's.
    /// </summary>
    internal static CurveReport MeasureCurves(string name, SlugGlyphBlob blob, float pixelsPerEm)
    {
        var w = blob.Words;
        var upem = w[1];
        var scale = 1f / (SlugGlyphEncoder.UnitsPerFontUnit * upem);
        int hBands = w[3], vBands = w[4], bandOffset = w[5];

        var longestList = 0;
        for (var b = 0; b < hBands + vBands; b++)
            longestList = Math.Max(longestList, w[bandOffset + b * SlugGlyphBlob.BandHeaderWords]);

        var pad = 1f / pixelsPerEm;
        var left = blob.MinXEm - pad;
        var top = blob.MaxYEm + pad;
        var cols = (int)Math.Ceiling((blob.MaxXEm - blob.MinXEm + 2 * pad) * pixelsPerEm);
        var rows = (int)Math.Ceiling((blob.MaxYEm - blob.MinYEm + 2 * pad) * pixelsPerEm);

        var worstPixel = 0;
        var worstRay = 0;
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < cols; i++)
            {
                var x = left + (i + 0.5f) / pixelsPerEm;
                var y = top - (j + 0.5f) / pixelsPerEm;
                var h = Walked(w, true, x, y, pixelsPerEm, scale, hBands, vBands, bandOffset);
                var v = Walked(w, false, x, y, pixelsPerEm, scale, hBands, vBands, bandOffset);
                worstPixel = Math.Max(worstPixel, h + v);
                worstRay = Math.Max(worstRay, Math.Max(h, v));
            }
        return new CurveReport(name, pixelsPerEm, blob.CurveCount, hBands, vBands, worstPixel, worstRay, longestList,
            cols * rows);
    }

    // Curves one ray reaches at (sx, sy): slug.frag's axis(), counting the curves that pass its early-out.
    private static int Walked(int[] w, bool horizontal, float sx, float sy, float ppe, float scale, int hBands, int vBands,
        int bandOffset)
    {
        float su = horizontal ? sx : sy, sv = horizontal ? sy : sx;
        var bands = horizontal ? hBands : vBands;
        int crossMin = horizontal ? w[9] : w[8], crossMax = horizontal ? w[11] : w[10];
        var vBlob = sv / (double)scale;
        long span = crossMax - crossMin;
        var band = span <= 0 ? 0 : (int)Math.Clamp(Math.Floor((vBlob - crossMin) * bands / span), 0, bands - 1);

        var header = bandOffset + (horizontal ? band : hBands + band) * SlugGlyphBlob.BandHeaderWords;
        int count = w[header], descStart = w[header + 1], ascStart = w[header + 2], split = w[header + 3];
        var descending = su / (double)scale >= split;
        var start = descending ? descStart : ascStart;
        var mirrored = !descending;

        var walked = 0;
        int a = horizontal ? 0 : 1;
        for (var k = 0; k < count; k++)
        {
            var curve = w[start + k];
            float p1 = w[curve + a] * scale - su, p2 = w[curve + 2 + a] * scale - su, p3 = w[curve + 4 + a] * scale - su;
            if (mirrored)
            {
                p1 = -p1; p2 = -p2; p3 = -p3;
            }
            if (MathF.Max(MathF.Max(p1, p2), p3) * ppe < -0.5f) break;
            walked++;
        }
        return walked;
    }

    // ---- the report as text ----------------------------------------------------------------------------------------

    internal static string Format(SlugProbeReport r)
    {
        static string Ms(double v) => double.IsNaN(v) ? "n/a" : $"{v:F3}";
        var sb = new StringBuilder();
        sb.AppendLine($"device: {r.Device}  (GPU timestamps: {(r.GpuTiming ? "yes" : "no")}, MSAA x{r.Msaa})");
        sb.AppendLine($"page: {r.Width}x{r.Height} px, body {r.SizePx:F1} px, {r.Colours} colours, {r.Frames} timed frames each");
        sb.AppendLine(r.CjkDistinct >= r.CjkCells
            ? $"CJK block: {r.CjkCells} cells, {r.CjkDistinct} distinct glyphs"
            : $"CJK block: {r.CjkCells} cells, only {r.CjkDistinct} distinct glyphs (the face has no more in the CJK ranges, so they repeat; --cjk-font PATH gives it a fuller one)");
        sb.AppendLine();
        sb.AppendLine("same page, persistent buffers:      draws/frame  glyphs   GPU ms median/p95    wall ms median/p95");
        foreach (var t in new[] { r.Mtsdf, r.Slug })
            sb.AppendLine($"  {t.Name,-6}                            {t.DrawsPerFrame,6}  {t.Glyphs,7}   {Ms(t.GpuMedianMs),8}/{Ms(t.GpuP95Ms),-8}   {Ms(t.WallMedianMs),8}/{Ms(t.WallP95Ms)}");
        sb.AppendLine();
        sb.AppendLine($"worst per-pixel curve walk, densest CJK glyph ({r.DensestGlyph}):");
        foreach (var c in r.Curves)
            sb.AppendLine($"  at {c.PixelsPerEm,5:F1} px/em: {c.WorstPerPixel} curves a pixel (worst ray {c.WorstPerRay}, longest band list {c.LongestBandList}; glyph has {c.Curves} curves, {c.HBands}x{c.VBands} bands, {c.Pixels} pixels)");
        sb.AppendLine();
        sb.AppendLine($"one dense CJK glyph ({r.DensestGlyph}) alone at {ColdSizePx:F0} px, first draw vs after:");
        foreach (var c in new[] { r.ColdMtsdf, r.ColdSlug })
            sb.AppendLine($"  {c.Name,-6} GPU ms cold {Ms(c.ColdGpuMs)}, warm median {Ms(c.WarmGpuMedianMs)};  wall ms cold {Ms(c.ColdWallMs)}, warm median {Ms(c.WarmWallMedianMs)}");
        sb.AppendLine();
        var b = r.Burst;
        sb.AppendLine($"Slug burst of {b.GlyphsRequested} new glyphs: {b.Frames} frames, store grew {b.GrowCount}x ({b.CapacityBeforeWords} -> {b.CapacityAfterWords} words), {b.Failed} failed");
        sb.AppendLine($"  longest frame: wall {Ms(b.LongestWallMs)} ms (steady median {Ms(b.SteadyWallMedianMs)} ms), GPU {Ms(b.LongestGpuMs)} ms");
        return sb.ToString();
    }
}
