using System.Collections.Concurrent;
using System.Text;
using DIR.Lib;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// SDF-based font atlas using R8_Unorm single-channel textures.
/// Glyphs are rasterized as signed distance fields — the fragment shader
/// reconstructs crisp anti-aliased edges at any scale via smoothstep.
/// </summary>
internal sealed unsafe class VkSdfFontAtlas : IDisposable
{
    // Glyphs are keyed by resolved identity (A1): the numeric glyph id for OpenType fonts
    // (Name == null), or the PostScript glyph name for Type1/PFB fonts (Gid == 0, no numeric
    // ids). Codepoint→identity is resolved once at the GetGlyph boundary via
    // ManagedFontRasterizer.ResolveGlyphIdentity — so the old (charCode, character) CID
    // workaround collapses: two codepoints that resolve to the same glyph now share one entry.
    private readonly record struct GlyphKey(string Font, float Size, uint Gid, string? Name);

    // Sentinel Gid for whitespace, which carries no ink and derives its advance from the 'n'
    // reference glyph. Kept off gid 0 (the shared notdef/blank entry for genuinely-missing
    // glyphs) and off any real glyph id (< NumGlyphs) so whitespace never collides with either.
    private const uint WhitespaceGid = uint.MaxValue;

    internal readonly record struct GlyphInfo(float U0, float V0, float U1, float V1,
        int Width, int Height, float AdvanceX, int BearingX, int BearingY, float Spread);

    private readonly VulkanContext _ctx;
    private readonly ManagedFontRasterizer _rasterizer;
    private readonly Dictionary<GlyphKey, GlyphInfo> _glyphs = new();
    private readonly HashSet<GlyphKey> _unflushedGlyphs = new();

    // Optional disk-persistent cache of SDF bitmaps. When supplied, every freshly
    // rasterized glyph is appended to disk, and the first GetGlyph/PreRasterizeBatch
    // call for a font bulk-loads its existing entries — making re-opens of the same
    // document near-instant after the first session.
    private readonly SdfGlyphDiskCache? _diskCache;
    private readonly HashSet<string> _diskLoadedFonts = new();
    // Completed background .sdfg reads awaiting render-thread insertion (DrainPendingDiskLoads).
    // Keeps the (potentially ~100ms) synchronous disk read off the render thread.
    private readonly ConcurrentQueue<(string Font, IReadOnlyList<DiskGlyphEntry> Entries)> _pendingDiskLoads = new();

    // Async SDF rasterization. The ~10ms-per-glyph distance-field computation runs OFF the render
    // thread: PreRasterizeBatch (and a draw-path miss) claims a key in _rasterizeInFlight, a background
    // task rasterizes it, and the finished bitmap lands in _pendingRasterized for the render thread to
    // insert — bounded — in BeginFrame. This keeps the render loop responsive on glyph-heavy (CJK) docs
    // where the old synchronous prewarm stalled a frame for seconds; glyphs now fill in progressively
    // over a handful of frames. _rasterizeInFlight dedups: the visible-glyph set is re-offered every
    // frame, so a key must be rasterized at most once.
    private readonly ConcurrentDictionary<GlyphKey, byte> _rasterizeInFlight = new();
    // The key already carries the resolved identity, so the finished bitmap needs nothing else
    // to be inserted and persisted (identity → gid/name lives in Key).
    private readonly ConcurrentQueue<(GlyphKey Key, MtsdfGlyphBitmap Bitmap)> _pendingRasterized = new();
    // Max glyphs inserted (staging blit + dirty-region upload) per frame from the rasterized queue.
    // Bounds per-frame upload so a 2000-glyph CJK page drains over ~frames (IsDirty keeps the loop
    // awake until empty), never in one stall.
    private const int MaxGlyphInsertsPerFrame = 96;

    // Upper bound for a page dimension, clamped to the device limit in the ctor. Pages don't grow
    // (a full page is left in place and a new one appended), so this only caps the initial page dim;
    // the default (DefaultInitialAtlasDim = 1024) sits well under it. Working-set headroom comes from
    // MaxPages, not page size. Kept generous so an unusually large initialWidth is still device-safe.
    private const int MaxAtlasSize = 8192;
    // MaxAtlasSize clamped to the device limit (set in the ctor). Use this, not the const, for grows.
    private readonly int _maxAtlasSize;
    private const float SdfSpread = 4f;

    // Bytes per atlas texel. The atlas stores MTSDF (RGBA8): RGB = per-channel
    // pseudo-distance, A = true distance. Every staging/upload byte offset is
    // scaled by this. (Was 1 when the atlas was single-channel R8.)
    private const int BytesPerTexel = 4;

    /// <summary>
    /// MTSDF glyphs are rasterized at this fixed size. The GPU scales the quad
    /// for any requested display size. Because a distance field encodes distance,
    /// not pixels, a single rasterization looks sharp at all display sizes — and
    /// MTSDF keeps corners sharp too, so a smaller raster costs far less fidelity
    /// than it would for plain SDF. 64px with 4-channel RGBA is memory-neutral vs
    /// the old 128px single-channel R8 (¼ the page area × 4 bytes = same bytes per
    /// glyph, same page size, same page cap).
    /// </summary>
    private const float SdfRasterSize = 64f;

    /// <summary>
    /// Default page dimension. The atlas is a list of fixed-size square pages of this size;
    /// when a page fills, a NEW page is appended (no realloc, no GPU drain, no re-upload), so
    /// this is the placement granularity, not a glyph cap. Power-of-two so a glyph's page index
    /// can be recovered exactly from its virtual V coordinate (see <see cref="InsertRasterized"/>
    /// and <see cref="DecodePage"/>).
    /// </summary>
    private const int DefaultInitialAtlasDim = (int)SdfRasterSize * 16; // 1024 (64px raster × 16)

    // Max resident pages before falling back to evict-all. 16 × 1024² × 4 bytes (RGBA) ≈ 64 MB worst
    // case — identical to the old 16 × 2048² × 1 byte R8 atlas (the 64px raster shrinks pages to 1024²,
    // the RGBA format restores the 4× per-texel, so per-glyph capacity and the memory cap are unchanged).
    // 8 was tight for glyph-heavy docs (many embedded subset fonts, or CJK with thousands of unique
    // glyphs): the atlas filled every page and then EvictAll thrashed — a vkDeviceWaitIdle drain plus
    // a full glyph re-raster on each scroll (the jank). 16 roughly doubles the working set first.
    private const int MaxPages = 16;

    /// <summary>One physical SDF page texture + its bookkeeping. Pages are never reallocated:
    /// a full page is left in place and a new page appended. That is what makes growth free —
    /// existing pages stay uploaded and bound; only new glyphs land on a fresh page.</summary>
    private sealed class Page
    {
        public VkImage Image;
        public VkDeviceMemory ImageMemory;
        public VkImageView ImageView;
        public VkDescriptorSet DescriptorSet;
        public byte[] Staging = [];                       // BytesPerTexel per pixel (RGBA), _pageDim²
        public int CursorX, CursorY, RowHeight;
        public int DirtyX0, DirtyY0, DirtyX1, DirtyY1;
        // LRU bookkeeping for per-page eviction. LastUsedFrame is stamped whenever a glyph on this
        // page is drawn (GetGlyph hit) or inserted; Keys lists the glyphs placed here so eviction can
        // drop exactly this page's entries from _glyphs without scanning the whole dictionary.
        public long LastUsedFrame;
        public readonly List<GlyphKey> Keys = new();
        // Image starts UNDEFINED; the first FlushPage transitions from there, not ShaderReadOnly.
        public bool NeedsInitialTransition;
        // Per-frame upload ring — slot k reused only after BeginFrame waited on its fence.
        public readonly VkBuffer[] UploadBuffers = new VkBuffer[VulkanContext.MaxFramesInFlight];
        public readonly VkDeviceMemory[] UploadMemories = new VkDeviceMemory[VulkanContext.MaxFramesInFlight];
        public readonly ulong[] UploadSizes = new ulong[VulkanContext.MaxFramesInFlight];
        // Persistently mapped pointer per slot (HostCoherent memory — legal to keep mapped
        // for the buffer's lifetime; vkFreeMemory unmaps implicitly).
        public readonly IntPtr[] UploadMapped = new IntPtr[VulkanContext.MaxFramesInFlight];
    }

    private readonly List<Page> _pages = new();
    private int _pageDim;          // fixed square page size (power of two)
    private bool _needsEviction;
    private VkSampler _sampler;    // shared across all pages (identical params)
    private long _frameTick;       // monotonic, ++ each BeginFrame — drives per-page LRU
    private int _activePageIdx;    // page currently being filled (last appended OR last LRU-reused)

    public int PageCount => _pages.Count;
    public VkDescriptorSet GetPageDescriptorSet(int pageIndex) => _pages[pageIndex].DescriptorSet;
    public VkSampler Sampler => _sampler;

    public bool IsDirty
    {
        get
        {
            if (_needsEviction) return true;
            // Pending async rasterization (or not-yet-inserted results) means the page isn't final —
            // report dirty so the event loop keeps redrawing and the deferred glyphs pop in. Same for
            // bounded disk-load tails still draining over frames.
            if (!_pendingRasterized.IsEmpty || !_rasterizeInFlight.IsEmpty || !_pendingDiskLoads.IsEmpty) return true;
            foreach (var p in _pages)
                if (p.DirtyX0 < p.DirtyX1 && p.DirtyY0 < p.DirtyY1) return true;
            return false;
        }
    }

    /// <summary>Recover which page a glyph lives on and its page-local V range from the virtual V
    /// encoded at insert time (V is normalized over the MaxPages-tall virtual stack). Exact while
    /// <see cref="_pageDim"/> and <see cref="MaxPages"/> are powers of two. U is already page-local.</summary>
    public void DecodePage(in GlyphInfo g, out int page, out float localV0, out float localV1)
    {
        var vy = g.V0 * MaxPages;
        page = (int)vy;
        localV0 = vy - page;
        localV1 = g.V1 * MaxPages - page;
    }

    public VkSdfFontAtlas(VulkanContext ctx, ManagedFontRasterizer rasterizer,
        SdfGlyphDiskCache? diskCache = null,
        int initialWidth = DefaultInitialAtlasDim, int initialHeight = DefaultInitialAtlasDim)
    {
        _ctx = ctx;
        _rasterizer = rasterizer;
        _diskCache = diskCache;
        // Page dimension = the requested size, clamped to the device limit and rounded DOWN to a
        // power of two (so DecodePage's (int)(V*MaxPages) recovers the page index exactly).
        // initialHeight is ignored — pages are square _pageDim. The atlas never grows the page;
        // it appends new pages instead, so this is granularity, not a cap.
        _maxAtlasSize = Math.Min(MaxAtlasSize, (int)ctx.MaxImageDimension2D);
        _pageDim = PrevPowerOfTwo(Math.Min(initialWidth, _maxAtlasSize));

        CreateSampler();     // shared by every page
        AllocateNewPage();   // start with one page
    }

    private static int PrevPowerOfTwo(int n)
    {
        var p = 1;
        while (p * 2 <= n) p *= 2;
        return p;
    }

    /// <summary>Append a fresh page: allocate its image + descriptor set, no GPU drain. This is the
    /// "grow" replacement — O(1), never touches existing pages.</summary>
    private Page AllocateNewPage()
    {
        var page = new Page
        {
            Staging = new byte[_pageDim * _pageDim * BytesPerTexel],
            DirtyX0 = _pageDim, DirtyY0 = _pageDim, DirtyX1 = 0, DirtyY1 = 0,
        };
        CreateImage(page, _pageDim, _pageDim);
        page.DescriptorSet = _ctx.AllocateDescriptorSet();
        _ctx.UpdateDescriptorSet(page.DescriptorSet, page.ImageView, _sampler);
        _pages.Add(page);
        _activePageIdx = _pages.Count - 1;
        page.LastUsedFrame = _frameTick;
        RenderDiag.Log("sdf.newpage", $"page {_pages.Count - 1} allocated {_pageDim}x{_pageDim}");
        return page;
    }

    private void DestroyPage(Page p)
    {
        var api = _ctx.DeviceApi;
        _ctx.FreeDescriptorSet(p.DescriptorSet);
        for (var i = 0; i < VulkanContext.MaxFramesInFlight; i++)
        {
            if (p.UploadBuffers[i] != VkBuffer.Null)
            {
                api.vkDestroyBuffer(p.UploadBuffers[i]);
                api.vkFreeMemory(p.UploadMemories[i]);
            }
        }
        api.vkDestroyImageView(p.ImageView);
        api.vkDestroyImage(p.Image);
        api.vkFreeMemory(p.ImageMemory);
    }

    public void BeginFrame()
    {
        _frameTick++;
        if (_needsEviction)
        {
            // All pages were hot last frame (working set genuinely > MaxPages in one frame — rare).
            // Per-page LRU couldn't free a safe page, so fall back to the full wipe.
            EvictAll();
            _needsEviction = false;
        }
        DrainPendingDiskLoads();
        DrainPendingRasterized();
    }

    // Inserts up to MaxGlyphInsertsPerFrame background-rasterized glyphs into the atlas. Render-thread
    // only (InsertRasterized mutates atlas/staging/cursor). Bounded per frame so the staging blit +
    // dirty-region upload never spike; the remainder drains on later frames (IsDirty keeps the loop
    // awake). Newly inserted glyphs are persisted to disk here (the background task only rasterizes).
    private void DrainPendingRasterized()
    {
        var inserted = 0;
        while (inserted < MaxGlyphInsertsPerFrame && _pendingRasterized.TryDequeue(out var r))
        {
            _rasterizeInFlight.TryRemove(r.Key, out _);
            if (_glyphs.ContainsKey(r.Key)) continue;       // raced with a disk load / duplicate
            var info = InsertRasterized(r.Key, r.Bitmap);
            if (info.Width > 0)
                _diskCache?.AppendGlyph(r.Key.Font, r.Key.Gid, r.Key.Name, in r.Bitmap);
            else if (!_needsEviction)
                // Genuinely blank glyph (empty SDF — InsertRasterized doesn't record those). Cache a
                // zero sentinel so the draw path / prewarm don't re-queue it every frame — otherwise
                // _rasterizeInFlight never settles and IsDirty pins the loop in a redraw busy-spin.
                _glyphs[r.Key] = default;
            inserted++;
            // Page cap hit (InsertRasterized set _needsEviction): stop; BeginFrame evicts next frame
            // and the remaining queued glyphs (or their re-requests) land afterwards.
            if (_needsEviction) break;
        }
    }

    // Inserts glyphs whose .sdfg read completed on a background thread (see EnsureFontLoadedFromDisk).
    // Render-thread only — InsertRasterized mutates atlas/staging/cursor state.
    private void DrainPendingDiskLoads()
    {
        // Bounded exactly like DrainPendingRasterized: at most MaxGlyphInsertsPerFrame inserts (and
        // therefore one bounded staging blit + dirty-region upload) per frame. A big .sdfg holds
        // thousands of glyphs; loading them all in one frame is a tens-of-MB single-command-buffer
        // upload that wedges the GPU. The tail re-queues and drains over the next frames (IsDirty keeps
        // the loop awake until empty), so the worst case is a brief progressive fill, never a stall.
        var budget = MaxGlyphInsertsPerFrame;
        while (budget > 0 && _pendingDiskLoads.TryDequeue(out var load))
        {
            var i = 0;
            var atlasFull = false;
            for (; i < load.Entries.Count && budget > 0; i++)
            {
                var e = load.Entries[i];
                var key = new GlyphKey(load.Font, SdfRasterSize, e.Gid, e.Name);
                if (_glyphs.ContainsKey(key)) continue;
                InsertRasterized(key, e.Bitmap);
                budget--;
                if (_needsEviction)
                {
                    // Atlas filled and nothing cold could be recycled. Do NOT EvictAll to cram in the
                    // rest — a big .sdfg holds far more glyphs than any view shows, and EvictAll clears
                    // _diskLoadedFonts → the font reloads → refills → wipes again (the on-open busy-spin).
                    // Drop this font's remaining cached glyphs; the ones actually drawn fault in on
                    // demand via the draw path, where per-page LRU keeps just the working set resident.
                    _needsEviction = false;
                    atlasFull = true;
                    i++;
                    RenderDiag.Log("sdf.diskload", $"{load.Font}: atlas full at entry {i}/{load.Entries.Count} — rest on demand");
                    break;
                }
            }
            // Hit the per-frame budget mid-font (not full): resume the unprocessed tail next frame.
            if (!atlasFull && i < load.Entries.Count)
            {
                var tail = new List<DiskGlyphEntry>(load.Entries.Count - i);
                for (var j = i; j < load.Entries.Count; j++) tail.Add(load.Entries[j]);
                _pendingDiskLoads.Enqueue((load.Font, tail));
            }
        }
    }

    /// <summary>
    /// Returns the scale factor between the requested fontSize and the SDF raster size.
    /// </summary>
    public static float GetGlyphScale(float requestedFontSize) => requestedFontSize / SdfRasterSize;

    public GlyphInfo GetGlyph(string fontPath, float fontSize, Rune character,
        bool skipUnflushed = false, int charCode = -1, GlyphMapHint hint = GlyphMapHint.Auto,
        bool rasterizeOnMiss = true)
    {
        // First-time use of a font with a disk cache configured: bulk-import every
        // previously-rasterized glyph for it. Idempotent and noop after the first call.
        EnsureFontLoadedFromDisk(fontPath);

        // Resolve (character, charCode, hint) → glyph identity once, here at the draw boundary,
        // and key by it. All SDF glyphs are rasterized at SdfRasterSize; the caller scales the quad.
        var key = MakeKey(fontPath, character, charCode, hint);
        if (_glyphs.TryGetValue(key, out var existing))
        {
            // Touch the page this glyph lives on so per-page LRU keeps the on-screen working set
            // resident and only evicts pages that have gone cold (Width==0 is the blank sentinel —
            // it has no page).
            if (existing.Width > 0)
            {
                DecodePage(existing, out var pg, out _, out _);
                if ((uint)pg < (uint)_pages.Count) _pages[pg].LastUsedFrame = _frameTick;
            }
            if (skipUnflushed && _unflushedGlyphs.Contains(key))
                return existing with { Width = 0 };
            return existing;
        }
        if (!rasterizeOnMiss)
        {
            // Draw path: NEVER rasterize on the render thread. Queue the glyph for background
            // rasterization (deduped) and skip drawing it this frame — it appears once the
            // background result is inserted (DrainPendingRasterized). Width==0 -> caller skips it.
            // Whitespace carries no ink and is warmed synchronously by PreRasterizeBatch, so it
            // never needs queuing here.
            if (!Rune.IsWhiteSpace(character) && _rasterizeInFlight.TryAdd(key, 0))
                QueueRasterizeAsync(key);
            return default;
        }
        var result = RasterizeGlyph(key);
        if (skipUnflushed && result.Width > 0)
            return result with { Width = 0 };
        return result;
    }

    // Resolve a (character, PDF charCode, cmap hint) request to the atlas identity key: the glyph
    // id for OpenType fonts, the glyph name for Type1/PFB. Whitespace has no ink and shares one
    // sentinel key (its advance derives from the 'n' reference glyph in RasterizeGlyph).
    private GlyphKey MakeKey(string fontPath, Rune character, int charCode, GlyphMapHint hint)
    {
        if (Rune.IsWhiteSpace(character))
            return new GlyphKey(fontPath, SdfRasterSize, WhitespaceGid, null);
        var id = _rasterizer.ResolveGlyphIdentity(fontPath, character, charCode, hint);
        return new GlyphKey(fontPath, SdfRasterSize, id.Gid, id.Type1Name);
    }

    // Rasterize a glyph to MTSDF by its resolved identity: Type1 by glyph name, otherwise by GID.
    // ManagedFontRasterizer is thread-safe, so this runs on both the render thread (RasterizeGlyph)
    // and background rasterize tasks (QueueRasterizeAsync / PreRasterizeBatch).
    private MtsdfGlyphBitmap RasterizeByKey(GlyphKey key) =>
        key.Name is not null
            ? _rasterizer.RasterizeGlyphMtsdfByType1Name(key.Font, key.Size, key.Name, SdfSpread)
            : _rasterizer.RasterizeGlyphMtsdfByGid(key.Font, key.Size, key.Gid, SdfSpread);

    // Rasterize one glyph on a background thread and enqueue the result for render-thread insertion.
    // Caller must have already claimed the key in _rasterizeInFlight. Used for the rare draw-path miss
    // (a glyph the per-frame prewarm batch didn't cover); the bulk path is PreRasterizeBatch.
    private void QueueRasterizeAsync(GlyphKey key)
    {
        Task.Run(() =>
        {
            try
            {
                _pendingRasterized.Enqueue((key, RasterizeByKey(key)));
            }
            catch (Exception ex)
            {
                // Don't leave the key permanently claimed if rasterization throws — release it so a
                // later frame can retry; otherwise the glyph would never appear.
                _rasterizeInFlight.TryRemove(key, out _);
                Console.Error.WriteLine($"[SdfAtlas] async rasterize failed for {DescribeKey(key)}: {ex.Message}");
            }
        });
    }

    // Human-readable identity for diagnostics: "gid N" for OpenType, "'name'" for Type1.
    private static string DescribeKey(GlyphKey key) => key.Name is not null ? $"'{key.Name}'" : $"gid {key.Gid}";

    /// <summary>
    /// If a disk cache is configured and we haven't yet imported <paramref name="fontPath"/>'s
    /// entries this session, read every cached SDF bitmap for the font and insert it into
    /// the atlas. Each loaded glyph goes through <see cref="InsertRasterized"/> — same
    /// path a freshly rasterized one would take — so it shows up in <c>_unflushedGlyphs</c>
    /// for the next <see cref="Flush"/> just like a runtime rasterization would. Loaded
    /// entries are NOT re-appended to disk (they're already there).
    /// </summary>
    private void EnsureFontLoadedFromDisk(string fontPath)
    {
        if (_diskCache is null) return;
        if (_diskLoadedFonts.Contains(fontPath)) return;
        // Don't commit the "loaded" guard until the cache can actually resolve this font's hash. For a
        // "mem:" subset font that means RegisterMemoryFont must have run first; if it hasn't yet (the
        // resolver registers during parse, which can race the first glyph use), bail WITHOUT marking —
        // so a later call retries once the font is registered. Previously we marked unconditionally,
        // so a single premature call permanently blocked the disk load → every glyph re-rasterized and
        // re-appended each session (the 2.7× .sdfg duplication + a needless ~1s cold rasterize pass).
        if (!_diskCache.HasHashFor(fontPath)) return;
        _diskLoadedFonts.Add(fontPath);

        // Read + deserialize the .sdfg on a BACKGROUND thread — a large/old UI-font cache can take
        // ~100ms, and that must never block the render thread. The decoded entries come back via
        // _pendingDiskLoads and are inserted into the atlas on the render thread by
        // DrainPendingDiskLoads(). Glyphs needed before the read lands are rasterized on-demand and
        // de-duped (by key) when the batch arrives.
        var cache = _diskCache;
        Task.Run(() =>
        {
            try
            {
                var entries = cache.LoadEntriesForFont(fontPath);
                if (entries.Count > 0) _pendingDiskLoads.Enqueue((fontPath, entries));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SdfDiskCache] async load failed for {fontPath}: {ex.Message}");
            }
        });
    }

    public void Flush(VkCommandBuffer cmd)
    {
        // Pick this frame's upload slot. BeginFrame has already waited on the fence that guards
        // this slot's last submit, so the GPU is done with each page's slot-k upload buffer.
        var slot = _ctx.CurrentFrame;
        var any = false;
        foreach (var page in _pages)
            any |= FlushPage(cmd, page, slot);
        // A glyph is "unflushed" until its page is uploaded; the loop flushes every dirty page in
        // this one command buffer, so once it returns all pages are current.
        if (any) _unflushedGlyphs.Clear();
    }

    private bool FlushPage(VkCommandBuffer cmd, Page page, int slot)
    {
        if (page.DirtyX0 >= page.DirtyX1 || page.DirtyY0 >= page.DirtyY1)
            return false;

        var regionW = page.DirtyX1 - page.DirtyX0;
        var regionH = page.DirtyY1 - page.DirtyY0;
        var pixelCount = regionW * regionH;
        var rowBytes = regionW * BytesPerTexel;

        var bufferSize = (ulong)(pixelCount * BytesPerTexel);
        EnsureUploadBuffer(page, slot, bufferSize);

        // Copy the dirty region (BytesPerTexel per pixel) row-by-row straight into the persistently
        // mapped upload buffer — no intermediate heap array (large dirty regions would land on
        // the LOH) and no map/unmap round-trip per flush (memory is HostCoherent). The upload buffer
        // is tightly packed (rowBytes stride), so vkCmdCopyBufferToImage uses bufferRowLength = 0.
        var dst = (byte*)page.UploadMapped[slot];
        fixed (byte* pStaging = page.Staging)
        {
            for (var row = 0; row < regionH; row++)
            {
                var srcOffset = ((page.DirtyY0 + row) * _pageDim + page.DirtyX0) * BytesPerTexel;
                Buffer.MemoryCopy(pStaging + srcOffset, dst + row * rowBytes, rowBytes, rowBytes);
            }
        }

        // First flush of a page: its image is still Undefined, so transition from there.
        // Subsequent flushes transition from ShaderReadOnly.
        var srcLayout = page.NeedsInitialTransition ? VkImageLayout.Undefined : VkImageLayout.ShaderReadOnlyOptimal;
        VulkanHelpers.TransitionImageLayout(_ctx.DeviceApi, cmd, page.Image, srcLayout, VkImageLayout.TransferDstOptimal);
        page.NeedsInitialTransition = false;

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(page.DirtyX0, page.DirtyY0, 0),
            imageExtent = new VkExtent3D((uint)regionW, (uint)regionH, 1)
        };
        _ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, page.UploadBuffers[slot], page.Image, VkImageLayout.TransferDstOptimal, 1, &region);

        VulkanHelpers.TransitionImageLayout(_ctx.DeviceApi, cmd, page.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        page.DirtyX0 = _pageDim; page.DirtyY0 = _pageDim;
        page.DirtyX1 = 0; page.DirtyY1 = 0;
        return true;
    }

    public void Dispose()
    {
        // Caller (VkRenderer.Dispose) ensures the GPU is idle before disposing the renderer.
        foreach (var page in _pages)
            DestroyPage(page);
        _pages.Clear();
        _ctx.DeviceApi.vkDestroySampler(_sampler);
    }

    private GlyphInfo RasterizeGlyph(GlyphKey key)
    {
        if (key.Gid == WhitespaceGid)
        {
            var refGlyph = GetGlyph(key.Font, SdfRasterSize, new Rune('n'));
            var info = new GlyphInfo(0, 0, 0, 0, 0, 0, refGlyph.AdvanceX, 0, 0, SdfSpread);
            _glyphs[key] = info;
            return info;
        }

        // Rasterize by the resolved identity in the key (glyph id, or Type1 glyph name).
        var bitmap = RasterizeByKey(key);
        var glyphInfo = InsertRasterized(key, bitmap);
        // Persist for the next session. AppendGlyph silently skips invalid/empty bitmaps.
        _diskCache?.AppendGlyph(key.Font, key.Gid, key.Name, in bitmap);
        return glyphInfo;
    }

    /// <summary>
    /// Serial atlas-insertion path: per-page cursor placement, staging-buffer blit, _glyphs dict
    /// insert. Caller must already hold the rasterized SDF bitmap. Mutates page state (cursor,
    /// staging, dirty region) + _glyphs/_unflushedGlyphs and may append a new page
    /// — must NOT be called concurrently from multiple threads.
    /// </summary>
    private GlyphInfo InsertRasterized(GlyphKey key, MtsdfGlyphBitmap bitmap)
    {
        var glyphWidth = bitmap.Width;
        var glyphHeight = bitmap.Height;

        if (glyphWidth == 0 || glyphHeight == 0) return default;
        // A glyph bigger than a whole page can never be placed (shouldn't happen at 64px raster).
        if (glyphWidth > _pageDim || glyphHeight > _pageDim) return default;

        var pageIdx = _activePageIdx;
        var page = _pages[pageIdx];

        // Advance to a new row if the glyph overflows the current row.
        if (page.CursorX + glyphWidth > _pageDim)
        {
            page.CursorX = 0;
            page.CursorY += page.RowHeight + 1;
            page.RowHeight = 0;
        }

        // Overflows the page → APPEND A NEW PAGE (no grow, no vkDeviceWaitIdle, no re-upload of
        // existing pages). At the page cap, recycle the least-recently-used page IN PLACE (per-page
        // LRU) instead of wiping all pages — this keeps the hot on-screen working set resident, so a
        // glyph-heavy CJK doc no longer thrashes (EvictAll → refill-all → redraw busy-spin). EvictAll
        // survives only as the all-pages-hot fallback (working set > MaxPages in a single frame).
        if (page.CursorY + glyphHeight > _pageDim)
        {
            if (_pages.Count < MaxPages)
            {
                page = AllocateNewPage();   // sets _activePageIdx
                pageIdx = _activePageIdx;
            }
            else if (TryEvictLruPage(out pageIdx))
            {
                page = _pages[pageIdx];     // cursor reset + _activePageIdx set by TryEvictLruPage
            }
            else
            {
                // Every page was touched within the last MaxFramesInFlight frames — nothing safe to
                // recycle. Caller treats Width=0 as not-yet-available and retries after the fallback
                // EvictAll runs next BeginFrame.
                if (!_needsEviction)
                    RenderDiag.Log("sdf.pagecap", $"all {MaxPages} pages hot glyphs={_glyphs.Count} — evict-all fallback");
                _needsEviction = true;
                return default;
            }
        }

        // Blit 4-channel MTSDF data into this page's staging buffer (BytesPerTexel per pixel).
        var rowBytes = glyphWidth * BytesPerTexel;
        for (var row = 0; row < glyphHeight; row++)
        {
            var srcOffset = row * rowBytes;
            var dstOffset = ((page.CursorY + row) * _pageDim + page.CursorX) * BytesPerTexel;
            Buffer.BlockCopy(bitmap.Rgba, srcOffset, page.Staging, dstOffset, rowBytes);
        }

        page.DirtyX0 = Math.Min(page.DirtyX0, page.CursorX);
        page.DirtyY0 = Math.Min(page.DirtyY0, page.CursorY);
        page.DirtyX1 = Math.Max(page.DirtyX1, page.CursorX + glyphWidth);
        page.DirtyY1 = Math.Max(page.DirtyY1, page.CursorY + glyphHeight);

        // U is page-local (every page shares the same width). V is the VIRTUAL y over the
        // MaxPages-tall stack so the page index is recoverable from V alone (see DecodePage).
        var virtDenom = (float)(MaxPages * _pageDim);
        var glyphInfo = new GlyphInfo(
            U0: page.CursorX / (float)_pageDim,
            V0: (pageIdx * _pageDim + page.CursorY) / virtDenom,
            U1: (page.CursorX + glyphWidth) / (float)_pageDim,
            V1: (pageIdx * _pageDim + page.CursorY + glyphHeight) / virtDenom,
            Width: glyphWidth,
            Height: glyphHeight,
            AdvanceX: bitmap.AdvanceX,
            BearingX: bitmap.BearingX,
            BearingY: bitmap.BearingY,
            Spread: bitmap.Spread);

        _glyphs[key] = glyphInfo;
        _unflushedGlyphs.Add(key);
        page.Keys.Add(key);              // for O(page) eviction without scanning all of _glyphs
        page.LastUsedFrame = _frameTick; // freshly written → hot
        page.CursorX += glyphWidth + 1;
        page.RowHeight = Math.Max(page.RowHeight, glyphHeight);
        return glyphInfo;
    }

    /// <summary>
    /// Per-page LRU eviction: when all pages are full, reset the least-recently-used page IN PLACE and
    /// reuse it (keeping every other page's glyphs), rather than wiping the whole atlas. Only a page no
    /// in-flight frame can still be sampling is eligible — its LastUsedFrame must be strictly older than
    /// MaxFramesInFlight, which (because the caller already waited that slot's fence in BeginFrame) means
    /// its GPU work has retired, so the image bytes can be overwritten with no device wait. The stale
    /// pixels below the reset cursor are never sampled again — their glyph keys were removed from
    /// _glyphs — so there's no need to clear the page or re-upload it; new glyphs overwrite from the top.
    /// Returns false when every page is hot (genuinely &gt; MaxPages working set this frame), leaving the
    /// caller to fall back to EvictAll.
    /// </summary>
    private bool TryEvictLruPage(out int pageIdx)
    {
        pageIdx = -1;
        var oldest = long.MaxValue;
        for (var i = 0; i < _pages.Count; i++)
        {
            var lru = _pages[i].LastUsedFrame;
            if (lru >= _frameTick - VulkanContext.MaxFramesInFlight) continue; // hot / possibly in-flight
            if (lru < oldest) { oldest = lru; pageIdx = i; }
        }
        if (pageIdx < 0) return false;

        var page = _pages[pageIdx];
        foreach (var k in page.Keys)
        {
            _glyphs.Remove(k);
            _unflushedGlyphs.Remove(k);
        }
        RenderDiag.Log("sdf.evictpage",
            $"recycled page {pageIdx} (dropped {page.Keys.Count} cold glyphs, {_glyphs.Count} remain)");
        page.Keys.Clear();
        page.CursorX = 0; page.CursorY = 0; page.RowHeight = 0;
        page.LastUsedFrame = _frameTick;
        _activePageIdx = pageIdx;
        return true;
    }

    /// <summary>
    /// Batch-warms a set of glyph keys with parallel SDF rasterization. Skips keys already
    /// in the atlas. Rasterization (the expensive part — SDF distance-field computation
    /// per glyph pixel) runs across the thread pool via <see cref="Parallel.For"/>; the
    /// resulting bitmaps are then inserted into the atlas serially on the calling thread
    /// (cursor placement and the staging buffer aren't thread-safe). On architectural PDFs
    /// where a page touches 60-100 unique glyphs at ~10 ms each, this cuts prewarm time
    /// from ~700 ms serial to ~200 ms on a 4-core box.
    ///
    /// <para>Thread safety: <see cref="ManagedFontRasterizer"/> backs this via
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> for its font cache and allocates
    /// only a per-call result bitmap, so concurrent rasterization is safe.</para>
    ///
    /// <para>Whitespace glyphs go through the serial path because their info is derived
    /// from a reference glyph ('n'), which requires reentry into <see cref="GetGlyph"/>.</para>
    /// </summary>
    public void PreRasterizeBatch(IReadOnlyList<(string Font, Rune Character, int CharCode, GlyphMapHint Hint)> keys)
    {
        if (keys.Count == 0) return;

        // Phase 0: warm the disk cache for every font referenced in this batch. Each call
        // is idempotent (single load per font per session) and bulk-inserts cached glyphs
        // into _glyphs, so Phase 1's "is it cached?" check will hit them automatically.
        if (_diskCache is not null)
        {
            string? lastFont = null;
            foreach (var (font, _, _, _) in keys)
            {
                if (ReferenceEquals(lastFont, font)) continue; // cheap consecutive-dup skip
                EnsureFontLoadedFromDisk(font);
                lastFont = font;
            }
        }

        // Phase 1: resolve each request to its identity key and claim the ones that need
        // rasterization (not cached, not whitespace, not already queued/in-flight). TryAdd both
        // dedups within this batch AND stakes the key, so the SAME visible-glyph set re-offered next
        // frame doesn't re-rasterize what's already pending. Two requests that resolve to the same
        // glyph now collapse to one key here (the A1 dedup).
        var toRasterize = new List<GlyphKey>();
        foreach (var (font, ch, charCode, hint) in keys)
        {
            if (Rune.IsWhiteSpace(ch)) continue;            // warmed synchronously in Phase 3
            var atlasKey = MakeKey(font, ch, charCode, hint);
            if (_glyphs.ContainsKey(atlasKey)) continue;
            if (!_rasterizeInFlight.TryAdd(atlasKey, 0)) continue;
            toRasterize.Add(atlasKey);
        }

        // Phase 2: rasterize OFF the render thread. SDF distance-field computation is the ~10ms/glyph
        // cost; running it inline here was the multi-second frame stall on glyph-heavy (CJK) pages.
        // One background task rasterizes the whole batch in parallel (ManagedFontRasterizer is
        // documented thread-safe — it only touches a ConcurrentDictionary font cache + a per-call
        // result bitmap) and enqueues each finished bitmap to _pendingRasterized. The render thread
        // inserts them bounded-per-frame in BeginFrame -> DrainPendingRasterized; IsDirty stays true
        // until the queue empties, so the loop keeps redrawing and glyphs fill in progressively.
        // Disk persistence happens at insertion time, not here.
        if (toRasterize.Count > 0)
        {
            var work = toRasterize;
            Task.Run(() => Parallel.For(0, work.Count, i =>
            {
                var atlasKey = work[i];
                try
                {
                    _pendingRasterized.Enqueue((atlasKey, RasterizeByKey(atlasKey)));
                }
                catch (Exception ex)
                {
                    // Release the claim so a later frame can retry; otherwise the glyph never appears.
                    _rasterizeInFlight.TryRemove(atlasKey, out _);
                    Console.Error.WriteLine($"[SdfAtlas] batch rasterize failed for {DescribeKey(atlasKey)}: {ex.Message}");
                }
            }));
        }

        // Phase 3: whitespace keys are cheap (their info derives from the 'n' reference glyph) and
        // need GetGlyph reentry, so warm them synchronously — there are only a handful per font.
        foreach (var (font, ch, charCode, hint) in keys)
        {
            if (Rune.IsWhiteSpace(ch)) GetGlyph(font, SdfRasterSize, ch, charCode: charCode, hint: hint);
        }
    }

    private void EvictAll()
    {
        RenderDiag.Log("sdf.evict", $"wiping {_glyphs.Count} glyphs across {_pages.Count} page(s)");
        _glyphs.Clear();
        // Drop pending async rasterization too. A background task still running may enqueue a stale
        // result afterwards — harmless: DrainPendingRasterized re-checks _glyphs and the key isn't
        // claimed anymore, so at worst the glyph is re-rasterized once. Re-requested on next draw.
        _pendingRasterized.Clear();
        _rasterizeInFlight.Clear();
        // Clear the disk-loaded guard too: after eviction the next use of a font should RE-LOAD its
        // cached glyphs from disk (cheap bulk read) instead of re-rasterizing each (~10ms) AND
        // re-appending them as duplicates. Leaving this set meant every eviction cycle re-rasterized
        // every glyph and grew the .sdfg file — the source of the observed 2.7× disk duplication and
        // the repeated large atlas flushes that fragment the LOH.
        _diskLoadedFonts.Clear();

        // Destroy every extra page (1..N-1); page 0 is reset in place. Those pages' descriptor sets may
        // still be referenced by the previous frame's draws, so a single device-idle is needed here —
        // but this is the ONLY remaining drain and fires only when all MaxPages are full (≈never).
        // Bounded (was an unbounded vkDeviceWaitIdle): prior in-flight frames only, capped, skip-when-stuck.
        if (_pages.Count > 1)
        {
            _ctx.TryWaitPriorFramesIdle("sdf atlas evict-all");
            for (var i = _pages.Count - 1; i >= 1; i--)
            {
                DestroyPage(_pages[i]);
                _pages.RemoveAt(i);
            }
        }

        // Reset page 0 in place: clear its staging + cursor and mark it fully dirty so the next Flush
        // re-uploads the cleared pixels. Its image is already valid (no new initial transition needed).
        var p0 = _pages[0];
        p0.CursorX = 0; p0.CursorY = 0; p0.RowHeight = 0;
        p0.Keys.Clear();
        p0.LastUsedFrame = _frameTick;
        Array.Clear(p0.Staging, 0, p0.Staging.Length);
        p0.DirtyX0 = 0; p0.DirtyY0 = 0;
        p0.DirtyX1 = _pageDim; p0.DirtyY1 = _pageDim;
        _activePageIdx = 0;
    }

    private void CreateImage(Page page, int width, int height)
    {
        var api = _ctx.DeviceApi;

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R8G8B8A8Unorm,
            extent = new VkExtent3D((uint)width, (uint)height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined
        };
        api.vkCreateImage(&imageCI, null, out page.Image).CheckResult();

        api.vkGetImageMemoryRequirements(page.Image, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out page.ImageMemory).CheckResult();
        api.vkBindImageMemory(page.Image, page.ImageMemory, 0);

        // Defer the Undefined -> TransferDst transition to the first FlushPage, which records into the
        // frame's command buffer. A one-shot submit here would collide with an in-recording frame cmd
        // buffer on some drivers (VK_ERROR_INITIALIZATION_FAILED from the next vkQueueSubmit).
        page.NeedsInitialTransition = true;

        // Identity swizzle: the shader samples the full RGBA MTSDF (RGB pseudo-distance
        // for median reconstruction, A true distance). No component broadcast — that was
        // only needed when the atlas was single-channel R8.
        var viewCI = new VkImageViewCreateInfo(
            page.Image, VkImageViewType.Image2D, VkFormat.R8G8B8A8Unorm,
            new VkComponentMapping(VkComponentSwizzle.Identity, VkComponentSwizzle.Identity, VkComponentSwizzle.Identity, VkComponentSwizzle.Identity),
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out page.ImageView).CheckResult();
    }

    private void CreateSampler()
    {
        // Bilinear filtering is ideal for SDF — smooth interpolation between
        // distance values produces correct anti-aliased edges
        VkSamplerCreateInfo samplerCI = new()
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            mipmapMode = VkSamplerMipmapMode.Linear,
            maxLod = 1.0f
        };
        _ctx.DeviceApi.vkCreateSampler(&samplerCI, null, out _sampler).CheckResult();
    }

    private void EnsureUploadBuffer(Page page, int slot, ulong size)
    {
        if (page.UploadBuffers[slot] != VkBuffer.Null && page.UploadSizes[slot] >= size)
            return;

        var api = _ctx.DeviceApi;

        if (page.UploadBuffers[slot] != VkBuffer.Null)
        {
            // Slot grows only when the dirty region is larger than the previous peak
            // for this slot — by the time we're here BeginFrame has waited on the
            // matching fence, so destroying the previous allocation is safe.
            api.vkDestroyBuffer(page.UploadBuffers[slot]);
            api.vkFreeMemory(page.UploadMemories[slot]);
        }

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out page.UploadBuffers[slot]).CheckResult();

        api.vkGetBufferMemoryRequirements(page.UploadBuffers[slot], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out page.UploadMemories[slot]).CheckResult();
        api.vkBindBufferMemory(page.UploadBuffers[slot], page.UploadMemories[slot], 0);
        page.UploadSizes[slot] = size;

        // Map once for the buffer's lifetime; FlushPage writes through this pointer every
        // flush instead of paying a vkMapMemory/vkUnmapMemory round-trip each time.
        void* mapped;
        api.vkMapMemory(page.UploadMemories[slot], 0, size, 0, &mapped).CheckResult();
        page.UploadMapped[slot] = (IntPtr)mapped;
    }
}
