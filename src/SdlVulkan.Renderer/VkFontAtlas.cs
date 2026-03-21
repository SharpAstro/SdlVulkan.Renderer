using System.Text;
using DIR.Lib;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

internal sealed unsafe class VkFontAtlas : IDisposable
{
    private readonly record struct GlyphKey(string Font, float Size, Rune Character);

    internal readonly record struct GlyphInfo(float U0, float V0, float U1, float V1, int Width, int Height, float AdvanceX, int BearingX, int BearingY);

    private readonly VulkanContext _ctx;
    private readonly FreeTypeGlyphRasterizer _rasterizer = new();
    private readonly Dictionary<GlyphKey, GlyphInfo> _glyphs = new();
    private readonly HashSet<GlyphKey> _unflushedGlyphs = new();

    private const int MaxAtlasSize = 2048;

    private int _atlasWidth;
    private int _atlasHeight;
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;

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

    public VkImageView ImageView => _imageView;
    public VkSampler Sampler => _sampler;

    public VkFontAtlas(VulkanContext ctx, int initialWidth = 512, int initialHeight = 512)
    {
        _ctx = ctx;
        _atlasWidth = initialWidth;
        _atlasHeight = initialHeight;
        _staging = new byte[initialWidth * initialHeight * 4];
        ResetDirtyRegion();

        CreateImage(initialWidth, initialHeight);
        CreateSampler();
        ctx.UpdateDescriptorSet(_imageView, _sampler);
    }

    /// <summary>
    /// Call at the start of each frame to handle deferred eviction.
    /// This ensures no stale UV coordinates exist in the current frame's batch.
    /// </summary>
    public void BeginFrame()
    {
        if (_needsEviction)
        {
            Console.Error.WriteLine($"[FontAtlas] BeginFrame: deferred eviction triggered, atlas {_atlasWidth}x{_atlasHeight}, {_glyphs.Count} glyphs");
            EvictAll();
            _needsEviction = false;
        }
    }

    /// <summary>
    /// Gets glyph info, rasterizing into the staging buffer if needed.
    /// Use <paramref name="skipUnflushed"/> in draw loops to avoid sampling stale GPU texture data.
    /// </summary>
    public GlyphInfo GetGlyph(string fontPath, float fontSize, Rune character, bool skipUnflushed = false)
    {
        fontSize = MathF.Round(fontSize);
        var key = new GlyphKey(fontPath, fontSize, character);
        if (_glyphs.TryGetValue(key, out var existing))
        {
            // Cache hit — safe to draw only if this glyph has been flushed to GPU
            if (skipUnflushed && _unflushedGlyphs.Contains(key))
            {
                Console.Error.WriteLine($"[FontAtlas] Skip unflushed (hit): '{character}' size={fontSize}");
                return existing with { Width = 0 }; // metrics preserved for advance, but skip quad
            }
            return existing;
        }
        Console.Error.WriteLine($"[FontAtlas] Cache miss: '{character}' size={fontSize} cursor=({_cursorX},{_cursorY}) glyphs={_glyphs.Count}");
        var result = RasterizeGlyph(key);
        if (skipUnflushed && result.Width > 0)
        {
            Console.Error.WriteLine($"[FontAtlas] Skip unflushed (new): '{character}' size={fontSize}");
            return result with { Width = 0 }; // just rasterized, not flushed yet — skip quad
        }
        return result;
    }

    public bool IsDirty => _needsEviction || (_dirtyX0 < _dirtyX1 && _dirtyY0 < _dirtyY1);

    public void Flush(VkCommandBuffer cmd)
    {
        if (_dirtyX0 >= _dirtyX1 || _dirtyY0 >= _dirtyY1)
            return;

        var regionW = _dirtyX1 - _dirtyX0;
        var regionH = _dirtyY1 - _dirtyY0;
        Console.Error.WriteLine($"[FontAtlas] Flush: uploading dirty region ({_dirtyX0},{_dirtyY0})-({_dirtyX1},{_dirtyY1}) = {regionW}x{regionH} px, atlas {_atlasWidth}x{_atlasHeight}");
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

        // Wait for any in-flight command buffers to finish reading the upload buffer
        // before overwriting it. With MaxFramesInFlight=2, the previous frame's
        // vkCmdCopyBufferToImage may still be reading the shared upload buffer.
        _ctx.DeviceApi.vkDeviceWaitIdle();

        EnsureUploadBuffer(bufferSize);

        void* mapped;
        _ctx.DeviceApi.vkMapMemory(_uploadMemory, 0, bufferSize, 0, &mapped);
        fixed (byte* pRgba = rgba)
            Buffer.MemoryCopy(pRgba, mapped, bufferSize, bufferSize);
        _ctx.DeviceApi.vkUnmapMemory(_uploadMemory);

        TransitionImageLayout(cmd, _image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal);

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

        TransitionImageLayout(cmd, _image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        ResetDirtyRegion();
        _unflushedGlyphs.Clear();
    }

    public void Dispose()
    {
        _rasterizer.Dispose();

        var api = _ctx.DeviceApi;

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

    private GlyphInfo RasterizeGlyph(GlyphKey key)
    {
        if (Rune.IsWhiteSpace(key.Character))
        {
            var refGlyph = GetGlyph(key.Font, key.Size, new Rune('n'));
            var info = new GlyphInfo(0, 0, 0, 0, 0, 0, refGlyph.AdvanceX, 0, 0);
            _glyphs[key] = info;
            return info;
        }

        var bitmap = _rasterizer.RasterizeGlyph(key.Font, key.Size, key.Character);
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
            Console.Error.WriteLine($"[FontAtlas] RasterizeGlyph: atlas full at max {_atlasWidth}x{_atlasHeight}, deferring eviction. Glyph: '{key.Character}' font={key.Font} size={key.Size}");
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
        Console.Error.WriteLine($"[FontAtlas] Grow: {oldWidth}x{oldHeight} -> {_atlasWidth}x{_atlasHeight}, {_glyphs.Count} glyphs, cursor ({_cursorX},{_cursorY})");

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
        api.vkDestroyImageView(_imageView);
        api.vkDestroyImage(_image);
        api.vkFreeMemory(_imageMemory);
        CreateImage(_atlasWidth, _atlasHeight);
        _ctx.UpdateDescriptorSet(_imageView, _sampler);

        _dirtyX0 = 0; _dirtyY0 = 0;
        _dirtyX1 = _atlasWidth; _dirtyY1 = _atlasHeight;
    }

    private void EvictAll()
    {
        Console.Error.WriteLine($"[FontAtlas] EvictAll: clearing {_glyphs.Count} glyphs, atlas {_atlasWidth}x{_atlasHeight}, cursor ({_cursorX},{_cursorY})");
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

        _ctx.ExecuteOneShot(cmd =>
            TransitionImageLayout(cmd, _image, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal));

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

    private void TransitionImageLayout(VkCommandBuffer cmd, VkImage image,
        VkImageLayout oldLayout, VkImageLayout newLayout)
    {
        VkImageMemoryBarrier barrier = new()
        {
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1)
        };

        VkPipelineStageFlags srcStage, dstStage;

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            srcStage = VkPipelineStageFlags.TopOfPipe;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.TransferWrite;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.Transfer;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else if (oldLayout == VkImageLayout.ShaderReadOnlyOptimal && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.ShaderRead;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
            srcStage = VkPipelineStageFlags.FragmentShader;
            dstStage = VkPipelineStageFlags.Transfer;
        }
        else if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            srcStage = VkPipelineStageFlags.TopOfPipe;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }
        else
        {
            throw new ArgumentException($"Unsupported layout transition: {oldLayout} -> {newLayout}");
        }

        _ctx.DeviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0,
            0, null, 0, null, 1, &barrier);
    }

    private void ResetDirtyRegion()
    {
        _dirtyX0 = _atlasWidth; _dirtyY0 = _atlasHeight;
        _dirtyX1 = 0; _dirtyY1 = 0;
    }
}
