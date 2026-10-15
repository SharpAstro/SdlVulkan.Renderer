using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static SDL3.SDL;

namespace TransparencyProbe;

/// <summary>
/// "caps": which composite-alpha modes, and which present modes, each device's surface offers, per kind of window.
/// What Vulkan PROMISES; "present" measures what the screen then shows.
/// </summary>
internal static unsafe class Caps
{
    public static int Run(VkInstance instance, VkInstanceApi api)
    {
        var devices = Program.Devices(api);
        foreach (var d in devices)
            Console.WriteLine($"device: {d.Name} | {d.Driver} | {d.Info}");
        Console.WriteLine();

        foreach (var (label, flags) in new (string, WindowFlags)[]
                 {
                     ("SDL plain", WindowFlags.Vulkan),
                     ("SDL borderless", WindowFlags.Vulkan | WindowFlags.Borderless),
                     ("SDL transparent", WindowFlags.Vulkan | WindowFlags.Transparent),
                     ("SDL transparent+borderless+on top",
                         WindowFlags.Vulkan | WindowFlags.Transparent | WindowFlags.Borderless | WindowFlags.AlwaysOnTop),
                 })
        {
            var window = CreateWindow(label, 320, 240, flags);
            if (window == 0)
                throw new InvalidOperationException(GetError());
            PumpEvents();
            var hwnd = GetPointerProperty(GetWindowProperties(window), Props.WindowWin32HWNDPointer, 0);
            if (!VulkanCreateSurface(window, instance.Handle, 0, out var surfaceHandle))
                throw new InvalidOperationException(GetError());
            Report(api, devices, $"{label} (exstyle 0x{Win32.GetWindowLongPtrW(hwnd, Win32.GwlExStyle):x})",
                new VkSurfaceKHR((ulong)surfaceHandle));
            DestroyWindow(window);
        }

        // Raw Win32 windows with the extended styles a transparent window would be made with outside SDL: layered
        // (per-pixel alpha through UpdateLayeredWindow) and no redirection bitmap (a DirectComposition window's).
        var module = Win32.GetModuleHandleW(null);
        fixed (char* className = "TransparencyProbe")
        {
            Win32.WndClassW wc = new()
            {
                lpfnWndProc = Win32.GetProcAddress(Win32.GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                hInstance = module,
                lpszClassName = className,
            };
            if (Win32.RegisterClassW(&wc) == 0)
                throw new InvalidOperationException($"RegisterClassW: {Marshal.GetLastPInvokeError()}");
            foreach (var (label, exStyle) in new (string, uint)[]
                     {
                         ("Win32 WS_EX_LAYERED", Win32.WsExLayered),
                         ("Win32 WS_EX_NOREDIRECTIONBITMAP", Win32.WsExNoRedirectionBitmap),
                         ("Win32 WS_EX_NOREDIRECTIONBITMAP|WS_EX_TOPMOST", Win32.WsExNoRedirectionBitmap | Win32.WsExTopmost),
                     })
            {
                var hwnd = Win32.CreateWindowExW(exStyle, className, className, Win32.WsPopup | Win32.WsVisible,
                    100, 100, 320, 240, 0, 0, module, 0);
                if (hwnd == 0)
                    throw new InvalidOperationException($"CreateWindowExW: {Marshal.GetLastPInvokeError()}");
                if ((exStyle & Win32.WsExLayered) != 0)
                    Win32.SetLayeredWindowAttributes(hwnd, 0, 255, 2 /* LWA_ALPHA */);
                VkWin32SurfaceCreateInfoKHR surfaceCI = new() { hinstance = module, hwnd = hwnd };
                api.vkCreateWin32SurfaceKHR(&surfaceCI, null, out var surface).CheckResult();
                Report(api, devices, $"{label} (exstyle 0x{Win32.GetWindowLongPtrW(hwnd, Win32.GwlExStyle):x})", surface);
                Win32.DestroyWindow(hwnd);
            }
        }
        return 0;
    }

    private static void Report(VkInstanceApi api, (VkPhysicalDevice Device, string Name, string Driver, string Info)[] devices,
        string label, VkSurfaceKHR surface)
    {
        Console.WriteLine(label);
        foreach (var d in devices)
        {
            string line;
            if (!Program.CanPresent(api, d.Device, surface, out _))
                line = "cannot present here";
            else
            {
                var result = api.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(d.Device, surface, out var caps);
                line = result == VkResult.Success
                    ? $"{caps.supportedCompositeAlpha} | present {string.Join(", ", Program.PresentModes(api, d.Device, surface))}"
                    : result.ToString();
            }
            Console.WriteLine($"  {$"{d.Name} [{d.Driver}]",-70} {line}");
        }
        api.vkDestroySurfaceKHR(surface);
    }
}
