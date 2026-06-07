// Windows backend — compiled only in the net10.0-windows TFM (see csproj Compile Remove).
// COM types come from WebView2Aot (WebView2.dll) + its DirectNAot dependency
// (DirectN.dll / DirectN.Extensions.dll). Usage mirrors smourier's HelloWebView2 sample.
#pragma warning disable CS0067 // WndProcOverride: not surfaced via this backend yet

using System.Reflection;
using System.Text;
using System.Text.Json;
using DirectN;
using DirectN.Extensions;
using DirectN.Extensions.Com;
using DIR.Lib;
using WebView2;
using WebView2.Utilities;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// Windows backend: hosts a WebView2 (Edge/Chromium) control as a child of the SDL window's
/// HWND via the WebView2Aot bindings (Native-AOT-safe). The HWND child captures its own
/// mouse/keyboard input, so no manual SDL→webview forwarding is needed.
/// </summary>
/// <remarks>
/// Environment + controller creation is asynchronous: the completed handlers fire while the
/// thread pumps Win32 messages — which SDL's event loop does — so this composes with SDL.
/// Calls made before the controller is ready (e.g. an early <see cref="Navigate"/>) are queued.
/// WebView2 requires an STA thread.
/// </remarks>
internal sealed class Win32WebView : INativeWebView
{
    private SdlVulkanWindow? _window;
    private ComObject<ICoreWebView2Controller>? _controller;
    private ICoreWebView2? _webView2;

    // Queued state applied once the controller/CoreWebView2 become ready.
    private string? _pendingNavigateUrl;
    private string? _pendingNavigateHtml;
    private RectInt? _bounds;
    private bool _visible = true;

    // Event-handler wrappers must be kept alive for as long as they're subscribed: the COM side
    // holds the only ref to the CCW, and dropping the managed object would break the callback.
    private CoreWebView2NavigationStartingEventHandler? _navStartingHandler;
    private CoreWebView2SourceChangedEventHandler? _sourceChangedHandler;
    private CoreWebView2NavigationCompletedEventHandler? _navCompletedHandler;
    private CoreWebView2DocumentTitleChangedEventHandler? _titleChangedHandler;
    private EventRegistrationToken _navStartingToken;
    private EventRegistrationToken _sourceChangedToken;
    private EventRegistrationToken _navCompletedToken;
    private EventRegistrationToken _titleChangedToken;

    // Diagnostics: renderer-crash handler + the DevTools-Protocol receivers/handlers that feed
    // console/exception/log capture. All kept alive for the lifetime of the subscription.
    private CoreWebView2ProcessFailedEventHandler? _processFailedHandler;
    private EventRegistrationToken _processFailedToken;
    private readonly List<ICoreWebView2DevToolsProtocolEventReceiver> _cdpReceivers = [];
    private readonly List<CoreWebView2DevToolsProtocolEventReceivedEventHandler> _cdpHandlers = [];

    // Two-way page<->host messaging: window.chrome.webview.postMessage <-> PostWebMessageAsJson.
    private CoreWebView2WebMessageReceivedEventHandler? _webMessageHandler;
    private EventRegistrationToken _webMessageToken;

    public event Action<string>? TitleChanged;
    public event Action<string>? NavigationCompleted;
    public event Action<string>? MessageReceived;
    public event Action<nint, uint, nint, nint>? WndProcOverride; // not surfaced via this backend yet
    public event Action<string>? Trace;
    public event Action<string, string>? ConsoleMessage;
    public event Action<string>? PageError;

    public void AttachToWindow(SdlVulkanWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;

        // Locate WebView2Loader.dll (file path or embedded resource) and check the runtime exists.
        WebView2Utilities.Initialize(Assembly.GetEntryAssembly());
        if (WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString() is null)
            throw new InvalidOperationException(
                "The Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen runtime " +
                "from https://developer.microsoft.com/microsoft-edge/webview2/.");

        var hwnd = window.GetNativeWindowHandle();
        if (hwnd == nint.Zero)
            throw new InvalidOperationException("SDL window did not expose a Win32 HWND.");

        // Default the initial bounds to the window's pixel client area if none was set yet.
        if (_bounds is null)
        {
            window.GetSizeInPixels(out var w, out var h);
            _bounds = new RectInt(new PointInt(w, h), new PointInt(0, 0));
        }

        WebView2.Functions.CreateCoreWebView2EnvironmentWithOptions(PWSTR.Null, PWSTR.Null, null!,
            new CoreWebView2CreateCoreWebView2EnvironmentCompletedHandler((envResult, env) =>
            {
                envResult.ThrowOnError();
                env.CreateCoreWebView2Controller(new HWND { Value = hwnd },
                    new CoreWebView2CreateCoreWebView2ControllerCompletedHandler((ctrlResult, controller) =>
                    {
                        ctrlResult.ThrowOnError();
                        _controller = new ComObject<ICoreWebView2Controller>(controller);
                        controller.put_IsVisible(_visible).ThrowOnError();
                        controller.put_Bounds(ToRect(_bounds.Value)).ThrowOnError();
                        controller.get_CoreWebView2(out var webView2).ThrowOnError();
                        _webView2 = webView2;
                        WireTraceEvents(webView2);
                        EnableDiagnostics(webView2);
                        WireMessaging(webView2);
                        ApplyPendingNavigation();
                    }));
            }));
    }

    public void Navigate(string url)
    {
        _pendingNavigateUrl = url;
        _pendingNavigateHtml = null;
        if (_webView2 is not null)
            ApplyPendingNavigation();
    }

    public void NavigateToString(string html)
    {
        _pendingNavigateHtml = html;
        _pendingNavigateUrl = null;
        if (_webView2 is not null)
            ApplyPendingNavigation();
    }

    private void ApplyPendingNavigation()
    {
        if (_webView2 is null)
            return;
        if (_pendingNavigateUrl is { } url)
        {
            _pendingNavigateUrl = null;
            _webView2.Navigate(PWSTR.From(url)).ThrowOnError();
        }
        else if (_pendingNavigateHtml is { } html)
        {
            _pendingNavigateHtml = null;
            _webView2.NavigateToString(PWSTR.From(html)).ThrowOnError();
        }
    }

    // Subscribes navigation/title events purely for diagnostics: each fires on the UI thread as the
    // message pump runs, so we can trace the full redirect chain and surface any load failure.
    private void WireTraceEvents(ICoreWebView2 webView2)
    {
        _navStartingHandler = new CoreWebView2NavigationStartingEventHandler((_, args) =>
        {
            args.get_Uri(out var uri);
            var redirected = default(BOOL);
            args.get_IsRedirected(ref redirected);
            Trace?.Invoke($"nav-starting{((bool)redirected ? " (redirect)" : "")}: {uri}");
        });
        webView2.add_NavigationStarting(_navStartingHandler, ref _navStartingToken).ThrowOnError();

        _sourceChangedHandler = new CoreWebView2SourceChangedEventHandler((sender, _) =>
        {
            sender.get_Source(out var uri);
            Trace?.Invoke($"source-changed: {uri}");
        });
        webView2.add_SourceChanged(_sourceChangedHandler, ref _sourceChangedToken).ThrowOnError();

        _navCompletedHandler = new CoreWebView2NavigationCompletedEventHandler((sender, args) =>
        {
            var success = default(BOOL);
            args.get_IsSuccess(ref success);
            var status = default(COREWEBVIEW2_WEB_ERROR_STATUS);
            args.get_WebErrorStatus(ref status);
            sender.get_Source(out var uri);
            var url = uri.ToString() ?? string.Empty;
            Trace?.Invoke($"nav-completed: success={(bool)success} status={status} url={url}");
            NavigationCompleted?.Invoke(url);
        });
        webView2.add_NavigationCompleted(_navCompletedHandler, ref _navCompletedToken).ThrowOnError();

        _titleChangedHandler = new CoreWebView2DocumentTitleChangedEventHandler((sender, _) =>
        {
            sender.get_DocumentTitle(out var title);
            var text = title.ToString() ?? string.Empty;
            Trace?.Invoke($"title-changed: {text}");
            TitleChanged?.Invoke(text);
        });
        webView2.add_DocumentTitleChanged(_titleChangedHandler, ref _titleChangedToken).ThrowOnError();
    }

    // Captures in-page diagnostics that WebView2 only exposes through the DevTools Protocol:
    // console output, uncaught JS exceptions, and browser log entries (network/CSP failures) —
    // plus renderer-process crashes. Subscribe to the CDP events, then enable the domains.
    private void EnableDiagnostics(ICoreWebView2 webView2)
    {
        _processFailedHandler = new CoreWebView2ProcessFailedEventHandler((_, args) =>
        {
            var kind = default(COREWEBVIEW2_PROCESS_FAILED_KIND);
            args.get_ProcessFailedKind(ref kind);
            Trace?.Invoke($"process-failed: {kind}");
        });
        webView2.add_ProcessFailed(_processFailedHandler, ref _processFailedToken).ThrowOnError();

        SubscribeCdp(webView2, "Runtime.consoleAPICalled", OnConsoleApiCalled);
        SubscribeCdp(webView2, "Runtime.exceptionThrown", OnExceptionThrown);
        SubscribeCdp(webView2, "Log.entryAdded", OnLogEntryAdded);

        CallCdp(webView2, "Runtime.enable");
        CallCdp(webView2, "Log.enable");
    }

    private void SubscribeCdp(ICoreWebView2 webView2, string eventName, Action<string> onJson)
    {
        webView2.GetDevToolsProtocolEventReceiver(PWSTR.From(eventName), out var receiver).ThrowOnError();
        var handler = new CoreWebView2DevToolsProtocolEventReceivedEventHandler((_, args) =>
        {
            args.get_ParameterObjectAsJson(out var json);
            onJson(json.ToString() ?? string.Empty);
        });
        var token = default(EventRegistrationToken);
        receiver.add_DevToolsProtocolEventReceived(handler, ref token).ThrowOnError();
        _cdpReceivers.Add(receiver);
        _cdpHandlers.Add(handler);
    }

    private static void CallCdp(ICoreWebView2 webView2, string method)
        => webView2.CallDevToolsProtocolMethod(PWSTR.From(method), PWSTR.From("{}"),
            new CoreWebView2CallDevToolsProtocolMethodCompletedHandler((_, _) => { })).ThrowOnError();

    // Runtime.consoleAPICalled → { type, args: [RemoteObject, ...] }
    private void OnConsoleApiCalled(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var level = root.TryGetProperty("type", out var t) ? t.GetString() ?? "log" : "log";
            var sb = new StringBuilder();
            if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in args.EnumerateArray())
                {
                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(DescribeRemoteObject(arg));
                }
            }
            ConsoleMessage?.Invoke(level, sb.ToString());
        }
        catch (JsonException) { /* ignore malformed CDP payloads */ }
    }

    // Runtime.exceptionThrown → { exceptionDetails: { text, url, lineNumber, exception:{ description } } }
    private void OnExceptionThrown(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("exceptionDetails", out var details))
                return;
            var text = string.Empty;
            if (details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var desc))
                text = desc.GetString() ?? string.Empty;
            if (text.Length == 0 && details.TryGetProperty("text", out var t))
                text = t.GetString() ?? string.Empty;
            if (details.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url)
            {
                var line = details.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l : 0;
                text = $"{text}  @ {url}:{line}";
            }
            PageError?.Invoke(text);
        }
        catch (JsonException) { /* ignore malformed CDP payloads */ }
    }

    // Log.entryAdded → { entry: { source, level, text, url } } — network/CSP/security failures.
    private void OnLogEntryAdded(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entry", out var entry))
                return;
            var source = entry.TryGetProperty("source", out var s) ? s.GetString() : null;
            var level = entry.TryGetProperty("level", out var l) ? l.GetString() : null;
            var text = entry.TryGetProperty("text", out var x) ? x.GetString() : null;
            var url = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
            var loc = string.IsNullOrEmpty(url) ? string.Empty : $" ({url})";
            Trace?.Invoke($"log[{source}/{level}]: {text}{loc}");
        }
        catch (JsonException) { /* ignore malformed CDP payloads */ }
    }

    // Renders a CDP RemoteObject as a short string: prefer its value, then description/type.
    private static string DescribeRemoteObject(JsonElement arg)
    {
        if (arg.TryGetProperty("value", out var v))
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText();
        if (arg.TryGetProperty("description", out var d))
            return d.GetString() ?? string.Empty;
        if (arg.TryGetProperty("unserializableValue", out var u))
            return u.GetString() ?? string.Empty;
        if (arg.TryGetProperty("type", out var t))
            return t.GetString() ?? string.Empty;
        return string.Empty;
    }

    public void SetBounds(RectInt bounds)
    {
        _bounds = bounds;
        _controller?.Object.put_Bounds(ToRect(bounds)).ThrowOnError();
    }

    public void Focus()
        => _controller?.Object
            .MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC)
            .ThrowOnError();

    public void SetVisible(bool visible)
    {
        _visible = visible;
        _controller?.Object.put_IsVisible(visible).ThrowOnError();
    }

    public Task<string> ExecuteScriptAsync(string javaScript)
    {
        var webView2 = _webView2
            ?? throw new InvalidOperationException(
                "WebView2 is not ready yet. Call AttachToWindow and wait for the first navigation.");

        // The completed handler fires on the UI thread; RunContinuationsAsynchronously keeps an
        // awaiting caller from re-entering the message pump on the same stack.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        webView2.ExecuteScript(PWSTR.From(javaScript),
            new CoreWebView2ExecuteScriptCompletedHandler((errorCode, result) =>
            {
                if (errorCode.IsError)
                    tcs.TrySetException(
                        new InvalidOperationException($"ExecuteScript failed (HRESULT 0x{errorCode.Value:X8})."));
                else
                    // Result is a JSON-encoded value (a quoted string, a number, or "null").
                    tcs.TrySetResult(result.ToString() ?? "null");
            })).ThrowOnError();
        return tcs.Task;
    }

    public void PostMessage(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var webView2 = _webView2
            ?? throw new InvalidOperationException(
                "WebView2 is not ready yet. Call AttachToWindow and wait for the first navigation.");
        // Delivered to the page on window.chrome.webview's 'message' event as event.data.
        webView2.PostWebMessageAsJson(PWSTR.From(json)).ThrowOnError();
    }

    // Surfaces the page's window.chrome.webview.postMessage(...) calls as MessageReceived (raw JSON).
    private void WireMessaging(ICoreWebView2 webView2)
    {
        _webMessageHandler = new CoreWebView2WebMessageReceivedEventHandler((_, args) =>
        {
            args.get_WebMessageAsJson(out var json);
            MessageReceived?.Invoke(json.ToString() ?? string.Empty);
        });
        webView2.add_WebMessageReceived(_webMessageHandler, ref _webMessageToken).ThrowOnError();
    }

    private static RECT ToRect(in RectInt b) => new()
    {
        left = b.UpperLeft.X,
        top = b.UpperLeft.Y,
        right = b.LowerRight.X,
        bottom = b.LowerRight.Y,
    };

    public void Dispose()
    {
        // Drop the handler refs so they're collectable once the controller releases its COM refs.
        _navStartingHandler = null;
        _sourceChangedHandler = null;
        _navCompletedHandler = null;
        _titleChangedHandler = null;
        _processFailedHandler = null;
        _webMessageHandler = null;
        _cdpHandlers.Clear();
        _cdpReceivers.Clear();
        _webView2 = null;
        _controller?.Dispose();
        _controller = null;
    }
}
