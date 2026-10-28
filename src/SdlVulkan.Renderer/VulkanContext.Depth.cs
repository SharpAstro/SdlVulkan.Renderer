using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>The render targets that can carry a depth image for <see cref="VkRenderer.BeginMeshRegion"/>:
/// see <see cref="VulkanContext.RequireMeshDepth"/>.</summary>
[Flags]
public enum MeshDepthTargets
{
    None = 0,
    /// <summary>The window's swapchain, or the offscreen target.</summary>
    Main = 1,
    /// <summary>The cached-layer slots (<see cref="VkRenderer.BeginCachedLayer"/>).</summary>
    CachedLayer = 2,
    /// <summary>The thumbnail capture target (<see cref="VkRenderer.BeginThumbnailCapture"/>).</summary>
    Thumbnail = 4,
    All = Main | CachedLayer | Thumbnail,
}

// The depth attachment a framebuffer in this renderer carries when its target may draw a mesh, and the
// helpers that keep the framebuffers and clear values in step with VulkanDevice.CreateCompatibleRenderPass's
// attachment order -- on either of its two shapes, with depth and without.
//
// EVERY target starts without a depth image, on the depth-less shape: the swapchain's framebuffers (and the
// damage load pass and single-sample pass that pair with them), the offscreen target, each cached-layer slot
// and the thumbnail capture. Depth is only for VkRenderer.BeginMeshRegion/DrawMesh, and where the device has
// no lazily allocated memory (a desktop GPU, lavapipe) the image costs as much as the multisample colour one,
// 16 bytes a pixel at 4x MSAA (#146). A target gets its depth image once something asks for it, per target:
//   * up front, RequireMeshDepth(targets), so the first frame draws its meshes; or
//   * lazily, a BeginMeshRegion on a depth-less pass returns false, draws nothing and makes the request.
// The request lands where no frame is being recorded against the objects it replaces: the next BeginFrame
// (window) / BeginOffscreenFrame for the main target, the next BeginCachedLayerPass for a layer slot, the
// next BeginThumbnailCapturePass for the thumbnail. From then on the target is on the depth shape for good,
// across resize and swapchain recreation, and VkRenderer binds the pipeline set matching the shape of the
// pass it records. The shared depth image rule below is unchanged, it just starts out empty.
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

    // Whether the MAIN target's framebuffers (the swapchain's, or the offscreen target's) carry a depth image
    // and so are on the depth shape, and whether something has asked for it. A request lands at the next
    // BeginFrame / BeginOffscreenFrame, where no frame is being recorded; until then HasDepth is what the
    // frame in hand was recorded with.
    private bool _mainHasDepth;
    private bool _mainWantsDepth;
    // The same pair for the cached layer's slots (each slot flips on its own turn, _layerHasDepth) and the
    // thumbnail capture target.
    private bool _layerWantsDepth;
    private bool _thumbWantsDepth;

    // Whether the main pass of the NEXT frame is on the depth shape: once the target has asked for depth.
    // What a renderer builds its first pipeline set for.
    internal bool MainPassWillHaveDepth => _mainWantsDepth;

    // Whether the main pass of the frame begun is on the depth shape, so which pipeline set it draws with.
    internal bool MainPassHasDepth => _mainHasDepth;

    /// <summary>
    /// Whether the main target (the swapchain, or the offscreen target) carries a depth image, which is what
    /// lets a mesh region draw on it. False until <see cref="RequireMeshDepth"/> has been honoured by a
    /// <see cref="BeginFrame"/> / <see cref="BeginOffscreenFrame"/>.
    /// </summary>
    public bool MainTargetHasDepth => _mainHasDepth;

    /// <summary>
    /// Asks for depth images on the given targets so that <see cref="VkRenderer.BeginMeshRegion"/> draws on
    /// them: the main target (<see cref="MeshDepthTargets.Main"/>, the default), the cached-layer slots and
    /// the thumbnail capture. Meshes are the only thing depth is for; a target that never draws one should
    /// not be named here, and does not pay for the image (16 bytes a pixel at 4x MSAA on a device without
    /// lazily allocated memory). A consumer that cannot say in advance need not call it at all: a
    /// <see cref="VkRenderer.BeginMeshRegion"/> on a depth-less pass returns false, draws nothing and makes
    /// this request for that target itself, so the next frame (next layer / capture pass) has depth. Calling
    /// it up front is what makes the first frame draw. Once made the request stands, across a resize or a
    /// swapchain recreation too.
    /// </summary>
    public void RequireMeshDepth(MeshDepthTargets targets = MeshDepthTargets.Main)
    {
        if ((targets & MeshDepthTargets.Main) != 0) _mainWantsDepth = true;
        if ((targets & MeshDepthTargets.CachedLayer) != 0) _layerWantsDepth = true;
        if ((targets & MeshDepthTargets.Thumbnail) != 0) _thumbWantsDepth = true;
    }

    // The main pass's render passes, by the shape the target has.
    private VkRenderPass MainRenderPass => _mainHasDepth ? _dev.RenderPass : _dev.RenderPassNoDepth;
    private VkRenderPass MainSingleSampleRenderPass
        => _mainHasDepth ? _dev.SingleSampleRenderPass : _dev.SingleSampleRenderPassNoDepth;

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
