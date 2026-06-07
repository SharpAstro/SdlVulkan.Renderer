using static SDL3.SDL;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// Extracts the native display-server handles a webview backend needs from an
/// <see cref="SdlVulkanWindow"/>. Windows/macOS need only the single handle from
/// <see cref="SdlVulkanWindow.GetNativeWindowHandle"/>; Linux needs both a display
/// pointer and a surface/window, plus to know whether the session is Wayland or X11.
/// </summary>
internal static class NativeWebViewHandle
{
    public enum LinuxDriver { None, Wayland, X11 }

    /// <summary>
    /// Resolves the Linux display-server handles for <paramref name="window"/>. Prefers
    /// Wayland (the backend's Wayland-first policy), falling back to X11. The X11 window is
    /// a NUMBER property (the XID), not a pointer — read accordingly.
    /// </summary>
    public static LinuxDriver GetLinuxHandles(SdlVulkanWindow window,
        out nint display, out nint surfaceOrWindow)
    {
        var props = GetWindowProperties(window.Handle);

        var wlSurface = GetPointerProperty(props, Props.WindowWaylandSurfacePointer, nint.Zero);
        if (wlSurface != nint.Zero)
        {
            display = GetPointerProperty(props, Props.WindowWaylandDisplayPointer, nint.Zero);
            surfaceOrWindow = wlSurface;
            return LinuxDriver.Wayland;
        }

        var x11Window = (nint)GetNumberProperty(props, Props.WindowX11WindowNumber, 0);
        if (x11Window != nint.Zero)
        {
            display = GetPointerProperty(props, Props.WindowX11DisplayPointer, nint.Zero);
            surfaceOrWindow = x11Window;
            return LinuxDriver.X11;
        }

        display = nint.Zero;
        surfaceOrWindow = nint.Zero;
        return LinuxDriver.None;
    }
}
