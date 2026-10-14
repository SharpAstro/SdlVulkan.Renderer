using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static SDL3.SDL;
using static Vortice.Vulkan.Vulkan;

namespace TransparencyProbe;

/// <summary>
/// Does a transparent window actually show the desktop through, on this machine, per Vulkan device?
/// </summary>
/// <remarks>
/// <para>Vulkan states it per swapchain, through the composite-alpha modes a surface offers, and the library asks for
/// <c>PRE_MULTIPLIED</c> wherever it is offered (<c>VulkanContext.CreateTransparent</c>). What a surface offers is
/// only half the story on Windows, and this tool exists for the other half. Measured 2026-10-14 on an x64 desktop
/// (GTX 1070, NVIDIA 582.66; Intel UHD 630), inside a Remote Desktop session: the native drivers offer
/// <c>OPAQUE</c> only (Intel adds <c>INHERIT</c>) for every kind of window, yet on an <c>SDL_WINDOW_TRANSPARENT</c>
/// window NVIDIA blended the frame's alpha under Mailbox presentation (not Fifo) and Intel under Fifo (it offers no
/// Mailbox). Mesa's Dozen offered <c>PRE_MULTIPLIED</c> and blended under every mode, on every window kind. The same
/// day on a Surface (Adreno X1-85, Qualcomm 31.0.170.0) at its console: <c>OPAQUE</c> only, blended under no mode
/// and on no window kind, and Dozen there as on the desktop. None of that is visible to Vulkan, so the only
/// way to know it for another machine, driver or session type is to put a known picture over a known backdrop and
/// look, which "present" and "buddy" do. Exit code 0 when the run went as described, 1 on an error, 2 when a
/// scored check did not match, 3 when the screen captured black.</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("TransparencyProbe reads the composed screen through GDI, so it runs on Windows only.");
            return 1;
        }
        // A powered-down panel is not composed, and its capture is black.
        Win32.KeepDisplayOn();
        var outDir = Path.Combine(Environment.CurrentDirectory, "transparency-probe-out");
        Directory.CreateDirectory(outDir);

        var command = args.Length > 0 ? args[0] : "";
        return command switch
        {
            "caps" => WithInstance((instance, api) => Caps.Run(instance, api)),
            "present" => WithInstance((instance, api) => Present.Run(instance, api, args[1..], outDir)),
            "buddy" => Buddy.Run(args.Length > 1 ? args[1] : "app", args.Length > 2 && args[2] == "native", outDir),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("""
            TransparencyProbe caps
                composite-alpha modes and present modes each device offers, per kind of window
            TransparencyProbe present all
                every device x present mode (Fifo, Mailbox) x window kind (SDL transparent, plain), scored on screen
            TransparencyProbe present <driver>:<device substring> [transparent|plain|shape] [fifo|mailbox]
                one combination, e.g. "present Dozen:Adreno", "present Qualcomm:Adreno transparent mailbox"
            TransparencyProbe buddy [app|standalone] [native]
                the library's transparent-window API end to end: halo, shape, clicks; native keeps the window on
                the native device, Dozen or not (a hard-edged companion: the shape alone)
            Pictures go to .\transparency-probe-out. Keep the display on and visible while it runs.
            """);
        return 1;
    }

    // SDL plus an instance made the way the library makes its own: SDL's surface extensions, Vulkan 1.1.
    private static unsafe int WithInstance(Func<VkInstance, VkInstanceApi, int> run)
    {
        if (!Init(InitFlags.Video | InitFlags.Events))
            throw new InvalidOperationException(GetError());
        VulkanLoadLibrary(null);
        vkInitialize().CheckResult();
        var extensions = VulkanGetInstanceExtensions(out _) ?? throw new InvalidOperationException(GetError());
        using var extensionArray = new VkStringArray(extensions);
        VkApplicationInfo app = new() { apiVersion = VkVersion.Version_1_1 };
        VkInstanceCreateInfo instanceCI = new()
        {
            pApplicationInfo = &app,
            enabledExtensionCount = extensionArray.Length,
            ppEnabledExtensionNames = extensionArray,
        };
        vkCreateInstance(&instanceCI, null, out var instance).CheckResult();
        var api = GetApi(instance);
        try
        {
            return run(instance, api);
        }
        finally
        {
            api.vkDestroyInstance();
            Quit();
        }
    }

    /// <summary>Every physical device with its driver: name, driver name (the "driver" in "driver:device"), info.</summary>
    internal static unsafe (VkPhysicalDevice Device, string Name, string Driver, string Info)[] Devices(VkInstanceApi api)
    {
        uint count = 0;
        api.vkEnumeratePhysicalDevices(&count, null);
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = devices)
            api.vkEnumeratePhysicalDevices(&count, p);
        return devices.Select(pd =>
        {
            VkPhysicalDeviceDriverProperties driver = new();
            VkPhysicalDeviceProperties2 props = new() { pNext = &driver };
            api.vkGetPhysicalDeviceProperties2(pd, &props);
            return (pd, Text(props.properties.deviceName), Text(driver.driverName), Text(driver.driverInfo));
        }).ToArray();
    }

    internal static unsafe bool CanPresent(VkInstanceApi api, VkPhysicalDevice device, VkSurfaceKHR surface, out uint family)
    {
        uint count = 0;
        api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
            api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        for (uint i = 0; i < count; i++)
        {
            api.vkGetPhysicalDeviceSurfaceSupportKHR(device, i, surface, out var supported);
            if ((families[i].queueFlags & VkQueueFlags.Graphics) != 0 && supported)
            {
                family = i;
                return true;
            }
        }
        family = 0;
        return false;
    }

    internal static unsafe VkPresentModeKHR[] PresentModes(VkInstanceApi api, VkPhysicalDevice device, VkSurfaceKHR surface)
    {
        uint count = 0;
        api.vkGetPhysicalDeviceSurfacePresentModesKHR(device, surface, &count, null);
        var modes = new VkPresentModeKHR[count];
        fixed (VkPresentModeKHR* p = modes)
            api.vkGetPhysicalDeviceSurfacePresentModesKHR(device, surface, &count, p);
        return modes;
    }

    internal static unsafe string Text(byte* s) => Marshal.PtrToStringUTF8((nint)s) ?? "";
}
