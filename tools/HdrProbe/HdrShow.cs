using Vortice.Vulkan;
using static SDL3.SDL;
using static Vortice.Vulkan.Vulkan;

namespace HdrProbe;

/// <summary>
/// Puts HDR on the panel so a person can see it: an scRGB swapchain on the HDR-capable display and a
/// row of patches at known brightness, drawn with clear rectangles and nothing else.
/// </summary>
/// <remarks>
/// <para><b>Why clear rects.</b> The probe's job is to prove the PRESENT path, not to render. A render
/// pass whose only recorded commands are <c>vkCmdClearAttachments</c> needs no shader, no pipeline, no
/// buffer and no descriptor, so the only thing between the numbers below and the panel is the
/// swapchain's colour space. If the bright patches come out brighter than the window chrome, the
/// compositor is treating the surface as scRGB; if every patch above SDR white looks the same, it is
/// clipping at 1.0 and the colour space did not take.</para>
/// <para><b>The numbers.</b> In <c>EXTENDED_SRGB_LINEAR_EXT</c> the value 1.0 is 80 nits, linear light.
/// Windows composes SDR content at the "SDR content brightness" the user set, which SDL reports as
/// <c>SDR_WHITE_LEVEL</c> in the same units (2.00 here, so 160 nits): that is the brightest white any
/// SDR window on the desktop shows, and it is the reference patch. <c>HDR_HEADROOM</c> is how far
/// above it the panel can go (2.38 here, so about 380 nits). The top row steps from a quarter of SDR
/// white to the panel's peak; the middle row shows the three primaries at SDR white and at the peak,
/// because a saturated red at 380 nits is the thing an SDR panel simply cannot show; the bottom strip
/// is a linear ramp from black to the peak, whose upper part is where HDR lives.</para>
/// <para><b>Only the panel can judge it.</b> A screenshot cannot: Windows' capture path (Snipping Tool,
/// PrintWindow, the inspector's own screenshot) composes to an SDR bitmap, so everything above 1.0 in
/// scRGB lands at 255. Measured on the first run, Surface Pro 11 OLED, Adreno driver 31.0.170.0: the
/// top row read 188, 255, 255, 255, 255, 255 in the PNG and the ramp saturated at step 14 of 64
/// (about 1.08 in scRGB), while the panel showed every patch right of 1.0 brighter than the last,
/// which is the HDR present working. So a capture that looks clipped says nothing either way; look
/// at the screen.</para>
/// <para>Everything is single-threaded and torn down in order; a window close, Escape or Q ends it.</para>
/// </remarks>
internal static unsafe class HdrShow
{
    private const int FramesInFlight = 2;

    public static void Run(VkInstance instance, VkInstanceApi api, VkPhysicalDevice physicalDevice, string deviceName, uint display, Rect bounds)
    {
        // ---- Window on the HDR display, and the compositor's numbers for it.
        const int w = 1280, h = 760;
        var window = CreateWindow("HdrProbe: scRGB patches (Esc closes)", w, h, WindowFlags.Vulkan);
        if (window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {GetError()}");
        SetWindowPosition(window, bounds.X + (bounds.W - w) / 2, bounds.Y + (bounds.H - h) / 2);
        for (var attempt = 0; attempt < 50 && GetDisplayForWindow(window) != display; attempt++)
        {
            PumpEvents();
            Thread.Sleep(10);
        }

        var props = GetWindowProperties(window);
        var sdrWhite = GetFloatProperty(props, Props.WindowSDRWhiteLevelFloat, 1f);
        var headroom = GetFloatProperty(props, Props.WindowHDRHeadroomFloat, 1f);
        var peak = sdrWhite * headroom;

        Console.WriteLine();
        Console.WriteLine($"== Showing on {GetDisplayName(display)} via {deviceName}");
        Console.WriteLine($"   scRGB: 1.0 = 80 nits.  SDR white = {sdrWhite:F2} ({sdrWhite * 80:F0} nits).  Headroom = {headroom:F2}, so the panel peak is {peak:F2} ({peak * 80:F0} nits).");
        Console.WriteLine("   Top row, left to right, as multiples of SDR white:  0.25  0.5  1.0 (= any SDR window's white)  1.5  2.0  peak");
        Console.WriteLine("   Middle row: red, green, blue at SDR white, then the same three at the panel peak.");
        Console.WriteLine("   Bottom strip: linear ramp, black to peak. If the patches right of 1.0 are not brighter than this window's title bar, HDR did not take.");
        Console.WriteLine("   Judge it on the panel, not in a screenshot: Windows captures to SDR, so anything above 1.0 lands at 255 in the PNG whether HDR took or not.");

        if (!VulkanCreateSurface(window, instance.Handle, nint.Zero, out var surfaceHandle))
            throw new InvalidOperationException($"SDL_Vulkan_CreateSurface failed: {GetError()}");
        var surface = new VkSurfaceKHR((ulong)surfaceHandle);

        // ---- Device: one graphics queue that can present to this surface, plus VK_KHR_swapchain.
        var queueFamily = FindPresentableGraphicsQueue(api, physicalDevice, surface);
        var priority = 1f;
        VkDeviceQueueCreateInfo queueCI = new()
        {
            queueFamilyIndex = queueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        using var deviceExtensions = new VkStringArray(["VK_KHR_swapchain"]);
        VkDeviceCreateInfo deviceCI = new()
        {
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueCI,
            enabledExtensionCount = deviceExtensions.Length,
            ppEnabledExtensionNames = deviceExtensions,
        };
        api.vkCreateDevice(physicalDevice, &deviceCI, null, out var device).CheckResult();
        var dev = GetApi(instance, device);
        dev.vkGetDeviceQueue(queueFamily, 0, out var queue);

        // ---- The swapchain that is the whole point: 16-bit float in scRGB.
        api.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physicalDevice, surface, out var caps);
        GetWindowSizeInPixels(window, out var pw, out var ph);
        var extent = caps.currentExtent.width != uint.MaxValue ? caps.currentExtent : new VkExtent2D((uint)pw, (uint)ph);
        var imageCount = Math.Max(2u, caps.minImageCount);
        if (caps.maxImageCount > 0 && imageCount > caps.maxImageCount)
            imageCount = caps.maxImageCount;

        const VkFormat format = VkFormat.R16G16B16A16Sfloat;
        const VkColorSpaceKHR colorSpace = VkColorSpaceKHR.ExtendedSrgbLinearEXT;
        VkSwapchainCreateInfoKHR swapCI = new()
        {
            surface = surface,
            minImageCount = imageCount,
            imageFormat = format,
            imageColorSpace = colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = VkSurfaceTransformFlagsKHR.Identity,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = VkPresentModeKHR.Fifo,
            clipped = true,
        };
        dev.vkCreateSwapchainKHR(&swapCI, null, out var swapchain).CheckResult();
        dev.vkGetSwapchainImagesKHR(swapchain, out uint count).CheckResult();
        var images = new VkImage[count];
        dev.vkGetSwapchainImagesKHR(swapchain, images).CheckResult();
        Console.WriteLine($"   Swapchain created: {count} images, {format} / {colorSpace}, {extent.width} x {extent.height}.");

        // ---- One render pass that clears to the background and stores for present.
        VkAttachmentDescription attachment = new()
        {
            format = format,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = VkImageLayout.PresentSrcKHR,
        };
        VkAttachmentReference colorRef = new() { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        VkSubpassDescription subpass = new()
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef,
        };
        VkSubpassDependency dependency = new()
        {
            srcSubpass = VK_SUBPASS_EXTERNAL,
            dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            srcAccessMask = 0,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite,
        };
        VkRenderPassCreateInfo rpCI = new()
        {
            attachmentCount = 1,
            pAttachments = &attachment,
            subpassCount = 1,
            pSubpasses = &subpass,
            dependencyCount = 1,
            pDependencies = &dependency,
        };
        dev.vkCreateRenderPass(&rpCI, null, out var renderPass).CheckResult();

        var views = new VkImageView[count];
        var framebuffers = new VkFramebuffer[count];
        for (var i = 0; i < count; i++)
        {
            var viewCI = new VkImageViewCreateInfo(images[i], VkImageViewType.Image2D, format, VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            dev.vkCreateImageView(&viewCI, null, out views[i]).CheckResult();

            var view = views[i];
            VkFramebufferCreateInfo fbCI = new()
            {
                renderPass = renderPass,
                attachmentCount = 1,
                pAttachments = &view,
                width = extent.width,
                height = extent.height,
                layers = 1,
            };
            dev.vkCreateFramebuffer(&fbCI, null, out framebuffers[i]).CheckResult();
        }

        // ---- The picture is static, so one command buffer per swapchain image, recorded once.
        VkCommandPoolCreateInfo poolCI = new() { queueFamilyIndex = queueFamily };
        dev.vkCreateCommandPool(&poolCI, null, out var pool).CheckResult();
        var commands = new VkCommandBuffer[count];
        for (var i = 0; i < count; i++)
        {
            dev.vkAllocateCommandBuffer(pool, out commands[i]).CheckResult();
            RecordPatches(dev, commands[i], renderPass, framebuffers[i], extent, sdrWhite, peak);
        }

        var imageAvailable = new VkSemaphore[FramesInFlight];
        var inFlight = new VkFence[FramesInFlight];
        var renderFinished = new VkSemaphore[count];
        for (var i = 0; i < FramesInFlight; i++)
        {
            dev.vkCreateSemaphore(out imageAvailable[i]).CheckResult();
            dev.vkCreateFence(VkFenceCreateFlags.Signaled, out inFlight[i]).CheckResult();
        }
        for (var i = 0; i < count; i++)
            dev.vkCreateSemaphore(out renderFinished[i]).CheckResult();

        // ---- Present until told to stop.
        var frame = 0;
        var running = true;
        while (running)
        {
            while (PollEvent(out var e))
            {
                var type = (EventType)e.Type;
                if (type is EventType.Quit or EventType.WindowCloseRequested)
                    running = false;
                else if (type == EventType.KeyDown && e.Key.Scancode is Scancode.Escape or Scancode.Q)
                    running = false;
            }
            if (!running)
                break;

            var fence = inFlight[frame];
            dev.vkWaitForFences(1, &fence, true, ulong.MaxValue);

            var acquire = dev.vkAcquireNextImageKHR(swapchain, ulong.MaxValue, imageAvailable[frame], VkFence.Null, out var imageIndex);
            if (acquire == VkResult.ErrorOutOfDateKHR || acquire == VkResult.SuboptimalKHR)
            {
                // A resize or a display change would need a swapchain rebuild; this is a probe, so end.
                Console.WriteLine($"   Swapchain reported {acquire}; a resize or display change ended the show.");
                break;
            }
            acquire.CheckResult();

            dev.vkResetFences(1, &fence);
            var wait = imageAvailable[frame];
            var signal = renderFinished[imageIndex];
            var cmd = commands[imageIndex];
            var stage = VkPipelineStageFlags.ColorAttachmentOutput;
            VkSubmitInfo submit = new()
            {
                waitSemaphoreCount = 1,
                pWaitSemaphores = &wait,
                pWaitDstStageMask = &stage,
                commandBufferCount = 1,
                pCommandBuffers = &cmd,
                signalSemaphoreCount = 1,
                pSignalSemaphores = &signal,
            };
            dev.vkQueueSubmit(queue, 1, &submit, fence).CheckResult();

            VkPresentInfoKHR present = new()
            {
                waitSemaphoreCount = 1,
                pWaitSemaphores = &signal,
                swapchainCount = 1,
                pSwapchains = &swapchain,
                pImageIndices = &imageIndex,
            };
            var presented = dev.vkQueuePresentKHR(queue, &present);
            if (presented is not (VkResult.Success or VkResult.SuboptimalKHR))
            {
                Console.WriteLine($"   vkQueuePresentKHR returned {presented}; ending.");
                break;
            }

            frame = (frame + 1) % FramesInFlight;
        }

        // ---- Tear down in order.
        dev.vkDeviceWaitIdle();
        foreach (var s in renderFinished) dev.vkDestroySemaphore(s);
        foreach (var s in imageAvailable) dev.vkDestroySemaphore(s);
        foreach (var f in inFlight) dev.vkDestroyFence(f);
        dev.vkDestroyCommandPool(pool);
        foreach (var fb in framebuffers) dev.vkDestroyFramebuffer(fb);
        foreach (var v in views) dev.vkDestroyImageView(v);
        dev.vkDestroyRenderPass(renderPass);
        dev.vkDestroySwapchainKHR(swapchain);
        dev.vkDestroyDevice();
        api.vkDestroySurfaceKHR(surface);
        DestroyWindow(window);
    }

    /// <summary>The patches, as clear rectangles inside one render pass whose own clear is the background.</summary>
    private static void RecordPatches(VkDeviceApi dev, VkCommandBuffer cmd, VkRenderPass renderPass, VkFramebuffer framebuffer,
        VkExtent2D extent, float sdrWhite, float peak)
    {
        VkCommandBufferBeginInfo begin = new();
        dev.vkBeginCommandBuffer(cmd, &begin).CheckResult();

        // Background: 18 percent of SDR white, the photographer's mid grey, so both rows have room to
        // read darker AND brighter than their surroundings.
        var grey = 0.18f * sdrWhite;
        var background = new VkClearValue(grey, grey, grey, 1f);
        VkRenderPassBeginInfo rpBegin = new()
        {
            renderPass = renderPass,
            framebuffer = framebuffer,
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            clearValueCount = 1,
            pClearValues = &background,
        };
        dev.vkCmdBeginRenderPass(cmd, &rpBegin, VkSubpassContents.Inline);

        var width = (int)extent.width;
        var height = (int)extent.height;
        var margin = width / 40;
        var gap = margin / 2;

        // Top row: multiples of SDR white. 1.0 is the reference; everything right of it is HDR.
        float[] multiples = [0.25f, 0.5f, 1f, 1.5f, 2f, peak / sdrWhite];
        var rowTop = margin;
        var rowHeight = (height - 4 * margin) / 3;
        var patchWidth = (width - 2 * margin - (multiples.Length - 1) * gap) / multiples.Length;
        for (var i = 0; i < multiples.Length; i++)
        {
            var v = Math.Min(multiples[i] * sdrWhite, peak);
            Clear(dev, cmd, margin + i * (patchWidth + gap), rowTop, patchWidth, rowHeight, v, v, v);
        }

        // Middle row: primaries at SDR white, then at the panel peak.
        var rowMid = rowTop + rowHeight + margin;
        (float R, float G, float B)[] primaries = [(1, 0, 0), (0, 1, 0), (0, 0, 1)];
        for (var i = 0; i < 3; i++)
        {
            var (r, g, b) = primaries[i];
            Clear(dev, cmd, margin + i * (patchWidth + gap), rowMid, patchWidth, rowHeight, r * sdrWhite, g * sdrWhite, b * sdrWhite);
            Clear(dev, cmd, margin + (i + 3) * (patchWidth + gap), rowMid, patchWidth, rowHeight, r * peak, g * peak, b * peak);
        }

        // Bottom strip: 64 steps, black to peak, linear in light.
        var rowBottom = rowMid + rowHeight + margin;
        const int steps = 64;
        var stepWidth = (width - 2 * margin) / steps;
        for (var i = 0; i < steps; i++)
        {
            var v = peak * (i + 0.5f) / steps;
            Clear(dev, cmd, margin + i * stepWidth, rowBottom, stepWidth, rowHeight, v, v, v);
        }

        dev.vkCmdEndRenderPass(cmd);
        dev.vkEndCommandBuffer(cmd).CheckResult();
    }

    private static void Clear(VkDeviceApi dev, VkCommandBuffer cmd, int x, int y, int w, int h, float r, float g, float b)
    {
        VkClearAttachment attachment = new()
        {
            aspectMask = VkImageAspectFlags.Color,
            colorAttachment = 0,
            clearValue = new VkClearValue(r, g, b, 1f),
        };
        VkClearRect rect = new()
        {
            rect = new VkRect2D(x, y, (uint)w, (uint)h),
            baseArrayLayer = 0,
            layerCount = 1,
        };
        dev.vkCmdClearAttachments(cmd, 1, &attachment, 1, &rect);
    }

    private static uint FindPresentableGraphicsQueue(VkInstanceApi api, VkPhysicalDevice device, VkSurfaceKHR surface)
    {
        uint count = 0;
        api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
            api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].queueFlags & VkQueueFlags.Graphics) == 0)
                continue;
            api.vkGetPhysicalDeviceSurfaceSupportKHR(device, i, surface, out var supported);
            if (supported)
                return i;
        }
        throw new InvalidOperationException("No graphics queue family can present to the HDR window's surface.");
    }
}
