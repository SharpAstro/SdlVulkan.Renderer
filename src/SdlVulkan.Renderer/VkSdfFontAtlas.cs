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
    /// Default initial atlas dimension, sized so roughly 256 max-extent glyphs
    /// fit before the first <see cref="Grow"/> — enough for typical startup UI
    /// (ASCII + punctuation + common emoji) without the first-frame Grow fallout
    /// that the old fixed 512 hit at higher raster sizes.
    /// </summary>
    private const int DefaultInitialAtlasDim = (int)SdfRasterSize * 16;

    private int _atlasWidth;
    private int _atlasHeight;
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;

    // Single-channel staging buffer (1 byte per pixel)
    private byte[] _staging;

    private int _dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1;
    private bool _needsEviction;

    private VkImage _image;
    private VkDeviceMemory _imageMemory;
    private VkImageView _imageView;
    private VkSampler _sampler;

    private VkBuffer _uploadBuffer;
    private VkDeviceMemory _uploadMemory;
    private ulong _uploadBufferSize;

    // Set whenever CreateImage allocates a fresh VkImage (initial + every Grow).
    // The image starts in VK_IMAGE_LAYOUT_UNDEFINED; first Flush must transition
    // from Undefined (not ShaderReadOnlyOptimal, which requires prior data).
    // Previous code transitioned via ctx.ExecuteOneShot during CreateImage — that
    // worked in isolation but submitting a side command buffer to the graphics
    // queue while the frame's command buffer is in recording state makes some
    // drivers return VK_ERROR_INITIALIZATION_FAILED from the next vkQueueSubmit.
    private bool _needsInitialTransition;

    // Own descriptor set for the SDF atlas texture
    private VkDescriptorSet _descriptorSet;

    public VkImageView ImageView => _imageView;
    public VkSampler Sampler => _sampler;
    public VkDescriptorSet DescriptorSet => _descriptorSet;
    public bool IsDirty => _needsEviction || (_dirtyX0 < _dirtyX1 && _dirtyY0 < _dirtyY1);

    public VkSdfFontAtlas(VulkanContext ctx, ManagedFontRasterizer rasterizer, int initialWidth = DefaultInitialAtlasDim, int initialHeight = DefaultInitialAtlasDim)
    {
        _ctx = ctx;
        _rasterizer = rasterizer;
        _atlasWidth = initialWidth;
        _atlasHeight = initialHeight;
        _staging = new byte[initialWidth * initialHeight]; // 1 byte per pixel
        ResetDirtyRegion();

        CreateImage(initialWidth, initialHeight);
        CreateSampler();
        _descriptorSet = ctx.AllocateDescriptorSet();
        ctx.UpdateDescriptorSet(_descriptorSet, _imageView, _sampler);
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
        if (_dirtyX0 >= _dirtyX1 || _dirtyY0 >= _dirtyY1)
            return;

        var regionW = _dirtyX1 - _dirtyX0;
        var regionH = _dirtyY1 - _dirtyY0;
        var pixelCount = regionW * regionH;

        // Extract dirty region into contiguous buffer (1 byte per pixel)
        var data = new byte[pixelCount];
        for (var row = 0; row < regionH; row++)
        {
            var srcOffset = (_dirtyY0 + row) * _atlasWidth + _dirtyX0;
            var dstOffset = row * regionW;
            Buffer.BlockCopy(_staging, srcOffset, data, dstOffset, regionW);
        }

        var bufferSize = (ulong)pixelCount;

        _ctx.DeviceApi.vkDeviceWaitIdle();
        EnsureUploadBuffer(bufferSize);

        void* mapped;
        _ctx.DeviceApi.vkMapMemory(_uploadMemory, 0, bufferSize, 0, &mapped);
        fixed (byte* pData = data)
            Buffer.MemoryCopy(pData, mapped, bufferSize, bufferSize);
        _ctx.DeviceApi.vkUnmapMemory(_uploadMemory);

        // First Flush after CreateImage: image is still in Undefined layout, so
        // transition from there. Subsequent flushes transition from ShaderReadOnly.
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
        _ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, _uploadBuffer, _image, VkImageLayout.TransferDstOptimal, 1, &region);

        VulkanHelpers.TransitionImageLayout(_ctx.DeviceApi, cmd, _image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        ResetDirtyRegion();
        _unflushedGlyphs.Clear();
    }

    public void Dispose()
    {
        var api = _ctx.DeviceApi;

        _ctx.FreeDescriptorSet(_descriptorSet);

        if (_uploadBuffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(_uploadBuffer);
            api.vkFreeMemory(_uploadMemory);
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
            _needsEviction = true;
            return default;
        }

        // Blit single-channel SDF data into staging buffer
        for (var row = 0; row < glyphHeight; row++)
        {
            var srcOffset = row * glyphWidth;
            var dstOffset = (_cursorY + row) * _atlasWidth + _cursorX;
            Buffer.BlockCopy(bitmap.Alpha, srcOffset, _staging, dstOffset, glyphWidth);
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
            BearingY: bitmap.BearingY,
            Spread: bitmap.Spread);

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

        var newStaging = new byte[_atlasWidth * _atlasHeight];
        for (var row = 0; row < oldHeight; row++)
        {
            var srcOffset = row * oldWidth;
            var dstOffset = row * _atlasWidth;
            Buffer.BlockCopy(_staging, srcOffset, newStaging, dstOffset, oldWidth);
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
        api.vkDestroyImageView(_imageView);
        api.vkDestroyImage(_image);
        api.vkFreeMemory(_imageMemory);
        CreateImage(_atlasWidth, _atlasHeight);
        _ctx.UpdateDescriptorSet(_descriptorSet, _imageView, _sampler);

        _dirtyX0 = 0; _dirtyY0 = 0;
        _dirtyX1 = _atlasWidth; _dirtyY1 = _atlasHeight;
    }

    private void EvictAll()
    {
        _glyphs.Clear();
        _cursorX = 0; _cursorY = 0; _rowHeight = 0;
        _staging = new byte[_atlasWidth * _atlasHeight];
        _dirtyX0 = 0; _dirtyY0 = 0;
        _dirtyX1 = _atlasWidth; _dirtyY1 = _atlasHeight;
    }

    private void CreateImage(int width, int height)
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
        api.vkCreateImage(&imageCI, null, out _image).CheckResult();

        api.vkGetImageMemoryRequirements(_image, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out _imageMemory).CheckResult();
        api.vkBindImageMemory(_image, _imageMemory, 0);

        // Defer the Undefined -> TransferDst transition to the next Flush, which
        // records into the frame's command buffer. A one-shot submit here would
        // collide with an in-recording frame cmd buffer on some drivers
        // (VK_ERROR_INITIALIZATION_FAILED from the next vkQueueSubmit).
        _needsInitialTransition = true;

        // Swizzle R channel into all RGBA channels so the sampler reads the SDF
        // value consistently regardless of which component the shader samples
        var viewCI = new VkImageViewCreateInfo(
            _image, VkImageViewType.Image2D, VkFormat.R8Unorm,
            new VkComponentMapping(VkComponentSwizzle.R, VkComponentSwizzle.R, VkComponentSwizzle.R, VkComponentSwizzle.R),
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out _imageView).CheckResult();
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

    private void EnsureUploadBuffer(ulong size)
    {
        if (_uploadBuffer != VkBuffer.Null && _uploadBufferSize >= size)
            return;

        var api = _ctx.DeviceApi;

        if (_uploadBuffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(_uploadBuffer);
            api.vkFreeMemory(_uploadMemory);
        }

        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out _uploadBuffer).CheckResult();

        api.vkGetBufferMemoryRequirements(_uploadBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out _uploadMemory).CheckResult();
        api.vkBindBufferMemory(_uploadBuffer, _uploadMemory, 0);
        _uploadBufferSize = size;
    }

    private void ResetDirtyRegion()
    {
        _dirtyX0 = _atlasWidth; _dirtyY0 = _atlasHeight;
        _dirtyX1 = 0; _dirtyY1 = 0;
    }
}
