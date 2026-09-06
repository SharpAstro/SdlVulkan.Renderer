using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// A Vulkan texture created from raw BGRA pixel data.
/// Owns its own VkImage, VkImageView, VkSampler, VkDeviceMemory, and VkDescriptorSet.
///
/// Supports two creation modes:
/// - CreateFromBgra: immediate upload via one-shot command (blocks GPU — use sparingly)
/// - CreateDeferred + RecordUpload + CleanupStaging: non-blocking, records into frame command buffer
/// </summary>
public sealed unsafe class VkTexture : IDisposable
{
    public VkDescriptorSet DescriptorSet { get; }

    /// <summary>
    /// A descriptor set binding THIS texture at 0 and <paramref name="mask"/> at 1, for
    /// <see cref="VkPipelineSet.MaskedPipeline"/>. The caller owns the set and returns it with
    /// <see cref="VulkanContext.FreeMaskedDescriptorSet"/>; neither texture is captured, so both must
    /// outlive any frame that draws with it.
    /// </summary>
    /// <remarks>
    /// A method here rather than the image views being made public: a view handed out is a view
    /// somebody can bind after the texture that owns it has been deferred for destruction, which is
    /// exactly the use-after-free the deferred-destroy machinery exists to prevent.
    /// </remarks>
    public VkDescriptorSet CreateMaskedDescriptorSet(VkTexture mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        var set = _ctx.AllocateMaskedDescriptorSet();
        _ctx.UpdateMaskedDescriptorSet(set, _imageView, _sampler, mask._imageView, mask._sampler);
        return set;
    }
    public int Width { get; }
    public int Height { get; }

    /// <summary>True once the upload commands have been recorded and the staging buffer can be freed after submit.</summary>
    public bool IsUploaded { get; private set; }

    private readonly VulkanContext _ctx;
    private VkImage _image;
    private VkDeviceMemory _imageMemory;
    private VkImageView _imageView;
    private VkSampler _sampler;

    // Staging resources — kept alive until upload is submitted, then freed
    private VkBuffer _stagingBuffer;
    private VkDeviceMemory _stagingMemory;
    private bool _disposed;

    private VkTexture(VulkanContext ctx, VkImage image, VkDeviceMemory imageMemory,
        VkImageView imageView, VkSampler sampler, VkDescriptorSet descriptorSet,
        int width, int height, VkBuffer stagingBuffer, VkDeviceMemory stagingMemory, bool uploaded)
    {
        _ctx = ctx;
        _image = image;
        _imageMemory = imageMemory;
        _imageView = imageView;
        _sampler = sampler;
        DescriptorSet = descriptorSet;
        Width = width;
        Height = height;
        _stagingBuffer = stagingBuffer;
        _stagingMemory = stagingMemory;
        IsUploaded = uploaded;
    }

    /// <summary>
    /// Creates a texture with deferred upload. Call RecordUpload() with the frame's command buffer
    /// before the render pass to schedule the GPU copy. No vkQueueWaitIdle — zero blocking.
    /// Call CleanupStaging() after the frame is submitted to free the staging buffer.
    /// </summary>
    /// <param name="format">Pixel format of <paramref name="pixelData"/>. Defaults to
    /// <see cref="VkFormat.B8G8R8A8Unorm"/> for historical reasons; callers with RGBA byte
    /// layout (the common CPU-renderer output) should pass <see cref="VkFormat.R8G8B8A8Unorm"/>
    /// and skip any CPU-side swizzle — letting the driver read the bytes directly is cheaper
    /// than a per-pixel swap loop.</param>
    /// <summary>
    /// Bytes one texel occupies in <paramref name="format"/>, for sizing a staging buffer.
    /// </summary>
    /// <remarks>
    /// Deliberately a closed set that THROWS on anything else, rather than assuming four. The upload
    /// path used to take a format and size its staging buffer at four bytes a pixel regardless, which
    /// went wrong in two different directions and neither is the one it looks like.
    /// <para>WIDER than four bytes, and the buffer is too small: the copy into it throws, so no format
    /// above 32 bits could be uploaded at all.</para>
    /// <para>NARROWER, and the buffer is oversized but the bytes still land where the image copy reads
    /// them, since Vulkan derives the copy's extent from the image rather than the buffer. So a
    /// single-channel texture rendered CORRECTLY and quietly cost four times the staging it needed --
    /// which for the masks this exists to carry is megabytes per tile, on exactly the path that was
    /// trying to save them.</para>
    /// <para>Adding a format here is one line. Being wrong about one is a throw on the wide side and
    /// wasted memory on the narrow, so it fails loudly in the direction that matters.</para>
    /// </remarks>
    private static int BytesPerPixel(VkFormat format) => format switch
    {
        VkFormat.R8Unorm or VkFormat.R8Snorm or VkFormat.R8Uint or VkFormat.R8Sint or VkFormat.R8Srgb => 1,
        VkFormat.R8G8Unorm or VkFormat.R16Unorm or VkFormat.R16Sfloat => 2,
        VkFormat.B8G8R8A8Unorm or VkFormat.B8G8R8A8Srgb
            or VkFormat.R8G8B8A8Unorm or VkFormat.R8G8B8A8Srgb
            or VkFormat.R32Sfloat or VkFormat.R16G16Sfloat => 4,
        VkFormat.R16G16B16A16Sfloat or VkFormat.R32G32Sfloat => 8,
        VkFormat.R32G32B32A32Sfloat => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format,
            "texel size unknown; add it to VkTexture.BytesPerPixel rather than letting the staging "
            + "buffer be sized wrong")
    };

    public static VkTexture CreateDeferred(VulkanContext ctx, ReadOnlySpan<byte> pixelData, int width, int height,
        VkFormat format = VkFormat.B8G8R8A8Unorm)
    {
        var api = ctx.DeviceApi;
        var bufferSize = (ulong)((long)width * height * BytesPerPixel(format));

        // Create and fill staging buffer
        VkBufferCreateInfo bufCI = new()
        {
            size = bufferSize,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out var stagingBuffer).CheckResult();

        api.vkGetBufferMemoryRequirements(stagingBuffer, out var memReqs);
        ctx.GraphicsDevice.NoteBufferCreated();
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out var stagingMemory).CheckResult();
        api.vkBindBufferMemory(stagingBuffer, stagingMemory, 0);

        void* mapped;
        api.vkMapMemory(stagingMemory, 0, bufferSize, 0, &mapped);
        pixelData.CopyTo(new Span<byte>(mapped, (int)bufferSize));
        api.vkUnmapMemory(stagingMemory);

        // Create device-local image
        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D((uint)width, (uint)height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined
        };
        api.vkCreateImage(&imageCI, null, out var image).CheckResult();
        ctx.GraphicsDevice.NoteImageCreated();

        api.vkGetImageMemoryRequirements(image, out var imgMemReqs);
        VkMemoryAllocateInfo imgAllocInfo = new()
        {
            allocationSize = imgMemReqs.size,
            memoryTypeIndex = ctx.FindMemoryType(imgMemReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&imgAllocInfo, null, out var imageMemory).CheckResult();
        api.vkBindImageMemory(image, imageMemory, 0);

        // Create image view
        var viewCI = new VkImageViewCreateInfo(
            image, VkImageViewType.Image2D, format,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out var imageView).CheckResult();

        // Samplers carry no per-image state, so every texture asking for its own identical one only
        // burned maxSamplerAllocationCount (commonly 4096) — reached by a single page carrying a few
        // thousand small images. Share the device's.
        var sampler = ctx.LinearClampSampler;

        // Allocate and update descriptor set
        var descriptorSet = ctx.AllocateDescriptorSet();
        ctx.UpdateDescriptorSet(descriptorSet, imageView, sampler);

        return new VkTexture(ctx, image, imageMemory, imageView, sampler, descriptorSet,
            width, height, stagingBuffer, stagingMemory, uploaded: false);
    }

    /// <summary>
    /// Records the staging→image copy commands into the given command buffer.
    /// Must be called BEFORE BeginRenderPass (transfers can't happen inside a render pass).
    /// </summary>
    public void RecordUpload(VkCommandBuffer cmd)
    {
        if (IsUploaded) return;

        var api = _ctx.DeviceApi;

        VulkanHelpers.TransitionImageLayout(api, cmd, _image,
            VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D((uint)Width, (uint)Height, 1)
        };
        api.vkCmdCopyBufferToImage(cmd, _stagingBuffer, _image, VkImageLayout.TransferDstOptimal, 1, &region);

        VulkanHelpers.TransitionImageLayout(api, cmd, _image,
            VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        IsUploaded = true;
    }

    /// <summary>
    /// Frees the staging buffer after the frame containing the upload has been submitted.
    /// Safe to call multiple times.
    /// </summary>
    public void CleanupStaging()
    {
        if (_stagingBuffer == VkBuffer.Null) return;
        var api = _ctx.DeviceApi;
        api.vkDestroyBuffer(_stagingBuffer);
        api.vkFreeMemory(_stagingMemory);
        _ctx.GraphicsDevice.NoteBufferDestroyed();
        _stagingBuffer = VkBuffer.Null;
        _stagingMemory = VkDeviceMemory.Null;
    }

    /// <summary>
    /// Legacy: creates and uploads immediately via one-shot command (blocks GPU).
    /// Use CreateDeferred + RecordUpload for non-blocking uploads.
    /// </summary>
    public static VkTexture CreateFromBgra(VulkanContext ctx, ReadOnlySpan<byte> bgraData, int width, int height)
    {
        var tex = CreateDeferred(ctx, bgraData, width, height);
        ctx.ExecuteOneShot(cmd => tex.RecordUpload(cmd));
        tex.CleanupStaging();
        return tex;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IsUploaded = false; // prevent use-after-free via stale references
        CleanupStaging();
        // Deferred, not destroyed: a texture is routinely disposed in the same frame that drew it (a
        // consumer swaps an image and drops the old one), and the frame's command buffer already holds
        // its descriptor set. Destroying now would submit that frame against freed objects -- the GPU
        // fault behind the TianWen viewer's watchdog crashes. The context destroys these once every frame
        // that could reference them has retired (see VulkanContext.DeferredDestroy).
        // _sampler is the device's shared sampler — outlives every texture, never destroyed here.
        var device = _ctx.GraphicsDevice;
        _ctx.DeferDestroy(view: _imageView, image: _image, memory: _imageMemory, descriptorSet: DescriptorSet);
        _ctx.DeferDestroy(device.NoteImageDestroyed);
    }
}
