#pragma warning disable CS0067 // events are raised once the WebKitGTK wiring lands (Phase 3)

using DIR.Lib;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// Linux backend: hosts a WebKitGTK <c>WebKitWebView</c> inside the SDL window. The driver is
/// detected at runtime (SDL is left on its native backend, not forced to X11): Wayland uses a
/// <c>wl_subsurface</c> of SDL's <c>wl_surface</c> (preferred); X11 reparents the GTK widget
/// into SDL's X11 window. <c>gtk_init()</c> must run on the main thread before use.
/// </summary>
internal sealed class GtkWebView : INativeWebView
{
    private SdlVulkanWindow? _window;

    public event Action<string>? TitleChanged;
    public event Action<string>? NavigationCompleted;
    public event Action<string>? MessageReceived;
    public event Action<nint, uint, nint, nint>? WndProcOverride; // always null on Linux
    public event Action<string>? Trace;
    public event Action<string, string>? ConsoleMessage;
    public event Action<string>? PageError;

    public void AttachToWindow(SdlVulkanWindow window)
    {
        _window = window;
        var driver = NativeWebViewHandle.GetLinuxHandles(window, out var display, out var surfaceOrWindow);
        _ = display;
        _ = surfaceOrWindow;
        switch (driver)
        {
            case NativeWebViewHandle.LinuxDriver.Wayland:
                // TODO(Phase 3): create WebKitWebView, render it into a wl_subsurface of
                //   `surfaceOrWindow`, sharing SDL's wl_display `display`.
                throw new NotImplementedException("GtkWebView Wayland (wl_subsurface) backend — Phase 3.");
            case NativeWebViewHandle.LinuxDriver.X11:
                // TODO(Phase 3): realize WebKitWebView, XReparentWindow its X11 window into
                //   `surfaceOrWindow` on display `display`.
                throw new NotImplementedException("GtkWebView X11 (XReparentWindow) backend — Phase 3.");
            default:
                throw new PlatformNotSupportedException(
                    "SDL window exposes neither a Wayland surface nor an X11 window.");
        }
    }

    public void Navigate(string url) => throw new NotImplementedException();
    public void NavigateToString(string html) => throw new NotImplementedException();
    public void SetBounds(RectInt bounds) => throw new NotImplementedException();
    public void Focus() => throw new NotImplementedException();
    public void SetVisible(bool visible) => throw new NotImplementedException();
    public Task<string> ExecuteScriptAsync(string javaScript) => throw new NotImplementedException();
    public void PostMessage(string json) => throw new NotImplementedException();

    public void Dispose()
    {
        // TODO(Phase 3): destroy the WebKitWebView / GTK widget.
    }
}
