using System.Text;
using DIR.Lib;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Vulkan backend for <see cref="SdfFontAtlas"/> (DIR.Lib), which owns every CPU-side atlas
/// decision — glyph keying, shelf packing, staging + dirty rects, LRU/evict policy, budgets.
/// This class owns only the GPU half: one <see cref="VkPageResources"/> per atlas page
/// (image + view + descriptor set + per-frame upload ring), the shared sampler, and
/// <see cref="Flush"/>, which records each page's dirty-region upload into the frame's command
/// buffer. Page lifecycle arrives through <see cref="ISdfAtlasBackend"/> hooks; everything else
/// forwards 1:1 to the core so <see cref="VkRenderer"/>'s call sites are unchanged by the split.
/// </summary>
internal sealed unsafe class VkSdfFontAtlas : IDisposable, ISdfAtlasBackend
{
    private readonly VulkanContext _ctx;
    private readonly SdfFontAtlas _core;
    private VkSampler _sampler;    // shared across all pages (identical params)

    /// <summary>One page's GPU-side resources, index-correlated with the core's page list
    /// (the teardown hooks fire in descending order precisely so RemoveAt keeps parity).</summary>
    private sealed class VkPageResources
    {
        public VkImage Image;
        public VkDeviceMemory ImageMemory;
        public VkImageView ImageView;
        public VkDescriptorSet DescriptorSet;
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

    private readonly List<VkPageResources> _pageResources = new();

    public VkSdfFontAtlas(VulkanContext ctx, ManagedFontRasterizer rasterizer,
        SdfGlyphDiskCache? diskCache = null,
        int initialWidth = SdfFontAtlas.DefaultInitialAtlasDim,
        int initialHeight = SdfFontAtlas.DefaultInitialAtlasDim,
        float rasterSize = SdfFontAtlas.SdfRasterSize,
        int maxPages = SdfFontAtlas.MaxPages,
        bool refuseWhenSaturated = false)
    {
        _ctx = ctx;
        // MUST run before constructing the core: its ctor synchronously allocates page 0, which
        // fires OnPageCreated(0) inline — and that needs the sampler for the descriptor update.
        CreateSampler();
        _core = new SdfFontAtlas(rasterizer,
            maxTextureDimension: (int)ctx.MaxImageDimension2D,
            framesInFlight: VulkanContext.MaxFramesInFlight,
            backend: this,
            initialPageDim: initialWidth,
            diskCache: diskCache,
            rasterSize: rasterSize,
            maxPages: maxPages,
            refuseWhenSaturated: refuseWhenSaturated);
        // initialHeight intentionally unused — pages are square, exactly as before the split.
        _ = initialHeight;
    }

    // ---- forwards to the backend-neutral core --------------------------------------------------

    /// <summary>The shared rasterizer this atlas resolves/rasterizes through — also what the
    /// renderer hands an <see cref="ITextShaper"/> so shaping keys off the same font state.</summary>
    internal ManagedFontRasterizer Rasterizer => _core.Rasterizer;

    public int PageCount => _core.PageCount;
    public string FrameStats => _core.FrameStats;
    public bool IsDirty => _core.IsDirty;

    public void DecodePage(in SdfFontAtlas.GlyphInfo g, out int page, out float localV0, out float localV1)
        => _core.DecodePage(in g, out page, out localV0, out localV1);

    public void BeginFrame() => _core.BeginFrame();

    public SdfFontAtlas.GlyphInfo GetGlyph(string fontPath, float fontSize, Rune character,
        bool skipUnflushed = false, int charCode = -1, GlyphMapHint hint = GlyphMapHint.Auto,
        bool rasterizeOnMiss = true)
        => _core.GetGlyph(fontPath, fontSize, character, skipUnflushed, charCode, hint, rasterizeOnMiss);

    public SdfFontAtlas.GlyphInfo GetGlyphByGid(string fontPath, uint gid, string? type1Name = null,
        bool skipUnflushed = false, bool rasterizeOnMiss = true)
        => _core.GetGlyphByGid(fontPath, gid, type1Name, skipUnflushed, rasterizeOnMiss);

    public void PreRasterizeBatch(IReadOnlyList<(string Font, Rune Character, int CharCode, GlyphMapHint Hint)> keys)
        => _core.PreRasterizeBatch(keys);

    public void PreRasterizeBatchByGid(IReadOnlyList<(string Font, uint Gid, string? Type1Name)> keys)
        => _core.PreRasterizeBatchByGid(keys);

    /// <summary>Raster size (px) this atlas bakes at — 64 for the default tier, larger for a big-text tier.</summary>
    public float RasterSize => _core.RasterSize;
    // Instance now (were static): the quad scale and the AA half-band both depend on THIS atlas's
    // raster size, which differs per tier — so callers invoke them on the specific atlas a glyph
    // came from, not on the type.
    public float GetGlyphScale(float requestedFontSize) => _core.GetGlyphScale(requestedFontSize);
    public float ScreenPxHalfBand(float fontSize) => _core.ScreenPxHalfBand(fontSize);

    // ---- GPU-side page surface ------------------------------------------------------------------

    public VkDescriptorSet GetPageDescriptorSet(int pageIndex) => _pageResources[pageIndex].DescriptorSet;
    public VkSampler Sampler => _sampler;

    // ---- ISdfAtlasBackend -----------------------------------------------------------------------

    public void OnPageCreated(int pageIndex, int pageDimension)
    {
        System.Diagnostics.Debug.Assert(_sampler != VkSampler.Null,
            "CreateSampler must run before the core ctor — OnPageCreated(0) fires inline from it");
        var res = new VkPageResources();
        CreateImage(res, pageDimension, pageDimension);
        res.DescriptorSet = _ctx.AllocateDescriptorSet();
        _ctx.UpdateDescriptorSet(res.DescriptorSet, res.ImageView, _sampler);
        _pageResources.Add(res);
        RenderDiag.Log("sdf.newpage", $"page {pageIndex} allocated {pageDimension}x{pageDimension}");
    }

    public void OnPagesWillBeDestroyed()
        // Evict-all teardown: those pages' descriptor sets may still be referenced by the previous
        // frame's draws, so wait for prior in-flight frames first. Bounded (was an unbounded
        // vkDeviceWaitIdle): prior in-flight frames only, capped, skip-when-stuck.
        => _ctx.TryWaitPriorFramesIdle("sdf atlas evict-all");

    public void OnPageDestroyed(int pageIndex)
    {
        DestroyPage(_pageResources[pageIndex]);
        _pageResources.RemoveAt(pageIndex);
    }

    // ---- flush: record dirty-region uploads into the frame command buffer ------------------------

    public void Flush(VkCommandBuffer cmd)
    {
        // Pick this frame's upload slot. BeginFrame has already waited on the fence that guards
        // this slot's last submit, so the GPU is done with each page's slot-k upload buffer.
        var slot = _ctx.CurrentFrame;
        var any = false;
        for (var i = 0; i < _core.PageCount; i++)
            any |= FlushPage(cmd, i, slot);
        // A glyph is "unflushed" until its page is uploaded; the loop flushes every dirty page in
        // this one command buffer, so once it returns all pages are current.
        _core.CompleteFlush(any);
    }

    private bool FlushPage(VkCommandBuffer cmd, int pageIndex, int slot)
    {
        if (!_core.TryGetDirtyRegion(pageIndex, out var r))
            return false;

        var page = _pageResources[pageIndex];
        var pageDim = _core.PageDimension;
        var regionW = r.Width;
        var regionH = r.Height;
        var pixelCount = regionW * regionH;
        var rowBytes = regionW * SdfFontAtlas.BytesPerTexel;

        var bufferSize = (ulong)(pixelCount * SdfFontAtlas.BytesPerTexel);
        EnsureUploadBuffer(page, slot, bufferSize);

        // Copy the dirty region (BytesPerTexel per pixel) row-by-row straight into the persistently
        // mapped upload buffer — no intermediate heap array (large dirty regions would land on
        // the LOH) and no map/unmap round-trip per flush (memory is HostCoherent). The upload buffer
        // is tightly packed (rowBytes stride), so vkCmdCopyBufferToImage uses bufferRowLength = 0.
        var dst = (byte*)page.UploadMapped[slot];
        fixed (byte* pStaging = _core.GetPageStaging(pageIndex))
        {
            for (var row = 0; row < regionH; row++)
            {
                var srcOffset = ((r.Y0 + row) * pageDim + r.X0) * SdfFontAtlas.BytesPerTexel;
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
            imageOffset = new VkOffset3D(r.X0, r.Y0, 0),
            imageExtent = new VkExtent3D((uint)regionW, (uint)regionH, 1)
        };
        _ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, page.UploadBuffers[slot], page.Image, VkImageLayout.TransferDstOptimal, 1, &region);

        VulkanHelpers.TransitionImageLayout(_ctx.DeviceApi, cmd, page.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        _core.MarkPageFlushed(pageIndex);
        return true;
    }

    public void Dispose()
    {
        // Caller (VkRenderer.Dispose) ensures the GPU is idle before disposing the renderer.
        // Core Dispose fires OnPageDestroyed for every page (descending) — freeing the GPU half.
        _core.Dispose();
        _ctx.DeviceApi.vkDestroySampler(_sampler);
    }

    // ---- Vulkan resource plumbing -----------------------------------------------------------------

    private void CreateImage(VkPageResources page, int width, int height)
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

    private void DestroyPage(VkPageResources p)
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

    private void EnsureUploadBuffer(VkPageResources page, int slot, ulong size)
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
