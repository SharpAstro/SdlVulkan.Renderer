using System.Text;
using Vortice.Vulkan;
using static SDL3.SDL;
using static Vortice.Vulkan.Vulkan;

namespace HdrProbe;

/// <summary>
/// Can this machine present HDR through SDL3 + Vulkan, and on which display?
/// </summary>
/// <remarks>
/// <para>The library's README chose Vulkan over OpenGL because an HDR swapchain is POSSIBLE through
/// <c>VK_EXT_swapchain_colorspace</c>; whether it is possible on a given box is a fact about that box's
/// driver, and this is the tool that states it. Three layers have to agree, and each is printed on its
/// own so a "no" names which one said it:</para>
/// <list type="number">
/// <item>The Vulkan LOADER has to offer <c>VK_EXT_swapchain_colorspace</c> as an instance extension.
/// Without it a driver reports <c>SRGB_NONLINEAR</c> for every format, whatever the panel can do, and
/// nothing else can even be asked for. Measured 2026-09-15 on one Surface Pro 11 (Adreno X1-85),
/// Windows HDR ON for its OLED, twice in one afternoon: the OEM-channel driver 31.0.137.0 listed
/// thirteen instance extensions and not this one, every surface SRGB_NONLINEAR only, exit 2; the
/// Qualcomm Software Center driver 31.0.170.0 (their "2026.08.2") listed fourteen including it,
/// reported Vulkan 1.4, and the same surface gained <c>R16G16B16A16_SFLOAT</c> in
/// <c>EXTENDED_SRGB_LINEAR_EXT</c>, exit 0. Same panel, same Windows, one driver apart, which is the
/// whole reason this is a tool and not a table.</item>
/// <item>SDL3 has to see HDR on the DISPLAY the window is on (<c>SDL_PROP_DISPLAY_HDR_ENABLED</c>) and
/// on the WINDOW (<c>HDR_ENABLED</c>, <c>SDR_WHITE_LEVEL</c>, <c>HDR_HEADROOM</c>). That is the
/// compositor's view, independent of Vulkan; on a two-display box it differs per display.</item>
/// <item>The SURFACE created on that window has to list <c>EXTENDED_SRGB_LINEAR_EXT</c> (scRGB, the
/// Windows-native path, with <c>R16G16B16A16_SFLOAT</c>) or <c>HDR10_ST2084_EXT</c>. Only a format in
/// one of those colour spaces can carry values above SDR white to the compositor.</item>
/// </list>
/// <para>A window is created on every display because the surface formats are per surface, and a
/// surface belongs to a window on one display. The windows are small, borderless and short-lived.</para>
/// </remarks>
internal static unsafe class Program
{
    private const string SwapchainColorSpaceExtension = "VK_EXT_swapchain_colorspace";
    private const string SurfaceCapabilities2Extension = "VK_KHR_get_surface_capabilities2";

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"HdrProbe failed: {ex}");
            return 1;
        }
    }

    private static int Run()
    {
        if (!Init(InitFlags.Video | InitFlags.Events))
            throw new InvalidOperationException($"SDL_Init failed: {GetError()}");
        VulkanLoadLibrary(null);
        vkInitialize().CheckResult();

        // ---- Layer 1: the loader's instance extensions.
        var available = new HashSet<string>(StringComparer.Ordinal);
        uint extensionCount = 0;
        vkEnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null).CheckResult();
        var extensions = new VkExtensionProperties[extensionCount];
        fixed (VkExtensionProperties* p = extensions)
            vkEnumerateInstanceExtensionProperties((byte*)null, &extensionCount, p).CheckResult();
        foreach (var ext in extensions)
        {
            var name = VkStringInterop.ConvertToManaged(ext.extensionName);
            if (name is not null)
                available.Add(name);
        }

        var hasColorSpaceExt = available.Contains(SwapchainColorSpaceExtension);
        var hasCaps2Ext = available.Contains(SurfaceCapabilities2Extension);

        Console.WriteLine("== Vulkan loader");
        Console.WriteLine($"   instance extensions: {available.Count}");
        Console.WriteLine($"   {SwapchainColorSpaceExtension}: {(hasColorSpaceExt ? "present" : "ABSENT (every surface will report SRGB_NONLINEAR only)")}");
        Console.WriteLine($"   {SurfaceCapabilities2Extension}: {(hasCaps2Ext ? "present" : "absent")}");

        var sdlExtensions = VulkanGetInstanceExtensions(out _)
            ?? throw new InvalidOperationException($"SDL_Vulkan_GetInstanceExtensions failed: {GetError()}");
        var enabled = new List<string>(sdlExtensions);
        if (hasColorSpaceExt) enabled.Add(SwapchainColorSpaceExtension);
        if (hasCaps2Ext) enabled.Add(SurfaceCapabilities2Extension);

        using var extensionNames = new VkStringArray(enabled.ToArray());
        VkInstanceCreateInfo instanceCI = new()
        {
            enabledExtensionCount = extensionNames.Length,
            ppEnabledExtensionNames = extensionNames,
        };
        vkCreateInstance(&instanceCI, null, out var instance).CheckResult();
        var api = GetApi(instance);

        // ---- The physical devices, named once.
        uint deviceCount = 0;
        api.vkEnumeratePhysicalDevices(&deviceCount, null);
        var devices = new VkPhysicalDevice[deviceCount];
        fixed (VkPhysicalDevice* p = devices)
            api.vkEnumeratePhysicalDevices(&deviceCount, p);

        Console.WriteLine();
        Console.WriteLine($"== Physical devices: {deviceCount}");
        var deviceNames = new string[deviceCount];
        for (var i = 0; i < deviceCount; i++)
        {
            api.vkGetPhysicalDeviceProperties(devices[i], out var props);
            deviceNames[i] = VkStringInterop.ConvertToManaged(props.deviceName) ?? $"<device {i}>";
            // The driver version's encoding is vendor-specific (vulkaninfo decodes Qualcomm's
            // 2150985728 as 0.855.0), so it is printed raw rather than decoded wrongly.
            var v = props.apiVersion;
            Console.WriteLine($"   [{i}] {deviceNames[i]}  ({props.deviceType}, Vulkan {v.Major}.{v.Minor}.{v.Patch}, driverVersion raw {props.driverVersion})");
        }

        // ---- Layers 2 and 3, per display.
        var displays = GetDisplays(out var displayCount)
            ?? throw new InvalidOperationException($"SDL_GetDisplays failed: {GetError()}");
        var anyHdrCapable = false;

        for (var d = 0; d < displayCount; d++)
        {
            var display = displays[d];
            var displayName = GetDisplayName(display) ?? $"display {display}";
            GetDisplayBounds(display, out var bounds);
            var displayProps = GetDisplayProperties(display);
            var displayHdr = GetBooleanProperty(displayProps, Props.DisplayHDREnabledBoolean, false);

            Console.WriteLine();
            Console.WriteLine($"== Display {d}: {displayName}  {bounds.W} x {bounds.H} at ({bounds.X}, {bounds.Y})");
            Console.WriteLine($"   SDL display HDR enabled: {displayHdr}");

            // A small borderless window INSIDE this display's bounds: the surface belongs to the window,
            // and the window's HDR properties are the compositor's answer for the display it is on.
            var window = CreateWindow("HdrProbe", 320, 200, WindowFlags.Vulkan | WindowFlags.Borderless);
            if (window == nint.Zero)
                throw new InvalidOperationException($"SDL_CreateWindow failed: {GetError()}");
            SetWindowPosition(window, bounds.X + 48, bounds.Y + 48);

            // The window is created on the primary display and MOVED here, and SDL re-derives the
            // window's HDR properties when the move lands, not when it is asked for. One pump is not
            // enough: the first version of this read the primary panel's "HDR on, headroom 2.31" for
            // every display. So wait until SDL itself says the window is on the display being probed,
            // and say so if it never does, because then the numbers below are the wrong display's.
            var landed = false;
            for (var attempt = 0; attempt < 50 && !landed; attempt++)
            {
                PumpEvents();
                landed = GetDisplayForWindow(window) == display;
                if (!landed)
                    Thread.Sleep(10);
            }
            if (!landed)
                Console.WriteLine("   WARNING: SDL never placed the probe window on this display; the window numbers below belong to another one.");

            // Reported but NOT authoritative for the display. Measured 2026-09-15 on a Surface Pro 11 with
            // two Apple Studio Display heads attached: the DISPLAY property said HDR off for both Apple
            // panels, while every window, wherever SDL agreed it was, read "HDR on, SDR white 2.00,
            // headroom 2.31", the OLED's numbers. On Windows the window values follow the HDR desktop
            // rather than the monitor under the window, so read the display property for "is this panel
            // HDR" and these three for "what does the compositor think white is".
            var windowProps = GetWindowProperties(window);
            var windowHdr = GetBooleanProperty(windowProps, Props.WindowHDREnabledBoolean, false);
            var sdrWhite = GetFloatProperty(windowProps, Props.WindowSDRWhiteLevelFloat, 1f);
            var headroom = GetFloatProperty(windowProps, Props.WindowHDRHeadroomFloat, 1f);
            Console.WriteLine($"   SDL window  HDR enabled: {windowHdr}   SDR white level: {sdrWhite:F2}   HDR headroom: {headroom:F2}   (window values follow the HDR desktop, not this panel; the display line above is the one to trust)");

            if (!VulkanCreateSurface(window, instance.Handle, nint.Zero, out var surfaceHandle))
                throw new InvalidOperationException($"SDL_Vulkan_CreateSurface failed: {GetError()}");
            var surface = new VkSurfaceKHR((ulong)surfaceHandle);

            for (var i = 0; i < deviceCount; i++)
            {
                uint count = 0;
                api.vkGetPhysicalDeviceSurfaceFormatsKHR(devices[i], surface, &count, null);
                var formats = new VkSurfaceFormatKHR[count];
                fixed (VkSurfaceFormatKHR* p = formats)
                    api.vkGetPhysicalDeviceSurfaceFormatsKHR(devices[i], surface, &count, p);

                var scRgb = false;
                var hdr10 = false;
                var sb = new StringBuilder();
                foreach (var f in formats)
                {
                    scRgb |= f.colorSpace == VkColorSpaceKHR.ExtendedSrgbLinearEXT;
                    hdr10 |= f.colorSpace == VkColorSpaceKHR.Hdr10St2084EXT;
                    sb.Append("      ").Append(f.format).Append("  ").Append(f.colorSpace).AppendLine();
                }

                var verdict = scRgb ? "HDR possible (scRGB)" : hdr10 ? "HDR possible (HDR10 PQ)" : "SDR only";
                Console.WriteLine($"   [{i}] {deviceNames[i]}: {count} surface formats, {verdict}");
                Console.Write(sb);
                anyHdrCapable |= scRgb || hdr10;
            }

            api.vkDestroySurfaceKHR(surface);
            DestroyWindow(window);
        }

        api.vkDestroyInstance();
        Quit();

        Console.WriteLine();
        Console.WriteLine(anyHdrCapable
            ? "VERDICT: at least one display can take an HDR swapchain through SDL3 + Vulkan on this machine."
            : hasColorSpaceExt
                ? "VERDICT: the loader offers VK_EXT_swapchain_colorspace but no surface listed an HDR colour space; check Windows HDR is on for the display."
                : "VERDICT: no HDR present is possible through Vulkan here; the loader offers no VK_EXT_swapchain_colorspace, so the driver is the block, not the panel.");
        return anyHdrCapable ? 0 : 2;
    }
}
