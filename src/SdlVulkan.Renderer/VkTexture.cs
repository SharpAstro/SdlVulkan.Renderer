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
    public static VkTexture CreateDeferred(VulkanContext ctx, ReadOnlySpan<byte> bgraData, int width, int height)
    {
        var api = ctx.DeviceApi;
        var bufferSize = (ulong)(width * height * 4);

        // Create and fill staging buffer
        VkBufferCreateInfo bufCI = new()
        {
            size = bufferSize,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out var stagingBuffer).CheckResult();

        api.vkGetBufferMemoryRequirements(stagingBuffer, out var memReqs);
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
        bgraData.CopyTo(new Span<byte>(mapped, (int)bufferSize));
        api.vkUnmapMemory(stagingMemory);

        // Create device-local image
        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.B8G8R8A8Unorm,
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
            image, VkImageViewType.Image2D, VkFormat.B8G8R8A8Unorm,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out var imageView).CheckResult();

        // Create sampler
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
        api.vkCreateSampler(&samplerCI, null, out var sampler).CheckResult();

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

        CleanupStaging();
        var api = _ctx.DeviceApi;
        _ctx.FreeDescriptorSet(DescriptorSet);
        api.vkDestroySampler(_sampler);
        api.vkDestroyImageView(_imageView);
        api.vkDestroyImage(_image);
        api.vkFreeMemory(_imageMemory);
    }
}
