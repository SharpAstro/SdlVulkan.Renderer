#pragma warning disable CS0067 // events are raised once the WKWebView wiring lands (Phase 2)

using DIR.Lib;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// macOS backend: hosts a <c>WKWebView</c> as a subview of the SDL window's content view, via
/// <c>System.Runtime.InteropServices.ObjectiveC</c> interop. The NSView hierarchy routes input
/// to the WKWebView natively — no manual forwarding needed.
/// </summary>
internal sealed class CocoaWebView : INativeWebView
{
    private SdlVulkanWindow? _window;

    public event Action<string>? TitleChanged;
    public event Action<string>? NavigationCompleted;
    public event Action<string>? MessageReceived;
    public event Action<nint, uint, nint, nint>? WndProcOverride; // always null on macOS
    public event Action<string>? Trace;
    public event Action<string, string>? ConsoleMessage;
    public event Action<string>? PageError;

    public void AttachToWindow(SdlVulkanWindow window)
    {
        _window = window;
        var nsWindow = window.GetNativeWindowHandle();
        // TODO(Phase 2): WKWebViewConfiguration + WKWebView via ObjectiveC interop;
        //   [nsWindow.contentView addSubview:webView].
        _ = nsWindow;
        throw new NotImplementedException("CocoaWebView.AttachToWindow — Phase 2.");
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
        // TODO(Phase 2): release the WKWebView / remove from superview.
    }
}
