using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// The depth attachment a framebuffer in this renderer carries when its target may draw a mesh, and the
// helpers that keep the framebuffers and clear values in step with VulkanDevice.CreateCompatibleRenderPass's
// attachment order -- on either of its two shapes, with depth and without (a target that never opens a mesh
// region has no depth image at all: the offscreen target starts that way, VulkanContext.Offscreen.cs).
//
// One depth image per render TARGET, not per pass or per frame: depth is written and tested inside a
// single pass and never read after it (loadOp Clear, storeOp DontCare), so consecutive passes and
// consecutive frames may reuse the same image -- the swapchain's framebuffers share one exactly as they
// share the multisample colour image, and the subpass dependencies already order one pass's depth
// writes against the next pass's clear. What a target cannot share is another target's: the cached
// layer and thumbnail passes are recorded into the same command buffer ahead of the swapchain pass and
// could in principle borrow its image, but their capacities are fixed while the swapchain resizes, and
// tying their framebuffers to an image the swapchain recreates would couple two lifetimes that are
// separate today. Each target owns its depth image, as each owns its multisample colour image.
//
// Memory: D32 (or a packed 24-bit format) at the target's sample count -- under 4x MSAA that is 16
// bytes a pixel, the same again as the multisample colour. TransientAttachment usage lets a tiler keep
// it in tile memory and never allocate it; a desktop GPU allocates it in full.
public sealed unsafe partial class VulkanContext
{
    /// <summary>The depth format every pass on this device uses. Forwarded from the shared device.</summary>
    public VkFormat DepthFormat => _dev.DepthFormat;

    /// <summary>
    /// What the depth attachment is cleared to: the far plane, which pairs with a Less depth test — a
    /// fragment passes when it is nearer than what is already there, and on an empty buffer everything
    /// is.
    /// </summary>
    internal const float DepthClearValue = 1f;

    /// <summary>Number of entries <see cref="FillClearValues"/> writes for a pass with depth.</summary>
    internal const int ClearValueCount = 2;

    /// <summary>Number of entries <see cref="FillClearValues"/> writes for a pass without depth: the colour.</summary>
    internal const int ClearValueCountNoDepth = 1;

    /// <summary>
    /// Creates a depth image, its memory and a depth-aspect view for a framebuffer of
    /// (<paramref name="width"/>, <paramref name="height"/>) at the device's sample count.
    /// </summary>
    internal void CreateDepthAttachment(uint width, uint height,
        out VkImage image, out VkDeviceMemory memory, out VkImageView view, VkSampleCountFlags? samples = null)
    {
        VkImageCreateInfo imgCI = new()
        {
            imageType = VkImageType.Image2D,
            format = DepthFormat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = samples ?? MsaaSamples,
            tiling = VkImageTiling.Optimal,
            // Never sampled and never stored, so TransientAttachment lets a tiler keep it in tile memory
            // and never write it out at all. Paired with the pass's storeOp DontCare — the flag alone
            // does not make it transient, the pass has to agree that nothing outlives it.
            usage = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.TransientAttachment,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&imgCI, null, out image).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(image, out var memReqs);
        // Lazily allocated where the device offers it: on a tiler this image is never backed at all,
        // which at a large export size under MSAA is the difference between a gigabyte and nothing.
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindTransientMemoryType(memReqs.memoryTypeBits)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out memory).CheckResult();
        DeviceApi.vkBindImageMemory(image, memory, 0).CheckResult();

        // Depth aspect only, even on a combined depth-stencil format: a framebuffer attachment view
        // must not name an aspect the render pass does not use, and nothing here uses stencil.
        var viewCI = new VkImageViewCreateInfo(image, VkImageViewType.Image2D, DepthFormat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Depth, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&viewCI, null, out view).CheckResult();
    }

    /// <summary>Destroys what <see cref="CreateDepthAttachment"/> made and nulls the handles; safe on
    /// handles that are already null.</summary>
    internal void DestroyDepthAttachment(ref VkImage image, ref VkDeviceMemory memory, ref VkImageView view)
    {
        if (view != VkImageView.Null)
        {
            DeviceApi.vkDestroyImageView(view);
            view = VkImageView.Null;
        }
        if (image != VkImage.Null)
        {
            DeviceApi.vkDestroyImage(image);
            image = VkImage.Null;
        }
        if (memory != VkDeviceMemory.Null)
        {
            DeviceApi.vkFreeMemory(memory);
            memory = VkDeviceMemory.Null;
        }
    }

    /// <summary>
    /// Creates a framebuffer for a pass built by <see cref="VulkanDevice.CreateCompatibleRenderPass"/>,
    /// with the attachments in the order that pass declares: colour, depth, then the resolve target
    /// under MSAA. <paramref name="colorView"/> is the multisample image when MSAA is on and the
    /// stored image otherwise; <paramref name="resolveView"/> is the stored image under MSAA and
    /// ignored otherwise. A Null <paramref name="depthView"/> is the depth-less shape: colour and then
    /// the resolve target, for a pass built with <c>withDepth</c> false.
    /// </summary>
    internal VkFramebuffer CreateCompatibleFramebuffer(VkRenderPass renderPass,
        VkImageView colorView, VkImageView depthView, VkImageView resolveView, uint width, uint height,
        bool? multisampled = null)
    {
        // A single-sample pass beside a multisampled device's own has two attachments, not three.
        var msaa = multisampled ?? MsaaSamples != VkSampleCountFlags.Count1;
        var withDepth = depthView != VkImageView.Null;
        Span<VkImageView> attachments = stackalloc VkImageView[3];
        var count = 0;
        attachments[count++] = colorView;
        if (withDepth) attachments[count++] = depthView;
        if (msaa) attachments[count++] = resolveView;
        fixed (VkImageView* pAtt = attachments)
        {
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = renderPass,
                attachmentCount = (uint)count,
                pAttachments = pAtt,
                width = width, height = height, layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out var framebuffer).CheckResult();
            return framebuffer;
        }
    }

    /// <summary>
    /// The clear values a compatible pass takes, in attachment order: the colour, then the depth at
    /// the far plane. Two entries in both modes of the depth shape — the MSAA resolve attachment comes
    /// after and is not cleared, so it needs none. A load pass ignores the colour entry and still needs
    /// the depth one, which is why the count is fixed by the shape rather than a per-pass decision. The
    /// depth-less shape (<paramref name="withDepth"/> false) takes the colour alone. Returns the count
    /// written, <see cref="ClearValueCount"/> or <see cref="ClearValueCountNoDepth"/>.
    /// </summary>
    internal static int FillClearValues(Span<VkClearValue> clears, float r, float g, float b, float a,
        bool withDepth = true)
    {
        clears[0] = new VkClearValue();
        clears[0].color = new VkClearColorValue(r, g, b, a);
        if (!withDepth) return ClearValueCountNoDepth;
        clears[1] = new VkClearValue();
        clears[1].depthStencil = new VkClearDepthStencilValue(DepthClearValue, 0);
        return ClearValueCount;
    }
}
