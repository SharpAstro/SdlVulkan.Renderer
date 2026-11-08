using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

// HDR output as an opt-in on the context: an scRGB swapchain (R16G16B16A16_SFLOAT, EXTENDED_SRGB_LINEAR_EXT) that the
// frame reaches through one composite pass.
//
// Zero cost for everyone else. A context that did not ask (the default), or whose surface offers no scRGB, takes none
// of this: no field below is allocated, no branch but the two guards (SubmitFrame's `_hdrActive`, the damage
// path's) is executed per frame, and the swapchain, the render passes, the pipelines, the shaders and the frame code are
// the ones that were here before. Everything that exists only for HDR is created when the swapchain is built and HDR is
// chosen, and torn down with it.
//
// How an HDR frame is made. Every existing pipeline writes sRGB-encoded 8-bit values, so the frame is rendered exactly
// as before into an SDR LAYER: a B8G8R8A8 (the device's colour format) image per swapchain image, standing where the
// swapchain image stood in the swapchain arrays (as the layered window's image does, VulkanContext.Layered.cs). The
// main pass, MSAA resolve, single-sample pass, cached layers, thumbnail capture and the DEBUG readback therefore see no
// difference and the device's passes stay B8G8R8A8, which is also what lets ONE shared device serve HDR and SDR windows.
// Then EndFrame records a composite pass on the float swapchain image: the queued HDR images first (VkRenderer.DrawHdrImage
// on an HDR frame), then the SDR layer over them, premultiplied, sRGB decoded to linear light and scaled to the display's
// SDR white (hdrcomposite.frag). Why a layer and not conversion in every shader: it keeps every pipeline, blend and readback
// bit-identical, and the cost (one layer image, one fullscreen pass) is paid only by a window that is on an HDR display.
//
// Not offered to a transparent window (a context from CreateTransparent / CreateTransparentForSharedDevice, a layered
// window, or a Dozen device): their alpha path is a different composition, and they take the SDR swapchain they always did.
//
// Damage. The composite redraws the whole swapchain image every frame, so an HDR frame is always repainted in full:
// BeginFrameRenderPass takes the clearing path whatever damage was declared. A frame must therefore call DrawHdrImage for
// every HDR image it shows, every frame.
public sealed unsafe partial class VulkanContext
{
    // Asked for at construction (the factories' hdr argument), and only ever true for an opaque window or an offscreen
    // target. Whether it was GOT is _hdrActive: the surface may not offer scRGB, and moving to another display re-asks.
    private readonly bool _wantsHdr;

    // The composite is configured: the swapchain (or the offscreen target) is scRGB and the arrays below exist.
    private bool _hdrActive;

    // The actual swapchain images and views (float), while _swapchainImages / _swapchainImageViews hold the SDR layers.
    private VkImage[] _hdrPresentImages = [];
    private VkImageView[] _hdrPresentViews = [];
    // The layers' images and memory, one per swapchain image. Owned here; the swapchain owns the present images.
    private VkImage[] _hdrLayerImages = [];
    private VkDeviceMemory[] _hdrLayerMemories = [];
    // One per swapchain image (one for the offscreen target): the composite pass over the float image, and the
    // descriptor that samples the layer drawn for the same index.
    private VkFramebuffer[] _hdrCompositeFramebuffers = [];
    private VkDescriptorSet[] _hdrLayerSets = [];

    // Per context, built when it first gets an HDR target. The pass depends only on the float format, so it outlives
    // swapchain recreation; the pipelines depend on the pass. The image pipeline waits for the first queued HDR image.
    private VkRenderPass _hdrCompositePass;
    private VkPipelineLayout _hdrCompositeLayout;
    private VkPipeline _hdrCompositeLayerPipeline;
    private VkPipeline _hdrCompositeImagePipeline;

    // The offscreen HDR target: the float image the composite writes and the readback copies from.
    private VkImage _hdrOffscreenImage;
    private VkDeviceMemory _hdrOffscreenMemory;
    private VkImageView _hdrOffscreenImageView;

    // Bytes of the composite layer pipeline's push block: one vec4 (x: SDR white in scRGB units).
    private const uint HdrCompositePushBytes = 16;

    // The floats of an HdrImagePipeline push block (VulkanDevice.HdrImagePushBytes).
    private const int HdrImagePushFloats = (int)(VulkanDevice.HdrImagePushBytes / sizeof(float));

    private struct HdrQueuedDraw
    {
        public VkBuffer Buffer;
        public ulong Offset;
        public VkDescriptorSet Set;
        public VkRect2D Scissor;
        public fixed float Push[HdrImagePushFloats];
    }

    // The HDR images the frame being recorded has drawn, in painter order, replayed by the composite. Reused across
    // frames; stamped with the frame ordinal so a frame that never reached its composite leaves nothing for the next.
    private HdrQueuedDraw[] _hdrDraws = [];
    private int _hdrDrawCount;
    private long _hdrQueueFrame;

    private float _sdrWhiteScRgb = 1f;
    private float _hdrHeadroom = 1f;

    /// <summary>Whether this context was asked for an HDR swapchain (the factories' <c>hdr</c> argument). False for a
    /// transparent window whatever was asked: see the notes on <see cref="HdrActive"/>.</summary>
    public bool HdrRequested => _wantsHdr;

    /// <summary>
    /// Whether frames are being presented as scRGB through the composite pass: HDR was asked for and the surface offered
    /// <c>R16G16B16A16_SFLOAT</c> with <c>EXTENDED_SRGB_LINEAR_EXT</c> (an offscreen target always does). When false the
    /// context is exactly an SDR one: <see cref="HdrRequested"/> may be true on a surface that does not offer scRGB, and
    /// then nothing here exists. Decided each time the swapchain is built, so a window moved to another display, whose
    /// surface formats differ, is re-evaluated on the resize that follows.
    /// </summary>
    /// <remarks>
    /// Never true for a transparent window, a layered window or a Dozen device: their alpha is carried another way.
    /// While true the frame is painted in full every time (no damage repaint) and <see cref="VkRenderer.DrawHdrImage"/>
    /// queues into the composite instead of tone-mapping.
    /// </remarks>
    public bool HdrActive => _hdrActive;

    /// <summary>
    /// The format of what is presented: <see cref="VkFormat.R16G16B16A16Sfloat"/> while <see cref="HdrActive"/>, else
    /// <see cref="SwapchainFormat"/>. (<see cref="SwapchainFormat"/> is the format the frame is RENDERED in, the SDR
    /// layer's, on either path, which is what every pipeline and side-car is built against.)
    /// </summary>
    public VkFormat PresentFormat => _hdrActive ? VkFormat.R16G16B16A16Sfloat : SwapchainFormat;

    /// <summary>
    /// The display's SDR white in scRGB units (1.0 = 80 cd/m²): what an sRGB 1.0 of the SDR layer is scaled to by the
    /// composite, so the UI keeps the brightness the user set for SDR content. Defaults to 1 (80 cd/m²). The app, or
    /// the event loop, keeps it current from SDL's <c>SDL_PROP_DISPLAY_SDR_WHITE_LEVEL_FLOAT</c>; read by the next frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a positive finite number.</exception>
    public float SdrWhiteScRgb
    {
        get => _sdrWhiteScRgb;
        set
        {
            if (!(value > 0f) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), value, null);
            _sdrWhiteScRgb = value;
        }
    }

    /// <summary>
    /// How far above <see cref="SdrWhiteScRgb"/> the display reaches (SDL's <c>SDL_PROP_DISPLAY_HDR_HEADROOM_FLOAT</c>),
    /// dimensionless, at least 1. Defaults to 1: no known headroom, so HDR images clip at SDR white. Read by the next
    /// frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a finite number of at least 1.</exception>
    public float HdrHeadroom
    {
        get => _hdrHeadroom;
        set
        {
            if (!(value >= 1f) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), value, null);
            _hdrHeadroom = value;
        }
    }

    /// <summary>The display's peak in scRGB units (1.0 = 80 cd/m²): <see cref="SdrWhiteScRgb"/> times
    /// <see cref="HdrHeadroom"/>. What an HDR image is clamped to (<see cref="HdrColor.ClampToPeak"/>).</summary>
    public float HdrPeakScRgb => _sdrWhiteScRgb * _hdrHeadroom;

    // ---- Swapchain -------------------------------------------------------------------------------------------

    // What the surface offers, asked of the surface itself: scRGB when it is there, else the SDR choice (unused).
    private SurfaceFormatChoice ChooseHdrSurfaceFormat()
        => HdrSurfaceFormat.Choose(QuerySurfaceFormats(), hdrRequested: true);

    private VkSurfaceFormatKHR[] QuerySurfaceFormats()
    {
        uint count = 0;
        InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(PhysicalDevice, _surface, &count, null);
        if (count == 0) return [];
        var formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* p = formats)
            InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(PhysicalDevice, _surface, &count, p);
        return formats;
    }

    // ---- Display facts ---------------------------------------------------------------------------------------

    private HdrDisplayInfo _hdrDisplay = HdrDisplayInfo.Sdr;

    /// <summary>The display facts this context was last given (<see cref="ApplyHdrDisplay"/>): SDR until the event loop,
    /// or the app, has read them. Only a context that asked for HDR (<see cref="HdrRequested"/>) acts on them.</summary>
    public HdrDisplayInfo HdrDisplay => _hdrDisplay;

    /// <summary>
    /// Takes the display facts of the window this context presents to (<see cref="SdlVulkanWindow.HdrDisplay"/>), when
    /// SDL says the display or the HDR state changed: never per frame. Returns what <see cref="HdrDisplayState.Decide"/>
    /// made of them; when <see cref="HdrDisplayDecision.RecreateSwapchain"/> is set the caller rebuilds the swapchain
    /// (<see cref="RecreateSwapchain"/>, through <see cref="VkRenderer.Resize"/> so the layout follows), which re-asks the
    /// surface and so adopts or drops scRGB. A context that did not ask for HDR (and any layered, transparent or offscreen
    /// one, which never does) only records the facts and does not touch the surface.
    /// </summary>
    public HdrDisplayDecision ApplyHdrDisplay(HdrDisplayInfo facts)
    {
        // An offscreen target has no surface to ask, and is never moved to another display: it only records the facts.
        var asked = _wantsHdr && !_isOffscreen;
        var offered = asked ? QuerySurfaceFormats() : [];
        var decision = HdrDisplayState.Decide(new HdrDisplayState(_hdrDisplay, _hdrActive), facts, offered, asked);
        _hdrDisplay = facts;
        // A rebuild into scRGB starts from the current values too, whatever changed first.
        if (decision.UpdateComposite || decision.RecreateSwapchain)
        {
            SdrWhiteScRgb = facts.SdrWhiteScRgb;
            HdrHeadroom = facts.Headroom;
        }
        return decision;
    }

    // After CreateSwapchainImages made an scRGB swapchain: the swapchain's images move aside, and the SDR layers take
    // their place in the swapchain arrays, so everything that follows (views, MSAA image, framebuffers, damage, readback)
    // builds against the layers exactly as it built against the swapchain's images.
    private void AdoptHdrSwapchain(VkExtent2D extent, uint imageCount)
    {
        _hdrPresentImages = _swapchainImages;
        _hdrPresentViews = new VkImageView[imageCount];
        for (var i = 0; i < imageCount; i++)
        {
            var viewCI = new VkImageViewCreateInfo(
                _hdrPresentImages[i], VkImageViewType.Image2D, VkFormat.R16G16B16A16Sfloat,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&viewCI, null, out _hdrPresentViews[i]).CheckResult();
        }

        _hdrLayerImages = new VkImage[imageCount];
        _hdrLayerMemories = new VkDeviceMemory[imageCount];
        for (var i = 0; i < imageCount; i++)
        {
            VkImageCreateInfo layerCI = new()
            {
                imageType = VkImageType.Image2D,
                format = _dev.ColorFormat,
                extent = new VkExtent3D(extent.width, extent.height, 1),
                mipLevels = 1,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                // Drawn into (or resolved into) as a swapchain image is, sampled by the composite, and copied out of for
                // the DEBUG inspector's capture, which takes it as it takes a swapchain image.
                usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc,
                sharingMode = VkSharingMode.Exclusive
            };
            DeviceApi.vkCreateImage(&layerCI, null, out _hdrLayerImages[i]).CheckResult();
            DeviceApi.vkGetImageMemoryRequirements(_hdrLayerImages[i], out var memReqs);
            VkMemoryAllocateInfo allocInfo = new()
            {
                allocationSize = memReqs.size,
                memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
            };
            DeviceApi.vkAllocateMemory(&allocInfo, null, out _hdrLayerMemories[i]).CheckResult();
            DeviceApi.vkBindImageMemory(_hdrLayerImages[i], _hdrLayerMemories[i], 0).CheckResult();
        }
        _swapchainImages = (VkImage[])_hdrLayerImages.Clone();
        EnsureHdrComposite();
        _hdrActive = true;
    }

    // The composite's per-image state, once the layers have views (CreateSwapchain's view loop) and the swapchain is
    // built: a framebuffer over each float image and a descriptor over each layer.
    private void CreateHdrSwapchainComposite(VkExtent2D extent)
        => CreateHdrCompositeTargets(_hdrPresentViews, _swapchainImageViews, extent.width, extent.height);

    private void CreateHdrCompositeTargets(VkImageView[] targetViews, VkImageView[] layerViews, uint width, uint height)
    {
        _hdrCompositeFramebuffers = new VkFramebuffer[targetViews.Length];
        _hdrLayerSets = new VkDescriptorSet[layerViews.Length];
        for (var i = 0; i < targetViews.Length; i++)
        {
            var view = targetViews[i];
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = _hdrCompositePass,
                attachmentCount = 1,
                pAttachments = &view,
                width = width, height = height, layers = 1
            };
            DeviceApi.vkCreateFramebuffer(&fbCI, null, out _hdrCompositeFramebuffers[i]).CheckResult();
            _hdrLayerSets[i] = _dev.AllocateDescriptorSet();
            _dev.UpdateDescriptorSet(_hdrLayerSets[i], layerViews[i], LinearClampSampler);
        }
    }

    // The pass, layout and layer pipeline, once per context.
    private void EnsureHdrComposite()
    {
        if (_hdrCompositePass != VkRenderPass.Null) return;
        // The float target is left where the device leaves its main image (PresentSrcKHR for a surface, where the
        // swapchain presents it; ColorAttachmentOptimal offscreen, where the readback transitions it itself).
        _hdrCompositePass = VulkanDevice.CreateCompatibleRenderPass(DeviceApi, VkFormat.R16G16B16A16Sfloat, DepthFormat,
            VkSampleCountFlags.Count1, VkAttachmentLoadOp.Clear, VkImageLayout.Undefined, _dev.ColorFinalLayout,
            withDepth: false);

        var setLayout = DescriptorSetLayout;
        VkPushConstantRange range = new()
        {
            stageFlags = VkShaderStageFlags.Fragment,
            offset = 0,
            size = HdrCompositePushBytes
        };
        VkPipelineLayoutCreateInfo layoutCI = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &range
        };
        DeviceApi.vkCreatePipelineLayout(&layoutCI, null, out _hdrCompositeLayout).CheckResult();
        _hdrCompositeLayerPipeline = VkPipelineSet.CreateHdrCompositePipeline(_dev, _hdrCompositePass, _hdrCompositeLayout);
    }

    // What CleanupSwapchain tears down for HDR: runs after the same bounded drain and present-queue flush that make the
    // swapchain's own images and the MSAA target legal to destroy outright. Not DeferDestroy: Dispose flushes the
    // deferred queue BEFORE CleanupSwapchain, so a destroy deferred here would never run.
    private void CleanupHdrSwapchain()
    {
        foreach (var fb in _hdrCompositeFramebuffers)
            DeviceApi.vkDestroyFramebuffer(fb);
        foreach (var set in _hdrLayerSets)
            _dev.FreeDescriptorSet(set);
        foreach (var v in _hdrPresentViews)
            DeviceApi.vkDestroyImageView(v);
        for (var i = 0; i < _hdrLayerImages.Length; i++)
        {
            DeviceApi.vkDestroyImage(_hdrLayerImages[i]);
            DeviceApi.vkFreeMemory(_hdrLayerMemories[i]);
        }
        _hdrCompositeFramebuffers = [];
        _hdrLayerSets = [];
        _hdrPresentViews = [];
        _hdrPresentImages = [];
        _hdrLayerImages = [];
        _hdrLayerMemories = [];
        _hdrActive = false;
    }

    // The per-context part, at Dispose.
    private void CleanupHdrComposite()
    {
        if (_hdrCompositePass == VkRenderPass.Null) return;
        if (_hdrCompositeImagePipeline != VkPipeline.Null) DeviceApi.vkDestroyPipeline(_hdrCompositeImagePipeline);
        DeviceApi.vkDestroyPipeline(_hdrCompositeLayerPipeline);
        DeviceApi.vkDestroyPipelineLayout(_hdrCompositeLayout);
        DeviceApi.vkDestroyRenderPass(_hdrCompositePass);
        _hdrCompositeImagePipeline = VkPipeline.Null;
        _hdrCompositeLayerPipeline = VkPipeline.Null;
        _hdrCompositeLayout = VkPipelineLayout.Null;
        _hdrCompositePass = VkRenderPass.Null;
    }

    // ---- Offscreen -------------------------------------------------------------------------------------------

    /// <summary>The format of the HDR offscreen target <see cref="ReadbackOffscreenScRgb"/> reads:
    /// <see cref="VkFormat.R16G16B16A16Sfloat"/>.</summary>
    public VkFormat OffscreenHdrFormat => VkFormat.R16G16B16A16Sfloat;

    // The float image the offscreen composite writes, after CreateOffscreenTarget made the SDR layer (_offscreenImage).
    private void CreateHdrOffscreenTarget(uint width, uint height)
    {
        VkImageCreateInfo imgCI = new()
        {
            imageType = VkImageType.Image2D,
            format = VkFormat.R16G16B16A16Sfloat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        DeviceApi.vkCreateImage(&imgCI, null, out _hdrOffscreenImage).CheckResult();
        DeviceApi.vkGetImageMemoryRequirements(_hdrOffscreenImage, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        DeviceApi.vkAllocateMemory(&allocInfo, null, out _hdrOffscreenMemory).CheckResult();
        DeviceApi.vkBindImageMemory(_hdrOffscreenImage, _hdrOffscreenMemory, 0).CheckResult();
        var viewCI = new VkImageViewCreateInfo(
            _hdrOffscreenImage, VkImageViewType.Image2D, VkFormat.R16G16B16A16Sfloat,
            VkComponentMapping.Rgba,
            new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
        DeviceApi.vkCreateImageView(&viewCI, null, out _hdrOffscreenImageView).CheckResult();

        EnsureHdrComposite();
        CreateHdrCompositeTargets([_hdrOffscreenImageView], [_offscreenImageView], width, height);
        _hdrActive = true;
    }

    private void CleanupHdrOffscreenTarget()
    {
        foreach (var fb in _hdrCompositeFramebuffers)
            DeviceApi.vkDestroyFramebuffer(fb);
        foreach (var set in _hdrLayerSets)
            _dev.FreeDescriptorSet(set);
        _hdrCompositeFramebuffers = [];
        _hdrLayerSets = [];
        if (_hdrOffscreenImageView != VkImageView.Null) DeviceApi.vkDestroyImageView(_hdrOffscreenImageView);
        if (_hdrOffscreenImage != VkImage.Null) DeviceApi.vkDestroyImage(_hdrOffscreenImage);
        if (_hdrOffscreenMemory != VkDeviceMemory.Null) DeviceApi.vkFreeMemory(_hdrOffscreenMemory);
        _hdrOffscreenImageView = VkImageView.Null;
        _hdrOffscreenImage = VkImage.Null;
        _hdrOffscreenMemory = VkDeviceMemory.Null;
        _hdrActive = false;
    }

    /// <summary>
    /// Copies the HDR offscreen target (what the composite wrote: <see cref="OffscreenHdrFormat"/>) into a new array of
    /// scRGB floats, R, G, B, A per pixel, top to bottom, 1.0 = 80 cd/m² as presented. Needs a context made by
    /// <see cref="CreateOffscreen"/> with <c>hdr: true</c>. Blocks like <see cref="ReadbackOffscreenRgba"/>, which on
    /// such a context reads the SDR layer instead (the frame as every ordinary pipeline drew it).
    /// </summary>
    public float[] ReadbackOffscreenScRgb()
    {
        if (!_isOffscreen || !_hdrActive) throw new InvalidOperationException("ReadbackOffscreenScRgb requires CreateOffscreen with hdr: true");
        var raw = ReadbackImageBytes(_hdrOffscreenImage, 8);
        var count = raw.Length / 2;
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = (float)BitConverter.UInt16BitsToHalf((ushort)(raw[i * 2] | raw[i * 2 + 1] << 8));
        return result;
    }

    // ---- The frame -------------------------------------------------------------------------------------------

    /// <summary>
    /// Queues an HDR image for the composite: drawn on the scRGB target, beneath the whole SDR layer, in the order
    /// queued. The caller has written the quad into the vertex ring and has the push block ready
    /// (<see cref="VkRenderer.DrawHdrImage"/>); the scissor is the clip in force, replayed against the composite.
    /// </summary>
    internal void QueueHdrImage(VkBuffer buffer, ulong offset, VkDescriptorSet set, VkRect2D scissor,
        ReadOnlySpan<float> push)
    {
        if (_hdrQueueFrame != _frameOrdinal)
        {
            _hdrDrawCount = 0;
            _hdrQueueFrame = _frameOrdinal;
        }
        if (_hdrDrawCount == _hdrDraws.Length)
            Array.Resize(ref _hdrDraws, Math.Max(8, _hdrDraws.Length * 2));
        ref var d = ref _hdrDraws[_hdrDrawCount++];
        d.Buffer = buffer;
        d.Offset = offset;
        d.Set = set;
        d.Scissor = scissor;
        fixed (float* p = d.Push)
            push[..HdrImagePushFloats].CopyTo(new Span<float>(p, HdrImagePushFloats));
    }

    // What exists only for HDR, for the tests that hold an SDR context to having none of it.
    internal int HdrLayerCount => _hdrLayerImages.Length + (_hdrOffscreenImage != VkImage.Null ? 1 : 0);
    internal VkRenderPass HdrCompositePass => _hdrCompositePass;
    internal VkPipeline HdrCompositeLayerPipeline => _hdrCompositeLayerPipeline;
    internal VkPipeline HdrCompositeImagePipeline => _hdrCompositeImagePipeline;

    /// <summary>How many HDR images the frame being recorded has queued. For tests.</summary>
    internal int QueuedHdrImages => _hdrQueueFrame == _frameOrdinal ? _hdrDrawCount : 0;

    // Records the composite after the main pass has ended: layer to shader-readable, the composite pass on the float
    // target (queued images, then the layer), layer back to where the device's main pass leaves it, so the next frame's
    // pass (and the DEBUG capture, which runs before this) finds it as it does an SDR swapchain image.
    private void RecordHdrComposite(VkCommandBuffer cmd)
    {
        var idx = _isOffscreen ? 0 : (int)_currentImageIndex;
        var layer = _isOffscreen ? _offscreenImage : _swapchainImages[idx];
        var width = _isOffscreen ? _offscreenWidth : SwapchainWidth;
        var height = _isOffscreen ? _offscreenHeight : SwapchainHeight;
        var finished = _dev.ColorFinalLayout;

        TransitionImageLayout(cmd, layer, finished, VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.ColorAttachmentWrite, VkAccessFlags.ShaderRead,
            VkPipelineStageFlags.ColorAttachmentOutput, VkPipelineStageFlags.FragmentShader);

        VkClearValue clear = new();
        clear.color = new VkClearColorValue(0f, 0f, 0f, 0f);
        VkRenderPassBeginInfo rpBI = new()
        {
            renderPass = _hdrCompositePass,
            framebuffer = _hdrCompositeFramebuffers[idx],
            renderArea = new VkRect2D(0, 0, width, height),
            clearValueCount = 1,
            pClearValues = &clear
        };
        DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);
        VkViewport viewport = new(0, 0, width, height, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, viewport);
        VkRect2D full = new(0, 0, width, height);
        DeviceApi.vkCmdSetScissor(cmd, 0, full);

        if (_hdrQueueFrame == _frameOrdinal && _hdrDrawCount > 0)
        {
            _hdrCompositeImagePipeline = _hdrCompositeImagePipeline != VkPipeline.Null
                ? _hdrCompositeImagePipeline
                : VkPipelineSet.CreateHdrImagePipeline(_dev, _hdrCompositePass, VkSampleCountFlags.Count1, scRgbOutput: true);
            DeviceApi.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _hdrCompositeImagePipeline);
            var imageLayout = _dev.HdrImagePipelineLayout;
            for (var i = 0; i < _hdrDrawCount; i++)
            {
                ref var d = ref _hdrDraws[i];
                DeviceApi.vkCmdSetScissor(cmd, 0, d.Scissor);
                fixed (float* p = d.Push)
                    DeviceApi.vkCmdPushConstants(cmd, imageLayout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
                        0, VulkanDevice.HdrImagePushBytes, p);
                var set = d.Set;
                DeviceApi.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, imageLayout, 0, 1, &set, 0, null);
                var buffer = d.Buffer;
                var offset = d.Offset;
                DeviceApi.vkCmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
                DeviceApi.vkCmdDraw(cmd, 6, 1, 0, 0);
            }
            DeviceApi.vkCmdSetScissor(cmd, 0, full);
            _hdrDrawCount = 0;
        }

        DeviceApi.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Graphics, _hdrCompositeLayerPipeline);
        var layerSet = _hdrLayerSets[idx];
        DeviceApi.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Graphics, _hdrCompositeLayout, 0, 1, &layerSet, 0, null);
        var push = stackalloc float[4] { _sdrWhiteScRgb, 0f, 0f, 0f };
        DeviceApi.vkCmdPushConstants(cmd, _hdrCompositeLayout, VkShaderStageFlags.Fragment, 0, HdrCompositePushBytes, push);
        DeviceApi.vkCmdDraw(cmd, 3, 1, 0, 0);
        DeviceApi.vkCmdEndRenderPass(cmd);

        TransitionImageLayout(cmd, layer, VkImageLayout.ShaderReadOnlyOptimal, finished,
            VkAccessFlags.None, VkAccessFlags.ColorAttachmentRead | VkAccessFlags.ColorAttachmentWrite,
            VkPipelineStageFlags.FragmentShader, VkPipelineStageFlags.ColorAttachmentOutput);
    }
}
