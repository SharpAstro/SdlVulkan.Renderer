using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// Single-sample main pass: a frame whose consumer asks for it (VkRenderer.SingleSampleMainPass) runs its
// main pass at one sample, writing the swapchain image (or the offscreen readback image) directly,
// where it would otherwise draw into a multisampled image and resolve it.
//
// Why a consumer would. A frame that only blits a cached layer -- content rendered and antialiased once,
// in its own multisampled pass -- and draws its chrome over it gains nothing from multisampling the whole
// window, and on a tiling GPU it pays for it: four times the samples in tile memory, so more bins, and a
// resolve of every pixel. A PDF viewer measured its idle frames at 3.09 ms of GPU at 4x and 2.22 ms at
// 1x (Adreno X1-85, 2880x1814). Text, rounded boxes, ellipses and textured quads antialias in their
// shaders and come out the same either way, and so does a line drawn with VkRenderer.DrawLine. Geometry
// that relies on MSAA for its edges (DrawTriangles, a page's fills and strokes, a mesh) does not, which is
// why this is the consumer's call, frame by frame.
//
// What it costs. One more render pass on the device (VulkanDevice.SingleSampleRenderPass), a framebuffer
// per swapchain image over the images the multisampled pass already resolves into, a single-sample depth
// image, and a second pipeline set in each VkRenderer, about 50 ms of driver compile built off the render
// thread (VkRenderer.SingleSampleMainPassReady). The cached-layer and thumbnail passes are untouched:
// they are recorded before the main pass begins, with the multisampled pipelines.
//
// And one thing it gives back. The damage path (VulkanContext.Damage.cs) is single-sample only, because a
// transient multisample image cannot be reloaded; a single-sampled frame loads the swapchain image itself,
// so it can repaint only what changed even on a device that multisamples.
public sealed unsafe partial class VulkanContext
{
    private VkFramebuffer[] _singleSampleFramebuffers = [];
    private VkImage _singleDepthImage;
    private VkDeviceMemory _singleDepthMemory;
    private VkImageView _singleDepthImageView;
    private VkRenderPass _singleSampleLoadRenderPass;

    private VkFramebuffer _offscreenSingleSampleFramebuffer;
    private VkImage _offscreenSingleDepthImage;
    private VkDeviceMemory _offscreenSingleDepthMemory;
    private VkImageView _offscreenSingleDepthImageView;

    /// <summary>The device's single-sample main pass, or Null when it does not multisample.</summary>
    public VkRenderPass SingleSampleRenderPass => _dev.SingleSampleRenderPass;

    /// <summary>Whether a frame can ask for a single-sample main pass: the device multisamples, so there
    /// is another sample count to choose.</summary>
    public bool SingleSampleMainPassAvailable => _dev.SingleSampleRenderPass != VkRenderPass.Null;

    /// <summary>Whether the last main pass begun ran at one sample, by request.</summary>
    public bool LastFrameSingleSampled { get; private set; }

    // After the swapchain's own framebuffers, from CreateSwapchain.
    private void CreateSingleSampleSwapchainTargets(VkFormat format, uint width, uint height)
    {
        if (!SingleSampleMainPassAvailable) return;
        CreateDepthAttachment(width, height, out _singleDepthImage, out _singleDepthMemory,
            out _singleDepthImageView, VkSampleCountFlags.Count1);
        _singleSampleFramebuffers = new VkFramebuffer[_swapchainImageViews.Length];
        for (var i = 0; i < _swapchainImageViews.Length; i++)
        {
            _singleSampleFramebuffers[i] = CreateCompatibleFramebuffer(SingleSampleRenderPass,
                colorView: _swapchainImageViews[i], depthView: _singleDepthImageView,
                resolveView: VkImageView.Null, width, height, multisampled: false);
        }
        // Loading the swapchain image is what the multisampled pass cannot do (see CreateLoadRenderPass).
        _singleSampleLoadRenderPass = VulkanDevice.CreateCompatibleRenderPass(DeviceApi, format, DepthFormat,
            VkSampleCountFlags.Count1, VkAttachmentLoadOp.Load, VkImageLayout.PresentSrcKHR, VkImageLayout.PresentSrcKHR);
    }

    private void CleanupSingleSampleSwapchainTargets()
    {
        foreach (var fb in _singleSampleFramebuffers)
            DeviceApi.vkDestroyFramebuffer(fb);
        _singleSampleFramebuffers = [];
        DestroyDepthAttachment(ref _singleDepthImage, ref _singleDepthMemory, ref _singleDepthImageView);
        if (_singleSampleLoadRenderPass != VkRenderPass.Null)
        {
            DeviceApi.vkDestroyRenderPass(_singleSampleLoadRenderPass);
            _singleSampleLoadRenderPass = VkRenderPass.Null;
        }
    }

    // After the offscreen target's own framebuffer, from CreateOffscreenTarget.
    private void CreateSingleSampleOffscreenTarget(uint width, uint height)
    {
        if (!SingleSampleMainPassAvailable) return;
        CreateDepthAttachment(width, height, out _offscreenSingleDepthImage, out _offscreenSingleDepthMemory,
            out _offscreenSingleDepthImageView, VkSampleCountFlags.Count1);
        _offscreenSingleSampleFramebuffer = CreateCompatibleFramebuffer(SingleSampleRenderPass,
            colorView: _offscreenImageView, depthView: _offscreenSingleDepthImageView,
            resolveView: VkImageView.Null, width, height, multisampled: false);
    }

    private void CleanupSingleSampleOffscreenTarget()
    {
        if (_offscreenSingleSampleFramebuffer != VkFramebuffer.Null)
        {
            DeviceApi.vkDestroyFramebuffer(_offscreenSingleSampleFramebuffer);
            _offscreenSingleSampleFramebuffer = VkFramebuffer.Null;
        }
        DestroyDepthAttachment(ref _offscreenSingleDepthImage, ref _offscreenSingleDepthMemory,
            ref _offscreenSingleDepthImageView);
    }
}
