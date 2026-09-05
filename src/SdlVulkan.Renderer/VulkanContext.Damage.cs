using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// Damage-based repaint: keep the previous frame's pixels and paint only the region that changed, so a
// frame drawn to update one number in a status bar does not re-shade the whole window. A consumer
// measured the alternative at 8% GPU on an Adreno X1-85 for a 4 Mpix pane, and without damage the only
// two states available are that and zero.
//
// PER SWAPCHAIN IMAGE, which is the whole difficulty. There are 2-3 images and the app renders into
// them in rotation, so the image acquired this frame does not hold the previous frame -- it holds the
// one from 2-3 frames ago. What has to be repainted into it is therefore the UNION of every frame's
// damage since that image was last painted, not this frame's damage. Get it wrong and stale pixels
// appear only at particular frame counts, which reads as an intermittent rendering glitch rather than a
// bookkeeping error. Hence one accumulator per image, unioned on every frame and cleared only for the
// image actually painted.
//
// The accumulator is a bounding box rather than a rect list on purpose. A draw takes ONE scissor
// (multiple scissors need a pipeline with multiple viewports and a shader that selects between them),
// and the app paints its frame once, so a list of rects could only be honoured by replaying the whole
// paint per rect. A box over merged damage is the useful approximation: a status-bar change stays a thin
// strip, which is the case this exists for.
public sealed unsafe partial class VulkanContext
{
    // Same attachments, samples and subpass refs as the swapchain pass -- so the pre-baked pipelines
    // stay render-pass compatible -- differing only in loading the previous colour instead of clearing
    // it. Depth is cleared on this pass as on every other: it is never preserved across frames.
    private VkRenderPass _loadRenderPass;

    private readonly SwapchainDamage _damage = new SwapchainDamage();

    /// <summary>Whether the last <see cref="BeginFrameRenderPass"/> restricted painting to a region.</summary>
    public bool LastFrameWasPartial { get; private set; }

    /// <summary>The region the last partial frame was confined to, in swapchain pixels.</summary>
    public VkRect2D LastFrameRegion { get; private set; }

    /// <summary>
    /// Resets damage bookkeeping to "every image unknown", so the next frame for each image clears and
    /// repaints in full. Call whenever the swapchain is created or recreated: the images are new (or
    /// resized), so nothing can be preserved.
    /// </summary>
    private void ResetDamageState(int imageCount) => _damage.Reset(imageCount);

    /// <summary>
    /// Adds a damaged rect for THIS frame, in swapchain pixels. Accumulates into every image, because
    /// each one will need it whenever its turn comes round.
    /// </summary>
    public void AddFrameDamage(float x, float y, float width, float height)
        => _damage.Add(x, y, width, height);

    /// <summary>
    /// Declares that this frame changes an unknown region, so every image must be fully repainted. The
    /// safe answer, and the right one for a resize, a theme change, or any surface whose damage a caller
    /// cannot enumerate.
    /// </summary>
    public void MarkFullFrameDamage() => _damage.MarkFull();

    /// <summary>
    /// Begins the frame's render pass, preserving the previous contents and confining painting to the
    /// accumulated damage when that is possible, and clearing the whole surface when it is not.
    /// </summary>
    /// <remarks>
    /// Viewport stays the FULL surface while the scissor and render area are the damaged region: the
    /// geometry the app submits is in surface coordinates, so shrinking the viewport would squash the
    /// whole frame into the region instead of cropping it to it.
    /// </remarks>
    public void BeginFrameRenderPass(VkCommandBuffer cmd, float clearR, float clearG, float clearB, float clearA)
    {
        var idx = (int)_currentImageIndex;

        // TryTake CLEARS as it reads, so it must be asked exactly once per frame whichever path is
        // taken -- the image is about to be painted either way.
        var partial = _damage.TryTake(idx, out var dx, out var dy, out var dw, out var dh)
            && _loadRenderPass != VkRenderPass.Null;

        if (!partial)
        {
            BeginRenderPass(cmd, clearR, clearG, clearB, clearA);
            LastFrameWasPartial = false;
            LastFrameRegion = new VkRect2D(0, 0, SwapchainWidth, SwapchainHeight);
            return;
        }

        var region = ClampToSwapchain(dx, dy, dw, dh);

        // The colour entry is ignored (loadOp Load), but the depth attachment is cleared on this pass
        // like every other, and a clear value must be supplied for every attachment index up to it.
        Span<VkClearValue> clears = stackalloc VkClearValue[ClearValueCount];
        FillClearValues(clears, clearR, clearG, clearB, clearA);

        fixed (VkClearValue* pClears = clears)
        {
            VkRenderPassBeginInfo rpBI = new()
            {
                renderPass = _loadRenderPass,
                framebuffer = _framebuffers[idx],
                renderArea = region,
                clearValueCount = ClearValueCount,
                pClearValues = pClears
            };
            DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);
        }
        _renderPassBegun = true;

        VkViewport viewport = new(0, 0, SwapchainWidth, SwapchainHeight, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, viewport);
        DeviceApi.vkCmdSetScissor(cmd, 0, region);

        LastFrameWasPartial = true;
        LastFrameRegion = region;
    }

    private VkRect2D ClampToSwapchain(float rx, float ry, float rw, float rh)
    {
        var x0 = rx < 0f ? 0 : (int)rx;
        var y0 = ry < 0f ? 0 : (int)ry;

        // Ceiling on the far edge: a damaged region that ends mid-pixel still needs that pixel.
        var x1 = (int)MathF.Ceiling(rx + rw);
        var y1 = (int)MathF.Ceiling(ry + rh);
        if (x1 > (int)SwapchainWidth) { x1 = (int)SwapchainWidth; }
        if (y1 > (int)SwapchainHeight) { y1 = (int)SwapchainHeight; }

        var w = x1 - x0;
        var h = y1 - y0;
        return new VkRect2D(x0, y0, (uint)(w < 0 ? 0 : w), (uint)(h < 0 ? 0 : h));
    }

    private VkRenderPass CreateLoadRenderPass(VkFormat format)
    {
        // Single sample only. Under MSAA the multisample attachment is transient (storeOp DontCare) and
        // cannot be reloaded from the resolved image, so preserving a frame would need a persistent
        // offscreen target and a blit back -- a different design. Returning Null here makes every frame
        // take the clearing path, which is correct rather than merely safe.
        if (MsaaSamples != VkSampleCountFlags.Count1)
        {
            return VkRenderPass.Null;
        }

        // The shared shape (attachments, samples, dependencies) with only the colour's load and layouts
        // stated here: keep what is already there, starting and ending where a presented image sits.
        // The dependencies in particular must stay byte-identical to the clearing pass's: a render pass
        // is compatible with the framebuffers and pipelines created against another only if everything
        // but load/store ops and layouts matches, and dependencies are not in that exemption. The loadOp
        // read this pass needs ordered after its own layout transition is therefore admitted in
        // VulkanDevice.FillSubpassDependencies for every pass, not widened here (tried, and validation
        // answered with VUID-VkRenderPassBeginInfo-renderPass-00904 on every partial frame).
        return VulkanDevice.CreateCompatibleRenderPass(DeviceApi, format, DepthFormat, MsaaSamples,
            VkAttachmentLoadOp.Load, VkImageLayout.PresentSrcKHR, VkImageLayout.PresentSrcKHR);
    }

    private void CleanupLoadRenderPass()
    {
        if (_loadRenderPass != VkRenderPass.Null)
        {
            DeviceApi.vkDestroyRenderPass(_loadRenderPass);
            _loadRenderPass = VkRenderPass.Null;
        }
    }
}
