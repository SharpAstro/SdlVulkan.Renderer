using Vortice.Vulkan;
using static SDL3.SDL;
using static Vortice.Vulkan.Vulkan;

namespace TransparencyProbe;

/// <summary>
/// "present": a Vulkan window over the <see cref="Backdrop"/>, presenting a known premultiplied picture with nothing
/// between it and the swapchain (no renderer, no shaders: clear rectangles only), scored against the capture:
/// </summary>
/// <remarks>
/// <code>
///   left third    alpha 0                  real alpha: the checkerboard unchanged; opaque: black
///   middle third  orange at alpha 0.5      real alpha: half checkerboard, half orange; opaque: half orange on black
///   right third   orange at alpha 1        orange either way
///   bottom strip  orange, alpha 0 to 1     a soft ramp (not scored; look at the picture)
/// </code>
/// The swapchain asks for <c>PRE_MULTIPLIED</c> where the surface offers it and <c>OPAQUE</c> otherwise, which is
/// what the library does, so a device that blends under <c>OPAQUE</c> shows up as "real alpha" beside it.
/// </remarks>
internal static unsafe class Present
{
    private const int W = 360, H = 240;
    private const int OrangeR = 240, OrangeG = 128, OrangeB = 32;

    private enum Verdict { RealAlpha, Opaque, Other, CaptureBlack }

    public static int Run(VkInstance instance, VkInstanceApi api, string[] args, string outDir)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("present: give a device as driver:device-substring, or \"all\".");
            return 1;
        }
        var devices = Program.Devices(api);
        using var backdrop = new Backdrop(W, H);
        if (Score.IsBlack(backdrop.Before))
        {
            Console.WriteLine("The screen captured black before anything was opened: is the display on, and the session visible?");
            return 3;
        }

        if (args[0] == "all")
        {
            Console.WriteLine($"{"device [driver]",-62} {"window",-11} {"present",-8} {"alpha mode",-13} {"left",6} {"mid",6} {"right",6}  verdict");
            var worst = 0;
            foreach (var d in devices)
            foreach (var kind in new[] { "transparent", "plain" })
            foreach (var mode in new[] { VkPresentModeKHR.Fifo, VkPresentModeKHR.Mailbox })
            {
                var verdict = RunOne(instance, api, d, kind, mode, backdrop, outDir, quiet: true);
                if (verdict == Verdict.CaptureBlack)
                    worst = 3;
            }
            Console.WriteLine($"Pictures in {outDir}.");
            return worst;
        }

        var pick = args[0].Split(':', 2);
        var match = devices.FirstOrDefault(d => d.Driver == pick[0]
            && d.Name.Contains(pick.Length > 1 ? pick[1] : "", StringComparison.OrdinalIgnoreCase));
        if (match.Device.Handle == 0)
        {
            Console.Error.WriteLine($"No device with driver '{pick[0]}'. These exist:");
            foreach (var d in devices)
                Console.Error.WriteLine($"  {d.Driver}:{d.Name}");
            return 1;
        }
        var windowKind = args.Length > 1 ? args[1] : "transparent";
        var presentMode = args.Length > 2 && args[2] == "mailbox" ? VkPresentModeKHR.Mailbox : VkPresentModeKHR.Fifo;
        return RunOne(instance, api, match, windowKind, presentMode, backdrop, outDir, quiet: false) switch
        {
            Verdict.CaptureBlack => 3,
            _ => 0,
        };
    }

    private static Verdict RunOne(VkInstance instance, VkInstanceApi api,
        (VkPhysicalDevice Device, string Name, string Driver, string Info) d, string windowKind,
        VkPresentModeKHR presentMode, Backdrop backdrop, string outDir, bool quiet)
    {
        var head = $"{$"{d.Name} [{d.Driver}]",-62} {windowKind,-11} {presentMode,-8}";
        var flags = WindowFlags.Vulkan | WindowFlags.Borderless | WindowFlags.AlwaysOnTop;
        if (windowKind is "transparent" or "shape")
            flags |= WindowFlags.Transparent;
        var window = CreateWindow("TransparencyProbe", W, H, flags);
        SetWindowPosition(window, backdrop.SdlX, backdrop.SdlY);
        RaiseWindow(window);
        PumpEvents();
        if (windowKind == "shape")
            ApplyShape(window);
        if (!VulkanCreateSurface(window, instance.Handle, 0, out var surfaceHandle))
            throw new InvalidOperationException(GetError());
        var surface = new VkSurfaceKHR((ulong)surfaceHandle);
        try
        {
            if (!Program.CanPresent(api, d.Device, surface, out var family))
            {
                Console.WriteLine($"{head} cannot present to this window");
                return Verdict.Other;
            }
            if (!Program.PresentModes(api, d.Device, surface).Contains(presentMode))
            {
                Console.WriteLine($"{head} {presentMode} not offered");
                return Verdict.Other;
            }
            return Show(instance, api, d.Device, family, surface, window, presentMode, backdrop, head, windowKind, outDir, d.Name, quiet);
        }
        finally
        {
            api.vkDestroySurfaceKHR(surface);
            DestroyWindow(window);
        }
    }

    // The drawn areas (middle and right thirds, the ramp), less a strip of the opaque third below the scored band:
    // a shape on top of whatever the swapchain does, so "shape" shows whether the region also clips the content.
    private static void ApplyShape(nint window)
    {
        var shape = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var drawn = (x >= 120 && y < 160) || y >= 180;
            var hole = x >= 250 && x < 350 && y >= 145 && y < 160;
            if (drawn && !hole)
                shape[(y * W + x) * 4 + 3] = 255;
        }
        fixed (byte* p = shape)
        {
            var surface = CreateSurfaceFrom(W, H, PixelFormat.ABGR8888, (nint)p, W * 4);
            if (!SetWindowShape(window, surface))
                Console.WriteLine($"SDL_SetWindowShape: {GetError()}");
            DestroySurface(surface);
        }
    }

    private static Verdict Show(VkInstance instance, VkInstanceApi api, VkPhysicalDevice physical, uint family,
        VkSurfaceKHR surface, nint window, VkPresentModeKHR presentMode, Backdrop backdrop, string head,
        string windowKind, string outDir, string deviceName, bool quiet)
    {
        var priority = 1f;
        VkDeviceQueueCreateInfo queueCI = new() { queueFamilyIndex = family, queueCount = 1, pQueuePriorities = &priority };
        using var deviceExtensions = new VkStringArray(["VK_KHR_swapchain"]);
        VkDeviceCreateInfo deviceCI = new()
        {
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueCI,
            enabledExtensionCount = deviceExtensions.Length,
            ppEnabledExtensionNames = deviceExtensions,
        };
        api.vkCreateDevice(physical, &deviceCI, null, out var device).CheckResult();
        var dev = GetApi(instance, device);
        dev.vkGetDeviceQueue(family, 0, out var queue);

        api.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physical, surface, out var caps);
        var alpha = (caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.PreMultiplied) != 0
            ? VkCompositeAlphaFlagsKHR.PreMultiplied
            : VkCompositeAlphaFlagsKHR.Opaque;
        uint formatCount = 0;
        api.vkGetPhysicalDeviceSurfaceFormatsKHR(physical, surface, &formatCount, null);
        var formats = new VkSurfaceFormatKHR[formatCount];
        fixed (VkSurfaceFormatKHR* p = formats)
            api.vkGetPhysicalDeviceSurfaceFormatsKHR(physical, surface, &formatCount, p);
        var format = formats.FirstOrDefault(f => f.format == VkFormat.B8G8R8A8Unorm, formats[0]);
        var extent = caps.currentExtent.width != uint.MaxValue ? caps.currentExtent : new VkExtent2D(W, H);
        var imageCount = Math.Max(2u, caps.minImageCount);
        if (presentMode == VkPresentModeKHR.Mailbox)
            imageCount = Math.Max(3u, imageCount);
        if (caps.maxImageCount > 0 && imageCount > caps.maxImageCount)
            imageCount = caps.maxImageCount;
        VkSwapchainCreateInfoKHR swapCI = new()
        {
            surface = surface,
            minImageCount = imageCount,
            imageFormat = format.format,
            imageColorSpace = format.colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = VkSurfaceTransformFlagsKHR.Identity,
            compositeAlpha = alpha,
            presentMode = presentMode,
            clipped = true,
        };
        dev.vkCreateSwapchainKHR(&swapCI, null, out var swapchain).CheckResult();
        dev.vkGetSwapchainImagesKHR(swapchain, out uint imageTotal).CheckResult();
        var images = new VkImage[imageTotal];
        dev.vkGetSwapchainImagesKHR(swapchain, images).CheckResult();

        var renderPass = CreateRenderPass(dev, format.format);
        VkCommandPoolCreateInfo poolCI = new() { queueFamilyIndex = family };
        dev.vkCreateCommandPool(&poolCI, null, out var pool).CheckResult();
        var views = new VkImageView[imageTotal];
        var framebuffers = new VkFramebuffer[imageTotal];
        var commands = new VkCommandBuffer[imageTotal];
        var renderFinished = new VkSemaphore[imageTotal];
        for (var i = 0; i < imageTotal; i++)
        {
            var viewCI = new VkImageViewCreateInfo(images[i], VkImageViewType.Image2D, format.format, VkComponentMapping.Rgba,
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
            dev.vkAllocateCommandBuffer(pool, out commands[i]).CheckResult();
            Record(dev, commands[i], renderPass, framebuffers[i], extent);
            dev.vkCreateSemaphore(out renderFinished[i]).CheckResult();
        }
        dev.vkCreateSemaphore(out var imageAvailable).CheckResult();
        dev.vkCreateFence(VkFenceCreateFlags.Signaled, out var inFlight).CheckResult();

        var hwnd = GetPointerProperty(GetWindowProperties(window), Props.WindowWin32HWNDPointer, 0);
        var verdict = Verdict.Other;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            PumpEvents();
            backdrop.Paint();
            dev.vkWaitForFences(1, &inFlight, true, ulong.MaxValue);
            dev.vkAcquireNextImageKHR(swapchain, ulong.MaxValue, imageAvailable, VkFence.Null, out var index).CheckResult();
            dev.vkResetFences(1, &inFlight);
            var cmd = commands[index];
            var signal = renderFinished[index];
            var stage = VkPipelineStageFlags.ColorAttachmentOutput;
            VkSubmitInfo submit = new()
            {
                waitSemaphoreCount = 1,
                pWaitSemaphores = &imageAvailable,
                pWaitDstStageMask = &stage,
                commandBufferCount = 1,
                pCommandBuffers = &cmd,
                signalSemaphoreCount = 1,
                pSignalSemaphores = &signal,
            };
            dev.vkQueueSubmit(queue, 1, &submit, inFlight).CheckResult();
            VkPresentInfoKHR present = new()
            {
                waitSemaphoreCount = 1,
                pWaitSemaphores = &signal,
                swapchainCount = 1,
                pSwapchains = &swapchain,
                pImageIndices = &index,
            };
            dev.vkQueuePresentKHR(queue, &present);
            if (presentMode == VkPresentModeKHR.Mailbox)
                Thread.Sleep(8); // Mailbox does not pace; keep it from spinning the GPU flat out

            if (clock.Elapsed.TotalSeconds >= 1.5)
            {
                var after = backdrop.Capture();
                verdict = Judge(backdrop, after, alpha, hwnd, head, windowKind, outDir, deviceName, presentMode, quiet);
                break;
            }
        }

        dev.vkDeviceWaitIdle();
        dev.vkDestroyFence(inFlight);
        dev.vkDestroySemaphore(imageAvailable);
        foreach (var s in renderFinished) dev.vkDestroySemaphore(s);
        dev.vkDestroyCommandPool(pool);
        foreach (var fb in framebuffers) dev.vkDestroyFramebuffer(fb);
        foreach (var v in views) dev.vkDestroyImageView(v);
        dev.vkDestroyRenderPass(renderPass);
        dev.vkDestroySwapchainKHR(swapchain);
        dev.vkDestroyDevice();
        return verdict;
    }

    private static Verdict Judge(Backdrop backdrop, byte[] after, VkCompositeAlphaFlagsKHR alpha, nint hwnd, string head,
        string windowKind, string outDir, string deviceName, VkPresentModeKHR presentMode, bool quiet)
    {
        if (Score.IsBlack(after))
        {
            Console.WriteLine($"{head} {alpha,-13} screen captured black: display off, or the Remote Desktop client minimized?");
            return Verdict.CaptureBlack;
        }
        var before = backdrop.Before;
        double Orange(int i, double a) => a * ((i % 4) switch { 0 => OrangeB, 1 => OrangeG, _ => OrangeR });
        double Blended(int x0, double a) => Score.MeanError(after, W, x0 + 10, 20, x0 + 110, 140, i => before[i] * (1 - a) + Orange(i, a));
        double OnBlack(int x0, double a) => Score.MeanError(after, W, x0 + 10, 20, x0 + 110, 140, i => Orange(i, a));
        double left = Blended(0, 0), middle = Blended(120, 0.5), right = Blended(240, 1);
        double leftOpaque = OnBlack(0, 0), middleOpaque = OnBlack(120, 0.5);

        // In "shape" the left third is outside the window's region, so it shows the backdrop whatever the alpha.
        var verdict = windowKind != "shape" && left < 6 && middle < 6 && right < 6 ? Verdict.RealAlpha
            : windowKind == "shape" && middle < 6 && right < 6 ? Verdict.RealAlpha
            : leftOpaque < 6 && middleOpaque < 6 ? Verdict.Opaque
            : windowKind == "shape" && middleOpaque < 6 ? Verdict.Opaque
            : Verdict.Other;
        var name = new string(deviceName.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var path = Path.Combine(outDir, $"present-{name}-{windowKind}-{presentMode}.png");
        Score.WriteSideBySide(path, before, after, W, H);

        var text = verdict switch
        {
            Verdict.RealAlpha => "REAL ALPHA",
            Verdict.Opaque => "opaque",
            _ => "neither (look at the picture)",
        };
        Console.WriteLine($"{head} {alpha,-13} {left,6:F1} {middle,6:F1} {right,6:F1}  {text}");
        if (!quiet)
        {
            Console.WriteLine($"  mean error per channel against real alpha: left {left:F1}, middle {middle:F1}, right {right:F1};"
                + $" against opaque: left {leftOpaque:F1}, middle {middleOpaque:F1}");
            var click = backdrop.WindowAt(60, 80);
            Console.WriteLine($"  a click on the alpha-0 third {(click == backdrop.Hwnd ? "goes through to the backdrop" : click == hwnd ? "is caught by the window" : "lands elsewhere")}");
            Console.WriteLine($"  picture: {path} (backdrop alone | with the window)");
        }
        return verdict;
    }

    private static VkRenderPass CreateRenderPass(VkDeviceApi dev, VkFormat format)
    {
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
        return renderPass;
    }

    // The picture, as clear rectangles in a pass whose own clear is transparent. Premultiplied: colour times alpha.
    private static void Record(VkDeviceApi dev, VkCommandBuffer cmd, VkRenderPass renderPass, VkFramebuffer framebuffer, VkExtent2D extent)
    {
        VkCommandBufferBeginInfo begin = new();
        dev.vkBeginCommandBuffer(cmd, &begin).CheckResult();
        var transparent = new VkClearValue(0f, 0f, 0f, 0f);
        VkRenderPassBeginInfo rpBegin = new()
        {
            renderPass = renderPass,
            framebuffer = framebuffer,
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            clearValueCount = 1,
            pClearValues = &transparent,
        };
        dev.vkCmdBeginRenderPass(cmd, &rpBegin, VkSubpassContents.Inline);
        Clear(dev, cmd, 120, 0, 120, 160, 0.5f);
        Clear(dev, cmd, 240, 0, 120, 160, 1f);
        const int Steps = 36;
        for (var i = 0; i < Steps; i++)
            Clear(dev, cmd, i * 10, 180, 10, 60, (i + 0.5f) / Steps);
        dev.vkCmdEndRenderPass(cmd);
        dev.vkEndCommandBuffer(cmd).CheckResult();
    }

    private static void Clear(VkDeviceApi dev, VkCommandBuffer cmd, int x, int y, int w, int h, float a)
    {
        VkClearAttachment attachment = new()
        {
            aspectMask = VkImageAspectFlags.Color,
            colorAttachment = 0,
            clearValue = new VkClearValue(OrangeR / 255f * a, OrangeG / 255f * a, OrangeB / 255f * a, a),
        };
        VkClearRect rect = new() { rect = new VkRect2D(x, y, (uint)w, (uint)h), layerCount = 1 };
        dev.vkCmdClearAttachments(cmd, 1, &attachment, 1, &rect);
    }
}
