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
    // CharCode is included in the key for CID subset fonts where the same Unicode
    // character may need different glyph indices. For non-CID fonts, charCode is -1.
    private readonly record struct GlyphKey(string Font, float Size, Rune Character, int CharCode);

    internal readonly record struct GlyphInfo(float U0, float V0, float U1, float V1,
        int Width, int Height, float AdvanceX, int BearingX, int BearingY, float Spread);

    private readonly VulkanContext _ctx;
    private readonly ManagedFontRasterizer _rasterizer;
    private readonly Dictionary<GlyphKey, GlyphInfo> _glyphs = new();
    private readonly HashSet<GlyphKey> _unflushedGlyphs = new();

    private const int MaxAtlasSize = 4096;
    // Effective cap, clamped to the device's maxImageDimension2D (set in the ctor) so we never request
    // an atlas larger than the GPU can allocate. Use this — not the const — for grow decisions.
    private readonly int _maxAtlasSize;
    private const float SdfSpread = 4f;

    /// <summary>
    /// SDF glyphs are rasterized at this fixed size. The GPU scales the quad
    /// for any requested display size. Because SDF encodes distance, not pixels,
    /// a single rasterization looks sharp at all display sizes. Raising this size
    /// gives more sub-pixel fidelity when glyphs are displayed smaller than the
    /// raster size, at the cost of atlas memory (quadratic).
    /// </summary>
    private const float SdfRasterSize = 128f;

    /// <summary>
    /// Default page dimension. The atlas is a list of fixed-size square pages of this size;
    /// when a page fills, a NEW page is appended (no realloc, no GPU drain, no re-upload), so
    /// this is the placement granularity, not a glyph cap. Power-of-two so a glyph's page index
    /// can be recovered exactly from its virtual V coordinate (see <see cref="InsertRasterized"/>
    /// and <see cref="DecodePage"/>).
    /// </summary>
    private const int DefaultInitialAtlasDim = (int)SdfRasterSize * 16; // 2048

    // Max resident pages before falling back to evict-all. 16 × 2048² × 1 byte ≈ 64 MB worst case.
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
        public byte[] Staging = [];                       // 1 byte per pixel, _pageDim²
        public int CursorX, CursorY, RowHeight;
        public int DirtyX0, DirtyY0, DirtyX1, DirtyY1;
        // Image starts UNDEFINED; the first FlushPage transitions from there, not ShaderReadOnly.
        public bool NeedsInitialTransition;
        // Per-frame upload ring — slot k reused only after BeginFrame waited on its fence.
        public readonly VkBuffer[] UploadBuffers = new VkBuffer[VulkanContext.MaxFramesInFlight];
        public readonly VkDeviceMemory[] UploadMemories = new VkDeviceMemory[VulkanContext.MaxFramesInFlight];
        public readonly ulong[] UploadSizes = new ulong[VulkanContext.MaxFramesInFlight];
    }

    private readonly List<Page> _pages = new();
    private int _pageDim;          // fixed square page size (power of two)
    private bool _needsEviction;
    private VkSampler _sampler;    // shared across all pages (identical params)

    public int PageCount => _pages.Count;
    public VkDescriptorSet GetPageDescriptorSet(int pageIndex) => _pages[pageIndex].DescriptorSet;
    public VkSampler Sampler => _sampler;

    public bool IsDirty
    {
        get
        {
            if (_needsEviction) return true;
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

    public VkSdfFontAtlas(VulkanContext ctx, ManagedFontRasterizer rasterizer, int initialWidth = DefaultInitialAtlasDim, int initialHeight = DefaultInitialAtlasDim)
    {
        _ctx = ctx;
        _rasterizer = rasterizer;
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
            Staging = new byte[_pageDim * _pageDim],
            DirtyX0 = _pageDim, DirtyY0 = _pageDim, DirtyX1 = 0, DirtyY1 = 0,
        };
        CreateImage(page, _pageDim, _pageDim);
        page.DescriptorSet = _ctx.AllocateDescriptorSet();
        _ctx.UpdateDescriptorSet(page.DescriptorSet, page.ImageView, _sampler);
        _pages.Add(page);
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
        if (_needsEviction)
        {
            EvictAll();
            _needsEviction = false;
        }
    }

    /// <summary>
    /// Returns the scale factor between the requested fontSize and the SDF raster size.
    /// </summary>
    public static float GetGlyphScale(float requestedFontSize) => requestedFontSize / SdfRasterSize;

    public GlyphInfo GetGlyph(string fontPath, float fontSize, Rune character,
        bool skipUnflushed = false, int charCode = -1, GlyphMapHint hint = GlyphMapHint.Auto)
    {
        // All SDF glyphs are rasterized at SdfRasterSize; the caller scales the quad
        var key = new GlyphKey(fontPath, SdfRasterSize, character, charCode);
        if (_glyphs.TryGetValue(key, out var existing))
        {
            if (skipUnflushed && _unflushedGlyphs.Contains(key))
                return existing with { Width = 0 };
            return existing;
        }
        var result = RasterizeGlyph(key, charCode, hint);
        if (skipUnflushed && result.Width > 0)
            return result with { Width = 0 };
        return result;
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

        // Extract dirty region into contiguous buffer (1 byte per pixel)
        var data = new byte[pixelCount];
        for (var row = 0; row < regionH; row++)
        {
            var srcOffset = (page.DirtyY0 + row) * _pageDim + page.DirtyX0;
            var dstOffset = row * regionW;
            Buffer.BlockCopy(page.Staging, srcOffset, data, dstOffset, regionW);
        }

        var bufferSize = (ulong)pixelCount;
        EnsureUploadBuffer(page, slot, bufferSize);

        void* mapped;
        _ctx.DeviceApi.vkMapMemory(page.UploadMemories[slot], 0, bufferSize, 0, &mapped);
        fixed (byte* pData = data)
            Buffer.MemoryCopy(pData, mapped, bufferSize, bufferSize);
        _ctx.DeviceApi.vkUnmapMemory(page.UploadMemories[slot]);

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

    private GlyphInfo RasterizeGlyph(GlyphKey key, int charCode = -1, GlyphMapHint hint = GlyphMapHint.Auto)
    {
        if (Rune.IsWhiteSpace(key.Character))
        {
            var refGlyph = GetGlyph(key.Font, SdfRasterSize, new Rune('n'));
            var info = new GlyphInfo(0, 0, 0, 0, 0, 0, refGlyph.AdvanceX, 0, 0, SdfSpread);
            _glyphs[key] = info;
            return info;
        }

        // Use WithCharCode variant for CID/embedded-subset fonts whose Unicode cmap
        // may be absent or unreliable. RasterizeGlyphSdfWithCharCode supports
        // multiple cmap strategies via GlyphMapHint.
        var bitmap = charCode >= 0
            ? _rasterizer.RasterizeGlyphSdfWithCharCode(key.Font, key.Size, key.Character, (uint)charCode, hint, SdfSpread)
            : _rasterizer.RasterizeGlyphSdf(key.Font, key.Size, key.Character, SdfSpread);
        return InsertRasterized(key, bitmap);
    }

    /// <summary>
    /// Serial atlas-insertion path: per-page cursor placement, staging-buffer blit, _glyphs dict
    /// insert. Caller must already hold the rasterized SDF bitmap. Mutates page state (cursor,
    /// staging, dirty region) + _glyphs/_unflushedGlyphs and may append a new page
    /// — must NOT be called concurrently from multiple threads.
    /// </summary>
    private GlyphInfo InsertRasterized(GlyphKey key, SdfGlyphBitmap bitmap)
    {
        var glyphWidth = bitmap.Width;
        var glyphHeight = bitmap.Height;

        if (glyphWidth == 0 || glyphHeight == 0) return default;
        // A glyph bigger than a whole page can never be placed (shouldn't happen at 128px raster).
        if (glyphWidth > _pageDim || glyphHeight > _pageDim) return default;

        var pageIdx = _pages.Count - 1;
        var page = _pages[pageIdx];

        // Advance to a new row if the glyph overflows the current row.
        if (page.CursorX + glyphWidth > _pageDim)
        {
            page.CursorX = 0;
            page.CursorY += page.RowHeight + 1;
            page.RowHeight = 0;
        }

        // Overflows the page → APPEND A NEW PAGE (no grow, no vkDeviceWaitIdle, no re-upload of
        // existing pages). Only when all pages are full do we fall back to a (deferred) evict-all.
        if (page.CursorY + glyphHeight > _pageDim)
        {
            if (_pages.Count >= MaxPages)
            {
                // Log once per full-episode (not per rejected glyph). Caller treats Width=0 as
                // not-yet-available and retries next frame, exactly like the old grow/evict path.
                if (!_needsEviction)
                    RenderDiag.Log("sdf.pagecap", $"all {MaxPages} pages full glyphs={_glyphs.Count} — evict deferred");
                _needsEviction = true;
                return default;
            }
            pageIdx = _pages.Count;
            page = AllocateNewPage();
        }

        // Blit single-channel SDF data into this page's staging buffer.
        for (var row = 0; row < glyphHeight; row++)
        {
            var srcOffset = row * glyphWidth;
            var dstOffset = (page.CursorY + row) * _pageDim + page.CursorX;
            Buffer.BlockCopy(bitmap.Alpha, srcOffset, page.Staging, dstOffset, glyphWidth);
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
        page.CursorX += glyphWidth + 1;
        page.RowHeight = Math.Max(page.RowHeight, glyphHeight);
        return glyphInfo;
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

        // Phase 1: identify keys that need rasterization (not already cached, not whitespace).
        // Whitespace and already-cached entries are handled separately.
        var toRasterize = new List<(GlyphKey AtlasKey, int CharCode, GlyphMapHint Hint)>(keys.Count);
        var seenThisBatch = new HashSet<GlyphKey>();
        foreach (var (font, ch, charCode, hint) in keys)
        {
            var atlasKey = new GlyphKey(font, SdfRasterSize, ch, charCode);
            if (_glyphs.ContainsKey(atlasKey)) continue;
            if (Rune.IsWhiteSpace(ch)) continue;          // serial fallback below
            if (!seenThisBatch.Add(atlasKey)) continue;    // dedup duplicates within the batch
            toRasterize.Add((atlasKey, charCode, hint));
        }

        // Phase 2: parallel SDF rasterization. Each iteration calls into
        // ManagedFontRasterizer which is documented thread-safe for concurrent
        // RasterizeGlyphSdf*() calls — it only mutates a ConcurrentDictionary<string, OpenTypeFont>
        // font cache, and per-glyph rendering allocates only the result bitmap.
        var bitmaps = new SdfGlyphBitmap[toRasterize.Count];
        Parallel.For(0, toRasterize.Count, i =>
        {
            var (atlasKey, charCode, hint) = toRasterize[i];
            bitmaps[i] = charCode >= 0
                ? _rasterizer.RasterizeGlyphSdfWithCharCode(atlasKey.Font, atlasKey.Size, atlasKey.Character, (uint)charCode, hint, SdfSpread)
                : _rasterizer.RasterizeGlyphSdf(atlasKey.Font, atlasKey.Size, atlasKey.Character, SdfSpread);
        });

        // Phase 3: serial atlas insertion. Per-page cursor / staging / _glyphs / _unflushedGlyphs
        // are not thread-safe and page allocation must run in isolation.
        for (var i = 0; i < toRasterize.Count; i++)
            InsertRasterized(toRasterize[i].AtlasKey, bitmaps[i]);

        // Phase 4: handle whitespace keys through the normal serial path (they recurse
        // into GetGlyph for the reference glyph and so can't go through the parallel
        // phase). This is cheap — whitespace lookups are constant-time once 'n' is cached.
        foreach (var (font, ch, charCode, hint) in keys)
        {
            if (Rune.IsWhiteSpace(ch)) GetGlyph(font, SdfRasterSize, ch, charCode: charCode, hint: hint);
        }
    }

    private void EvictAll()
    {
        RenderDiag.Log("sdf.evict", $"wiping {_glyphs.Count} glyphs across {_pages.Count} page(s)");
        _glyphs.Clear();
        // Destroy every extra page (1..N-1); page 0 is reset in place. Those pages' descriptor sets may
        // still be referenced by the previous frame's draws, so a single device-idle is needed here —
        // but this is the ONLY remaining drain and fires only when all MaxPages are full (≈never).
        if (_pages.Count > 1)
        {
            _ctx.DeviceApi.vkDeviceWaitIdle();
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
        Array.Clear(p0.Staging, 0, p0.Staging.Length);
        p0.DirtyX0 = 0; p0.DirtyY0 = 0;
        p0.DirtyX1 = _pageDim; p0.DirtyY1 = _pageDim;
    }

    private void CreateImage(Page page, int width, int height)
    {
        var api = _ctx.DeviceApi;

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R8Unorm,
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

        // Swizzle R channel into all RGBA channels so the sampler reads the SDF
        // value consistently regardless of which component the shader samples
        var viewCI = new VkImageViewCreateInfo(
            page.Image, VkImageViewType.Image2D, VkFormat.R8Unorm,
            new VkComponentMapping(VkComponentSwizzle.R, VkComponentSwizzle.R, VkComponentSwizzle.R, VkComponentSwizzle.R),
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
    }
}
