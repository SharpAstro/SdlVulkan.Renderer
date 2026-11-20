using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// A texture made on the GPU from Y'CbCr planes (YCbCrPlanes), by the compute shader Shaders/ycbcrhdr.comp: the colour
// conversion, the transfer and an optional whole-factor reduction run per texel there instead of per pixel on the
// processor, and the planes (two bytes a sample) are all that crosses to the GPU, not a float raster of 16 bytes a pixel.
//
// Lifecycle, the same as a deferred upload's. Creation fills a host-visible buffer with the planes, the code table and
// a zeroed peak word, and makes the image. RecordUpload records the dispatch into the frame's command buffer (before
// the render pass, like every upload) and marks the texture uploaded, so it can be drawn in that frame. When that
// frame has retired (VulkanContext.DeferDestroy), the peak the shader measured is read into MeasuredPeak and the
// buffer and the compute set are released. A dropped frame requeues the dispatch as it requeues a copy; the
// generation counter keeps the dropped frame's retirement from reading a peak the requeued dispatch has not written.
public sealed unsafe partial class VkTexture
{
    /// <summary>
    /// The brightest R, G or B of any texel, in the texture's own units (for <see cref="VkRenderer.DrawHdrImage"/>,
    /// 1.0 = 203 cd/m2, so the source peak in cd/m2 is this times 203): measured by the conversion that made a texture
    /// with <see cref="CreateDeferredFromYCbCr"/>. Null until the frame that ran the conversion has retired, and always
    /// null for a texture made any other way.
    /// </summary>
    public float? MeasuredPeak { get; private set; }

    private sealed class Conversion
    {
        public required VkYCbCrConverter Converter { get; init; }
        public required VkBuffer Buffer { get; init; }
        public required VkDeviceMemory Memory { get; init; }
        public required VkDescriptorSet Set { get; init; }
        public required ulong PeakOffset { get; init; }
        public required uint[] Push { get; init; }
        public int Generation { get; set; }
    }

    private Conversion? _conversion;

    /// <summary>
    /// A float texture (<see cref="VkFormat.R16G16B16A16Sfloat"/>, for <see cref="VkRenderer.DrawHdrImage"/>) made from
    /// <paramref name="planes"/> on the GPU, reduced by a whole <paramref name="reduce"/> factor: each texel is the
    /// average, in linear light, of a <paramref name="reduce"/> x <paramref name="reduce"/> block of converted pixels,
    /// and a right or bottom remainder narrower than a block is dropped. Queue it with
    /// <see cref="VulkanContext.QueueTextureUpload"/> as any deferred texture; the conversion runs in that frame, and
    /// <see cref="MeasuredPeak"/> is known once the frame has retired.
    /// </summary>
    /// <exception cref="ArgumentException">The planes do not describe a picture (<see cref="YCbCrPlanes"/>).</exception>
    /// <exception cref="NotSupportedException">The device cannot run the conversion: no compute on the graphics queue,
    /// or a plane larger than one storage-buffer descriptor reaches (at least 128 MiB, 64 Mpixel of luma).</exception>
    public static VkTexture CreateDeferredFromYCbCr(VulkanContext ctx, YCbCrPlanes planes, int reduce = 1)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(planes);
        planes.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(reduce, 1);
        int outWidth = planes.Width / reduce, outHeight = planes.Height / reduce;
        if (outWidth == 0 || outHeight == 0)
            throw new ArgumentOutOfRangeException(nameof(reduce), reduce, $"Reducing {planes.Width}x{planes.Height} by {reduce} leaves no texel.");

        var device = ctx.GraphicsDevice;
        var converter = device.YCbCrConverter;
        var api = ctx.DeviceApi;

        // One buffer, five sections, each starting where a storage descriptor may.
        var alignment = converter.StorageAlignment;
        ulong Align(ulong offset) => (offset + alignment - 1) / alignment * alignment;
        static ulong Words(long samples) => ((ulong)samples * 2 + 3) / 4 * 4;
        var lumaBytes = Words(planes.Luma.Length);
        var chromaBytes = planes.IsMonochrome ? 4 : Words(planes.Cb.Length); // a monochrome picture binds a dummy word
        var tableBytes = (ulong)planes.CodeToLinear.Length * sizeof(float);
        var cbOffset = Align(lumaBytes);
        var crOffset = Align(cbOffset + chromaBytes);
        var tableOffset = Align(crOffset + chromaBytes);
        var peakOffset = Align(tableOffset + tableBytes);
        var total = peakOffset + sizeof(uint);
        var maxRange = device.MaxStorageBufferRange;
        if (lumaBytes > maxRange)
            throw new NotSupportedException($"A {planes.Width}x{planes.Height} luma plane is {lumaBytes} bytes, more than one storage buffer reaches here ({maxRange}).");

        VkBufferCreateInfo bufferCI = new()
        {
            size = total,
            usage = VkBufferUsageFlags.StorageBuffer,
            sharingMode = VkSharingMode.Exclusive,
        };
        api.vkCreateBuffer(&bufferCI, null, out var buffer).CheckResult();
        device.NoteBufferCreated();
        api.vkGetBufferMemoryRequirements(buffer, out var bufferNeeds);
        VkMemoryAllocateInfo bufferAlloc = new()
        {
            allocationSize = bufferNeeds.size,
            memoryTypeIndex = ctx.FindMemoryType(bufferNeeds.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent),
        };
        api.vkAllocateMemory(&bufferAlloc, null, out var bufferMemory).CheckResult();
        api.vkBindBufferMemory(buffer, bufferMemory, 0);

        byte* mapped;
        api.vkMapMemory(bufferMemory, 0, total, 0, (void**)&mapped).CheckResult();
        MemoryMarshal.AsBytes(planes.Luma.Span).CopyTo(new Span<byte>(mapped, checked((int)lumaBytes)));
        if (!planes.IsMonochrome)
        {
            MemoryMarshal.AsBytes(planes.Cb.Span).CopyTo(new Span<byte>(mapped + cbOffset, checked((int)chromaBytes)));
            MemoryMarshal.AsBytes(planes.Cr.Span).CopyTo(new Span<byte>(mapped + crOffset, checked((int)chromaBytes)));
        }
        MemoryMarshal.AsBytes(planes.CodeToLinear.Span).CopyTo(new Span<byte>(mapped + tableOffset, checked((int)tableBytes)));
        *(uint*)(mapped + peakOffset) = 0;
        api.vkUnmapMemory(bufferMemory);

        VkImageCreateInfo imageCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R16G16B16A16Sfloat,
            extent = new VkExtent3D((uint)outWidth, (uint)outHeight, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            // TransferSrc only so a test can read the result back (ReadbackRgbaHalf).
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        api.vkCreateImage(&imageCI, null, out var image).CheckResult();
        device.NoteImageCreated();
        api.vkGetImageMemoryRequirements(image, out var imageNeeds);
        VkMemoryAllocateInfo imageAlloc = new()
        {
            allocationSize = imageNeeds.size,
            memoryTypeIndex = ctx.FindMemoryType(imageNeeds.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal),
        };
        api.vkAllocateMemory(&imageAlloc, null, out var imageMemory).CheckResult();
        api.vkBindImageMemory(image, imageMemory, 0);
        var viewCI = new VkImageViewCreateInfo(image, VkImageViewType.Image2D, VkFormat.R16G16B16A16Sfloat,
            VkComponentMapping.Rgba, new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        api.vkCreateImageView(&viewCI, null, out var view).CheckResult();

        var sampler = ctx.LinearClampSampler;
        var descriptorSet = ctx.AllocateDescriptorSet();
        ctx.UpdateDescriptorSet(descriptorSet, view, sampler);

        var computeSet = converter.Allocate();
        ReadOnlySpan<(ulong, ulong)> sections =
        [
            (0, lumaBytes),
            (cbOffset, chromaBytes),
            (planes.IsMonochrome ? cbOffset : crOffset, chromaBytes),
            (tableOffset, tableBytes),
            (peakOffset, sizeof(uint)),
        ];
        converter.Write(computeSet, buffer, sections, view);

        var flags = (planes.FullRange ? 1u : 0u) | (planes.IdentityMatrix ? 2u : 0u) | (planes.IsMonochrome ? 4u : 0u);
        uint[] push =
        [
            (uint)planes.Width, (uint)planes.Height, (uint)planes.ChromaWidth,
            (uint)planes.ChromaShiftX, (uint)planes.ChromaShiftY, (uint)reduce, (uint)planes.BitDepth, flags,
            BitConverter.SingleToUInt32Bits((float)planes.Kr), BitConverter.SingleToUInt32Bits((float)planes.Kb),
        ];

        return new VkTexture(ctx, image, imageMemory, view, sampler, descriptorSet, outWidth, outHeight,
            VkBuffer.Null, VkDeviceMemory.Null, uploaded: false)
        {
            _conversion = new Conversion
            {
                Converter = converter,
                Buffer = buffer,
                Memory = bufferMemory,
                Set = computeSet,
                PeakOffset = peakOffset,
                Push = push,
            },
        };
    }

    /// <summary>
    /// <see cref="CreateDeferredFromYCbCr"/> run at once through a one-shot command (blocks until the GPU is done), with
    /// <see cref="MeasuredPeak"/> known on return. For tools and tests; a frame loop queues the deferred one.
    /// </summary>
    public static VkTexture CreateFromYCbCr(VulkanContext ctx, YCbCrPlanes planes, int reduce = 1)
    {
        var texture = CreateDeferredFromYCbCr(ctx, planes, reduce);
        if (texture._conversion is not { } conversion) throw new InvalidOperationException("A Y'CbCr texture without its conversion.");
        try
        {
            ctx.ExecuteOneShot(cmd => texture.RecordConversion(cmd, conversion));
        }
        catch (VkException ex) when (ex.Result != VkResult.Timeout)
        {
            // The submit did not take, so nothing references the texture: free it rather than leak it.
            texture.Dispose();
            throw;
        }
        texture.IsUploaded = true;
        texture.RetireConversion(conversion, conversion.Generation);
        return texture;
    }

    /// <summary>The dispatch, in a frame: provisional until the frame reaches the queue, read back once it retires.</summary>
    private void RecordConversionIntoFrame(VkCommandBuffer cmd, Conversion conversion)
    {
        RecordConversion(cmd, conversion);
        IsUploaded = true;
        var generation = ++conversion.Generation;
        _ctx.OnFrameDropped(cmd, _requeueUpload ??= RequeueUpload);
        _ctx.DeferDestroy(() => RetireConversion(conversion, generation));
    }

    private void RecordConversion(VkCommandBuffer cmd, Conversion conversion)
    {
        var api = _ctx.DeviceApi;
        var converter = conversion.Converter;
        var whole = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);

        // Whatever the image held is discarded: the shader writes every texel.
        VkImageMemoryBarrier toGeneral = new()
        {
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.General,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _image,
            subresourceRange = whole,
            srcAccessMask = 0,
            dstAccessMask = VkAccessFlags.ShaderWrite,
        };
        api.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.ComputeShader, 0,
            0, null, 0, null, 1, &toGeneral);

        api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Compute, converter.Pipeline);
        var set = conversion.Set;
        api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, converter.PipelineLayout, 0, 1, &set, 0, null);
        fixed (uint* push = conversion.Push)
            api.vkCmdPushConstants(cmd, converter.PipelineLayout, VkShaderStageFlags.Compute, 0, VkYCbCrConverter.PushBytes, push);
        api.vkCmdDispatch(cmd,
            ((uint)Width + VkYCbCrConverter.GroupSize - 1) / VkYCbCrConverter.GroupSize,
            ((uint)Height + VkYCbCrConverter.GroupSize - 1) / VkYCbCrConverter.GroupSize, 1);

        // The image to the fragment shaders that draw it, the peak word to the host that reads it after the fence.
        VkImageMemoryBarrier toSampled = new()
        {
            oldLayout = VkImageLayout.General,
            newLayout = VkImageLayout.ShaderReadOnlyOptimal,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _image,
            subresourceRange = whole,
            srcAccessMask = VkAccessFlags.ShaderWrite,
            dstAccessMask = VkAccessFlags.ShaderRead,
        };
        VkBufferMemoryBarrier peakToHost = new()
        {
            srcAccessMask = VkAccessFlags.ShaderWrite,
            dstAccessMask = VkAccessFlags.HostRead,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            buffer = conversion.Buffer,
            offset = conversion.PeakOffset,
            size = sizeof(uint),
        };
        api.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.ComputeShader,
            VkPipelineStageFlags.FragmentShader | VkPipelineStageFlags.Host, 0,
            0, null, 1, &peakToHost, 1, &toSampled);
    }

    /// <summary>
    /// The frame (or one-shot) that ran <paramref name="generation"/> of the conversion has retired: read the peak and
    /// let the buffer and the compute set go. Nothing for a generation since requeued, or a texture since disposed.
    /// </summary>
    private void RetireConversion(Conversion conversion, int generation)
    {
        if (_disposed || !IsUploaded || !ReferenceEquals(_conversion, conversion) || generation != conversion.Generation) return;

        var api = _ctx.DeviceApi;
        void* mapped;
        api.vkMapMemory(conversion.Memory, conversion.PeakOffset, sizeof(uint), 0, &mapped).CheckResult();
        MeasuredPeak = BitConverter.UInt32BitsToSingle(*(uint*)mapped);
        api.vkUnmapMemory(conversion.Memory);

        // Retired, so no command buffer can still read them: released now rather than deferred again.
        _conversion = null;
        api.vkDestroyBuffer(conversion.Buffer);
        api.vkFreeMemory(conversion.Memory);
        _ctx.GraphicsDevice.NoteBufferDestroyed();
        conversion.Converter.Free(conversion.Set);
    }

    /// <summary>Disposal before the conversion retired: its buffer and set go once every frame that may use them has.</summary>
    private void ReleaseConversion()
    {
        if (_conversion is not { } conversion) return;
        _conversion = null;
        _ctx.DeferDestroy(buffer: conversion.Buffer, memory: conversion.Memory);
        _ctx.DeferDestroy(_ctx.GraphicsDevice.NoteBufferDestroyed);
        _ctx.DeferDestroy(() => conversion.Converter.Free(conversion.Set));
    }

    /// <summary>
    /// The texels as written, four halves a texel (R, G, B, A), top row first, read through a one-shot copy. Only a
    /// texture made by <see cref="CreateFromYCbCr"/> or <see cref="CreateDeferredFromYCbCr"/> (and uploaded) can be
    /// read; for tests.
    /// </summary>
    internal Half[] ReadbackRgbaHalf()
    {
        if (!IsUploaded) throw new InvalidOperationException("Nothing written yet.");
        var api = _ctx.DeviceApi;
        var device = _ctx.GraphicsDevice;
        var bytes = (ulong)Width * (ulong)Height * 8;

        VkBufferCreateInfo bufferCI = new() { size = bytes, usage = VkBufferUsageFlags.TransferDst, sharingMode = VkSharingMode.Exclusive };
        api.vkCreateBuffer(&bufferCI, null, out var buffer).CheckResult();
        device.NoteBufferCreated();
        api.vkGetBufferMemoryRequirements(buffer, out var needs);
        VkMemoryAllocateInfo alloc = new()
        {
            allocationSize = needs.size,
            memoryTypeIndex = _ctx.FindMemoryType(needs.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent),
        };
        api.vkAllocateMemory(&alloc, null, out var memory).CheckResult();
        api.vkBindBufferMemory(buffer, memory, 0);
        try
        {
            var image = _image;
            var (width, height) = ((uint)Width, (uint)Height);
            _ctx.ExecuteOneShot(cmd =>
            {
                var whole = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);
                VkImageMemoryBarrier toSource = new()
                {
                    oldLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    newLayout = VkImageLayout.TransferSrcOptimal,
                    srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                    dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                    image = image,
                    subresourceRange = whole,
                    srcAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite,
                    dstAccessMask = VkAccessFlags.TransferRead,
                };
                api.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.AllCommands, VkPipelineStageFlags.Transfer, 0,
                    0, null, 0, null, 1, &toSource);
                VkBufferImageCopy region = new()
                {
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                    imageExtent = new VkExtent3D(width, height, 1),
                };
                api.vkCmdCopyImageToBuffer(cmd, image, VkImageLayout.TransferSrcOptimal, buffer, 1, &region);
                VkImageMemoryBarrier back = toSource with
                {
                    oldLayout = VkImageLayout.TransferSrcOptimal,
                    newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    srcAccessMask = VkAccessFlags.TransferRead,
                    dstAccessMask = VkAccessFlags.ShaderRead,
                };
                api.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.FragmentShader, 0,
                    0, null, 0, null, 1, &back);
            });
            var texels = new Half[Width * Height * 4];
            void* mapped;
            api.vkMapMemory(memory, 0, bytes, 0, &mapped).CheckResult();
            new ReadOnlySpan<Half>(mapped, texels.Length).CopyTo(texels);
            api.vkUnmapMemory(memory);
            return texels;
        }
        finally
        {
            api.vkDestroyBuffer(buffer);
            api.vkFreeMemory(memory);
            device.NoteBufferDestroyed();
        }
    }
}
