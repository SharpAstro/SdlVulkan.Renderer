using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Shared Vulkan helper methods used by VkFontAtlas and VkTexture.
/// </summary>
internal static unsafe class VulkanHelpers
{
    /// <summary>
    /// Clears a single-mip, single-layer colour image (already in <see cref="VkImageLayout.TransferDstOptimal"/>) to
    /// transparent black. For an atlas on its first upload: a Vulkan image starts with UNDEFINED contents and the
    /// atlases upload only the rectangles they wrote, so every texel no glyph has covered yet keeps whatever the driver
    /// handed over. A linear-filtered glyph blends its edge with the texel just outside it, which is one of those.
    /// NVIDIA and Android drivers happen to return zeroed memory, so it never showed; Metal (MoltenVK on iOS) does not,
    /// and small glyphs grew a tinted line on their bottom and right edges (7.95).
    /// </summary>
    public static void ClearToTransparent(VkDeviceApi api, VkCommandBuffer cmd, VkImage image)
    {
        var transparent = new VkClearColorValue(0f, 0f, 0f, 0f);
        var whole = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);
        api.vkCmdClearColorImage(cmd, image, VkImageLayout.TransferDstOptimal, &transparent, 1, &whole);

        // The upload that follows writes the same image at the same (transfer) stage: without a barrier the two writes
        // are unordered and the clear could land after the copy (the validation layer's WRITE_AFTER_WRITE). The layout
        // stays TransferDstOptimal.
        VkImageMemoryBarrier barrier = new()
        {
            oldLayout = VkImageLayout.TransferDstOptimal,
            newLayout = VkImageLayout.TransferDstOptimal,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = whole,
            srcAccessMask = VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.TransferWrite,
        };
        api.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Transfer, 0,
            0, null, 0, null, 1, &barrier);
    }

    public static void TransitionImageLayout(VkDeviceApi api, VkCommandBuffer cmd, VkImage image,
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

        api.vkCmdPipelineBarrier(cmd, srcStage, dstStage, 0,
            0, null, 0, null, 1, &barrier);
    }
}
