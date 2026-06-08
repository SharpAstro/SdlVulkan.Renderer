#if DEBUG
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

// DEBUG-only swapchain readback for the live UI debug inspector (see DebugInspector.cs).
// Mirrors VulkanContext.Offscreen.cs ReadbackOffscreenRgba almost verbatim; the only differences
// are (a) it copies a swapchain image instead of the offscreen target, and (b) the layout
// transitions are PresentSrcKHR <-> TransferSrcOptimal because the image was just presented by
// vkQueuePresentKHR (the offscreen path uses ColorAttachmentOptimal).
//
// Must be called on the RENDER THREAD after EndFrame (after present). The one-shot command buffer
// + vkQueueWaitIdle stalls the queue, which is fine for a debug tool. Requires the swapchain to
// have been created with VkImageUsageFlags.TransferSrc (added under #if DEBUG in CreateSwapchain).
public sealed unsafe partial class VulkanContext
{
    /// <summary>
    /// Copies the most recently presented swapchain image into a freshly-allocated RGBA byte array
    /// (R,G,B,A per pixel, top-to-bottom). Blocks until the GPU finishes the copy. DEBUG-only.
    /// </summary>
    internal byte[] ReadbackSwapchainRgba()
    {
        if (_isOffscreen) throw new InvalidOperationException("ReadbackSwapchainRgba requires a swapchain context");

        var imageIndex = _currentImageIndex;
        var image = _swapchainImages[imageIndex];
        var width = SwapchainWidth;
        var height = SwapchainHeight;
        var pixelCount = (int)(width * height);
        var size = (ulong)(pixelCount * 4);

        // Host-visible staging buffer to receive the image copy.
        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateBuffer(&bufCI, null, out var stagingBuffer).CheckResult();
        DeviceApi.vkGetBufferMemoryRequirements(stagingBuffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out var stagingMemory).CheckResult();
        DeviceApi.vkBindBufferMemory(stagingBuffer, stagingMemory, 0);

        // One-shot command buffer: transition image PresentSrc->TransferSrc, copy, transition back.
        DeviceApi.vkAllocateCommandBuffer(CommandPool, out var cmd).CheckResult();
        VkCommandBufferBeginInfo bi = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        DeviceApi.vkBeginCommandBuffer(cmd, &bi);

        TransitionImageLayout(cmd, image,
            VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.MemoryRead, VkAccessFlags.TransferRead,
            VkPipelineStageFlags.BottomOfPipe, VkPipelineStageFlags.Transfer);

        VkBufferImageCopy region = new()
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D(width, height, 1)
        };
        DeviceApi.vkCmdCopyImageToBuffer(cmd, image, VkImageLayout.TransferSrcOptimal,
            stagingBuffer, 1, &region);

        TransitionImageLayout(cmd, image,
            VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR,
            VkAccessFlags.TransferRead, VkAccessFlags.MemoryRead,
            VkPipelineStageFlags.Transfer, VkPipelineStageFlags.BottomOfPipe);

        DeviceApi.vkEndCommandBuffer(cmd);
        VkSubmitInfo si = new() { commandBufferCount = 1, pCommandBuffers = &cmd };
        DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &si, VkFence.Null).CheckResult();
        DeviceApi.vkQueueWaitIdle(GraphicsQueue);
        DeviceApi.vkFreeCommandBuffers(CommandPool, 1, &cmd);

        // Map and copy out. B8G8R8A8 -> convert to R8G8B8A8 for caller convenience.
        void* mapped;
        DeviceApi.vkMapMemory(stagingMemory, 0, size, 0, &mapped);
        var result = new byte[pixelCount * 4];
        var src = new Span<byte>(mapped, pixelCount * 4);
        for (var i = 0; i < pixelCount; i++)
        {
            result[i * 4 + 0] = src[i * 4 + 2]; // R <- B
            result[i * 4 + 1] = src[i * 4 + 1]; // G
            result[i * 4 + 2] = src[i * 4 + 0]; // B <- R
            result[i * 4 + 3] = src[i * 4 + 3]; // A
        }
        DeviceApi.vkUnmapMemory(stagingMemory);

        DeviceApi.vkDestroyBuffer(stagingBuffer);
        DeviceApi.vkFreeMemory(stagingMemory);
        return result;
    }
}
#endif
