using System.Runtime.InteropServices;
using DIR.Lib;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// A platform-abstracted browser view hosted as a native child surface on top of an
/// <see cref="SdlVulkanWindow"/>'s Vulkan swapchain. The window-system compositor stacks
/// the webview above the Vulkan surface — it does not interact with the Vulkan render pass.
/// </summary>
/// <remarks>
/// Backends: <c>Win32WebView</c> (WebView2 via WebView2Aot, Windows), <c>GtkWebView</c>
/// (WebKitGTK, Linux), <c>CocoaWebView</c> (WKWebView, macOS). Create one via
/// <see cref="NativeWebView.Create"/>.
/// </remarks>
public interface INativeWebView : IDisposable
{
    /// <summary>Parents the webview into <paramref name="window"/>'s native window/view.
    /// Must be called on the main (event-loop) thread, after the window exists.</summary>
    void AttachToWindow(SdlVulkanWindow window);

    /// <summary>Navigates to <paramref name="url"/>.</summary>
    void Navigate(string url);

    /// <summary>Loads <paramref name="html"/> directly as the document content.</summary>
    void NavigateToString(string html);

    /// <summary>Positions/sizes the webview child surface, in window pixel coordinates
    /// (the same space the renderer draws in). Forward <c>SdlWindowView.OnResize</c> here.</summary>
    void SetBounds(RectInt bounds);

    /// <summary>Gives the webview input focus.</summary>
    void Focus();

    /// <summary>Shows or hides the webview child surface.</summary>
    void SetVisible(bool visible);

    /// <summary>Evaluates <paramref name="javaScript"/> in the page and returns its JSON result.</summary>
    Task<string> ExecuteScriptAsync(string javaScript);

    /// <summary>Posts <paramref name="json"/> to the page (.NET → JS). The page receives it on
    /// <c>window.chrome.webview</c>'s <c>message</c> event as <c>event.data</c>. Must be valid JSON.
    /// Pairs with <see cref="MessageReceived"/> for a two-way host↔page channel.</summary>
    void PostMessage(string json);

    /// <summary>Raised when the document title changes.</summary>
    event Action<string>? TitleChanged;

    /// <summary>Raised when a navigation completes; carries the final URL.</summary>
    event Action<string>? NavigationCompleted;

    /// <summary>Raised when the page posts a message to the host via
    /// <c>window.chrome.webview.postMessage(...)</c> (JS → .NET). Carries the message as raw JSON;
    /// the host parses whatever protocol it defines on top.</summary>
    event Action<string>? MessageReceived;

    /// <summary>Windows/HWND only: lets the host observe window messages the webview child
    /// receives. Args mirror a WndProc: (hwnd, msg, wParam, lParam). Null on other platforms.</summary>
    event Action<nint, uint, nint, nint>? WndProcOverride;

    /// <summary>Low-level diagnostic trace of navigation activity (nav-starting, source-changed,
    /// nav-completed with success/error status, title changes) plus browser log entries
    /// (network/CSP failures). Intended for logging the redirect chain and diagnosing
    /// blank/failed loads — not a stable API.</summary>
    event Action<string>? Trace;

    /// <summary>Raised for each <c>console.*</c> call in the page. Args: (level, text), where
    /// level is <c>log</c>/<c>info</c>/<c>warning</c>/<c>error</c>/<c>debug</c>.</summary>
    event Action<string, string>? ConsoleMessage;

    /// <summary>Raised on an uncaught JavaScript exception in the page; carries the error
    /// description and stack.</summary>
    event Action<string>? PageError;
}

/// <summary>
/// Factory that creates the appropriate <see cref="INativeWebView"/> backend for the
/// running platform. Isolates the single point of platform dispatch.
/// </summary>
public static class NativeWebView
{
    /// <summary>Creates the platform's native webview backend.</summary>
    /// <exception cref="PlatformNotSupportedException">No backend exists for this platform,
    /// or this is the base (non-Windows) build running on Windows.</exception>
    public static INativeWebView Create()
    {
        if (OperatingSystem.IsWindows())
        {
#if WINDOWS
            return new Win32WebView();
#else
            throw new PlatformNotSupportedException(
                "Win32WebView is only present in the net10.0-windows build of " +
                "SdlVulkan.Renderer.WebView. Reference the package from a Windows-targeting app.");
#endif
        }

        if (OperatingSystem.IsMacOS())
            return new CocoaWebView();

        if (OperatingSystem.IsLinux())
            return new GtkWebView();

        throw new PlatformNotSupportedException(
            $"No INativeWebView backend for this platform ({RuntimeInformation.OSDescription}).");
    }
}
