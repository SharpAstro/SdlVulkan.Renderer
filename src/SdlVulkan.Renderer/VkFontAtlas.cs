using System.Text;
using DIR.Lib;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

internal sealed unsafe class VkFontAtlas : IDisposable
{
    // CharCode is included in the key for CID subset fonts where the same Unicode
    // character may need different glyph indices. For non-CID fonts, charCode is -1.
    private readonly record struct GlyphKey(string Font, float Size, Rune Character, int CharCode);

    internal readonly record struct GlyphInfo(float U0, float V0, float U1, float V1, int Width, int Height, float AdvanceX, int BearingX, int BearingY);

    private readonly VulkanContext _ctx;
    // The glyph rasterizer. Normally this atlas creates and owns it, but a multi-window host injects a
    // single PROCESS-OWNED rasterizer shared by every window's atlas (and the PDF parser). That shared
    // instance must outlive any one window — a document tab can be torn out into another window, and its
    // parser keeps rasterizing through this same instance — so an injected rasterizer is NOT disposed
    // here (see _ownsRasterizer). Dispose() on this rasterizer only clears its font cache, which would
    // otherwise pull every registered embedded font out from under a tab that outlived its origin window.
    internal readonly ManagedFontRasterizer Rasterizer;
    private readonly bool _ownsRasterizer;
    private readonly Dictionary<GlyphKey, GlyphInfo> _glyphs = new();
    private readonly HashSet<GlyphKey> _unflushedGlyphs = new();

    private const int MaxAtlasSize = 4096;

    private int _atlasWidth;
    private int _atlasHeight;
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;

    private byte[] _staging;

    private int _dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1;
    private bool _needsEviction;

    // Set whenever CreateImage allocates a fresh VkImage (initial + every Grow). The image starts
    // in VK_IMAGE_LAYOUT_UNDEFINED; the first Flush must transition from there. Previously CreateImage
    // did this via ctx.ExecuteOneShot — a SECOND vkQueueSubmit to the graphics queue while the frame's
    // command buffer is already recording (CreateImage runs inside Grow → RasterizeGlyph → OnPreFlush,
    // which is after vkBeginCommandBuffer). Some drivers (Nvidia/Intel) then return
    // VK_ERROR_INITIALIZATION_FAILED from the next vkQueueSubmit, which spins the swapchain-recovery
    // loop. Deferring the transition into the frame's own command buffer (as VkSdfFontAtlas does)
    // avoids the side-submit entirely.
    private bool _needsInitialTransition;

    private VkImage _image;
    private VkDeviceMemory _imageMemory;
    private VkImageView _imageView;
    private VkSampler _sampler;

    // Per-frame upload ring — one slot per frame-in-flight. See VkSdfFontAtlas
    // for the rationale: the previous single-slot design needed a
    // vkDeviceWaitIdle() on every Flush (full-GPU stall); N slots indexed by
    // _ctx.CurrentFrame are naturally race-free because BeginFrame has already
    // waited on the matching per-slot fence before Flush is reached.
    private readonly VkBuffer[] _uploadBuffers = new VkBuffer[VulkanContext.MaxFramesInFlight];
    private readonly VkDeviceMemory[] _uploadMemories = new VkDeviceMemory[VulkanContext.MaxFramesInFlight];
    private readonly ulong[] _uploadBufferSizes = new ulong[VulkanContext.MaxFramesInFlight];

    public VkImageView ImageView => _imageView;
    public VkSampler Sampler => _sampler;

    public VkFontAtlas(VulkanContext ctx, ManagedFontRasterizer? rasterizer = null,
        int initialWidth = 512, int initialHeight = 512)
    {
        _ctx = ctx;
        // Use the injected (process-owned, shared) rasterizer when given; otherwise create and own one.
        Rasterizer = rasterizer ?? new ManagedFontRasterizer();
        _ownsRasterizer = rasterizer is null;
        _atlasWidth = initialWidth;
        _atlasHeight = initialHeight;
        _staging = new byte[initialWidth * initialHeight * 4];
        ResetDirtyRegion();

        CreateImage(initialWidth, initialHeight);
        CreateSampler();
        ctx.UpdateDescriptorSet(ctx.DescriptorSet, _imageView, _sampler);
    }

    /// <summary>
    /// Call at the start of each frame to handle deferred eviction.
    /// This ensures no stale UV coordinates exist in the current frame's batch.
    /// </summary>
    public void BeginFrame()
    {
        if (_needsEviction)
        {
            EvictAll();
            _needsEviction = false;
        }
    }

    /// <summary>
    /// Returns the scale factor between the requested fontSize and the actual rasterized size.
    /// Callers must scale glyph metrics (Width, Height, BearingX, BearingY) by this factor.
    /// </summary>
    public static float GetGlyphScale(float requestedFontSize)
    {
        var quantized = QuantizeFontSize(requestedFontSize);
        return requestedFontSize / quantized;
    }

    /// <summary>
    /// Gets glyph info, rasterizing into the staging buffer if needed.
    /// Use <paramref name="skipUnflushed"/> in draw loops to avoid sampling stale GPU texture data.
    /// </summary>
    public GlyphInfo GetGlyph(string fontPath, float fontSize, Rune character, bool skipUnflushed = false, int charCode = -1, GlyphMapHint hint = GlyphMapHint.Auto)
    {
        fontSize = QuantizeFontSize(fontSize);
        var key = new GlyphKey(fontPath, fontSize, character, charCode);
        if (_glyphs.TryGetValue(key, out var existing))
        {
            // Cache hit — safe to draw only if this glyph has been flushed to GPU
            if (skipUnflushed && _unflushedGlyphs.Contains(key))
                return existing with { Width = 0 }; // metrics preserved for advance, but skip quad
            return existing;
        }
        var result = RasterizeGlyph(key, charCode, hint);
        if (skipUnflushed && result.Width > 0)
            return result with { Width = 0 }; // just rasterized, not flushed yet — skip quad
        return result;
    }

    /// <summary>
    /// Max rasterization size in pixels. Larger glyphs are rasterized at this size
    /// and the textured quad is scaled up by the GPU — avoids atlas overflow at high zoom.
    /// </summary>
    private const float MaxRasterSize = 128f;

    /// <summary>
    /// Snaps font sizes to coarser steps at larger sizes to reduce atlas churn during zoom.
    /// Small text (≤16pt): 1pt steps. Medium (≤48pt): 2pt steps. Large: 4pt steps.
    /// Capped at MaxRasterSize — the GPU scales the quad for anything larger.
    /// </summary>
    private static float QuantizeFontSize(float size)
    {
        if (size < 4f) return 4f;
        if (size <= 16f) return MathF.Ceiling(size);
        if (size <= 48f) return MathF.Ceiling(size / 2f) * 2f;
        if (size <= MaxRasterSize) return MathF.Ceiling(size / 4f) * 4f;
        return MaxRasterSize;
    }

    public bool IsDirty => _needsEviction || (_dirtyX0 < _dirtyX1 && _dirtyY0 < _dirtyY1);

    public void Flush(VkCommandBuffer cmd)
    {
        if (_dirtyX0 >= _dirtyX1 || _dirtyY0 >= _dirtyY1)
            return;

        var regionW = _dirtyX1 - _dirtyX0;
        var regionH = _dirtyY1 - _dirtyY0;
        var pixelCount = regionW * regionH;

        // Extract the dirty region into a contiguous buffer
        var rgba = new byte[pixelCount * 4];
        for (var row = 0; row < regionH; row++)
        {
            var srcOffset = ((_dirtyY0 + row) * _atlasWidth + _dirtyX0) * 4;
            var dstOffset = row * regionW * 4;
            Buffer.BlockCopy(_staging, srcOffset, rgba, dstOffset, regionW * 4);
        }

        var bufferSize = (ulong)(pixelCount * 4);

        // Pick this frame's upload slot. BeginFrame has already waited on the
        // fence that guards this slot's last submit, so the GPU is done with it.
        var slot = _ctx.CurrentFrame;
        EnsureUploadBuffer(slot, bufferSize);

        void* mapped;
        _ctx.DeviceApi.vkMapMemory(_uploadMemories[slot], 0, bufferSize, 0, &mapped);
        fixed (byte* pRgba = rgba)
            Buffer.MemoryCopy(pRgba, mapped, bufferSize, bufferSize);
        _ctx.DeviceApi.vkUnmapMemory(_uploadMemories[slot]);

        // First Flush after CreateImage: the image is still in Undefined layout, so transition from
        // there (a fresh image has no prior contents to preserve). Subsequent flushes come from
        // ShaderReadOnly. This replaces the side-submit that CreateImage used to do.
        var srcLayout = _needsInitialTransition ? VkImageLayout.Undefined : VkImageLayout.ShaderReadOnlyOptimal;
        VulkanHelpers.TransitionImageLayout(_ctx.DeviceApi, cmd, _image, srcLayout, VkImageLayout.TransferDstOptimal);
        _needsInitialTransition = false;

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(_dirtyX0, _dirtyY0, 0),
            imageExtent = new VkExtent3D((uint)regionW, (uint)regionH, 1)
        };
        _ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, _uploadBuffers[slot], _image, VkImageLayout.TransferDstOptimal, 1, &region);

        VulkanHelpers.TransitionImageLayout(_ctx.DeviceApi, cmd, _image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        ResetDirtyRegion();
        _unflushedGlyphs.Clear();
    }

    public void Dispose()
    {
        // Only dispose the rasterizer if this atlas created it. An injected shared rasterizer is owned
        // by the host (it outlives this window — see the Rasterizer field comment).
        if (_ownsRasterizer)
            Rasterizer.Dispose();

        var api = _ctx.DeviceApi;

        for (var i = 0; i < VulkanContext.MaxFramesInFlight; i++)
        {
            if (_uploadBuffers[i] != VkBuffer.Null)
            {
                api.vkDestroyBuffer(_uploadBuffers[i]);
                api.vkFreeMemory(_uploadMemories[i]);
            }
        }

        api.vkDestroySampler(_sampler);
        api.vkDestroyImageView(_imageView);
        api.vkDestroyImage(_image);
        api.vkFreeMemory(_imageMemory);
    }

    private GlyphInfo RasterizeGlyph(GlyphKey key, int charCode = -1, GlyphMapHint hint = GlyphMapHint.Auto)
    {
        if (Rune.IsWhiteSpace(key.Character))
        {
            var refGlyph = GetGlyph(key.Font, key.Size, new Rune('n'));
            var info = new GlyphInfo(0, 0, 0, 0, 0, 0, refGlyph.AdvanceX, 0, 0);
            _glyphs[key] = info;
            return info;
        }

        // Use charCode-aware lookup when charCode is available (subset/embedded fonts).
        // RasterizeGlyphWithCharCode supports multiple cmap strategies via GlyphMapHint.
        GlyphBitmap bitmap;
        if (charCode >= 0)
            bitmap = Rasterizer.RasterizeGlyphWithCharCode(key.Font, key.Size, key.Character, (uint)charCode, hint);
        else
            bitmap = Rasterizer.RasterizeGlyph(key.Font, key.Size, key.Character);
        var glyphWidth = bitmap.Width;
        var glyphHeight = bitmap.Height;

        if (glyphWidth == 0 || glyphHeight == 0) return default;

        if (_cursorX + glyphWidth > _atlasWidth)
        {
            _cursorX = 0;
            _cursorY += _rowHeight + 1;
            _rowHeight = 0;
        }

        if (_cursorY + glyphHeight > _atlasHeight)
        {
            if (_atlasWidth < MaxAtlasSize || _atlasHeight < MaxAtlasSize)
            {
                Grow();
                return RasterizeGlyph(key);
            }
            // Defer eviction to next frame start to avoid stale UVs in current batch
            _needsEviction = true;
            return default;
        }

        // Blit glyph RGBA into staging buffer
        for (var row = 0; row < glyphHeight; row++)
        {
            var srcOffset = row * glyphWidth * 4;
            var dstOffset = ((_cursorY + row) * _atlasWidth + _cursorX) * 4;
            Buffer.BlockCopy(bitmap.Rgba, srcOffset, _staging, dstOffset, glyphWidth * 4);
        }

        _dirtyX0 = Math.Min(_dirtyX0, _cursorX);
        _dirtyY0 = Math.Min(_dirtyY0, _cursorY);
        _dirtyX1 = Math.Max(_dirtyX1, _cursorX + glyphWidth);
        _dirtyY1 = Math.Max(_dirtyY1, _cursorY + glyphHeight);

        var glyphInfo = new GlyphInfo(
            U0: _cursorX / (float)_atlasWidth,
            V0: _cursorY / (float)_atlasHeight,
            U1: (_cursorX + glyphWidth) / (float)_atlasWidth,
            V1: (_cursorY + glyphHeight) / (float)_atlasHeight,
            Width: glyphWidth,
            Height: glyphHeight,
            AdvanceX: bitmap.AdvanceX,
            BearingX: bitmap.BearingX,
            BearingY: bitmap.BearingY);

        _glyphs[key] = glyphInfo;
        _unflushedGlyphs.Add(key);
        _cursorX += glyphWidth + 1;
        _rowHeight = Math.Max(_rowHeight, glyphHeight);
        return glyphInfo;
    }

    private void Grow()
    {
        var oldWidth = _atlasWidth;
        var oldHeight = _atlasHeight;

        _atlasWidth = Math.Min(_atlasWidth * 2, MaxAtlasSize);
        _atlasHeight = Math.Min(_atlasHeight * 2, MaxAtlasSize);

        var newStaging = new byte[_atlasWidth * _atlasHeight * 4];
        // Copy old rows into the wider buffer
        for (var row = 0; row < oldHeight; row++)
        {
            var srcOffset = row * oldWidth * 4;
            var dstOffset = row * _atlasWidth * 4;
            Buffer.BlockCopy(_staging, srcOffset, newStaging, dstOffset, oldWidth * 4);
        }
        _staging = newStaging;

        var scaleX = (float)oldWidth / _atlasWidth;
        var scaleY = (float)oldHeight / _atlasHeight;
        var keys = new GlyphKey[_glyphs.Count];
        _glyphs.Keys.CopyTo(keys, 0);
        foreach (var key in keys)
        {
            var g = _glyphs[key];
            _glyphs[key] = g with { U0 = g.U0 * scaleX, V0 = g.V0 * scaleY, U1 = g.U1 * scaleX, V1 = g.V1 * scaleY };
        }

        var api = _ctx.DeviceApi;
        // Drain the GPU before swapping the atlas image — same in-flight use-after-free + in-use-descriptor
        // hazard as VkSdfFontAtlas.Grow: with MaxFramesInFlight=2, frame N-1 may still be sampling the old
        // image through this descriptor when frame N grows, and the Adreno X1-85 punishes that by failing
        // the next vkQueueSubmit. This was historically masked by the per-Flush vkDeviceWaitIdle that the
        // upload-ring refactor removed — that drain ran every frame, so it had been incidentally protecting
        // Grow too. Grows are rare (the atlas only doubles), so a targeted device idle here is cheap.
        api.vkDeviceWaitIdle();
        api.vkDestroyImageView(_imageView);
        api.vkDestroyImage(_image);
        api.vkFreeMemory(_imageMemory);
        CreateImage(_atlasWidth, _atlasHeight);
        _ctx.UpdateDescriptorSet(_ctx.DescriptorSet, _imageView, _sampler);

        _dirtyX0 = 0; _dirtyY0 = 0;
        _dirtyX1 = _atlasWidth; _dirtyY1 = _atlasHeight;
    }

    private void EvictAll()
    {
        _glyphs.Clear();
        _cursorX = 0; _cursorY = 0; _rowHeight = 0;
        _staging = new byte[_atlasWidth * _atlasHeight * 4];
        _dirtyX0 = 0; _dirtyY0 = 0;
        _dirtyX1 = _atlasWidth; _dirtyY1 = _atlasHeight;
    }

    private void CreateImage(int width, int height)
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
        api.vkCreateImage(&imageCI, null, out _image).CheckResult();

        api.vkGetImageMemoryRequirements(_image, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out _imageMemory).CheckResult();
        api.vkBindImageMemory(_image, _imageMemory, 0);

        // Defer the Undefined→ShaderReadOnly transition into the next Flush (the frame's own command
        // buffer) instead of side-submitting here — see _needsInitialTransition above.
        _needsInitialTransition = true;

        var viewCI = new VkImageViewCreateInfo(
            _image, VkImageViewType.Image2D, VkFormat.R8G8B8A8Unorm,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out _imageView).CheckResult();
    }

    private void CreateSampler()
    {
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

    private void EnsureUploadBuffer(int slot, ulong size)
    {
        if (_uploadBuffers[slot] != VkBuffer.Null && _uploadBufferSizes[slot] >= size)
            return;

        var api = _ctx.DeviceApi;

        if (_uploadBuffers[slot] != VkBuffer.Null)
        {
            // Slot grows only when the dirty region is larger than the previous peak
            // for this slot — by the time we're here BeginFrame has waited on the
            // matching fence, so destroying the previous allocation is safe.
            api.vkDestroyBuffer(_uploadBuffers[slot]);
            api.vkFreeMemory(_uploadMemories[slot]);
        }

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out _uploadBuffers[slot]).CheckResult();

        api.vkGetBufferMemoryRequirements(_uploadBuffers[slot], out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out _uploadMemories[slot]).CheckResult();
        api.vkBindBufferMemory(_uploadBuffers[slot], _uploadMemories[slot], 0);
        _uploadBufferSizes[slot] = size;
    }

    private void ResetDirtyRegion()
    {
        _dirtyX0 = _atlasWidth; _dirtyY0 = _atlasHeight;
        _dirtyX1 = 0; _dirtyY1 = 0;
    }
}
