#pragma warning disable CS0067 // WndProcOverride is HWND-only; never raised on the Linux backend.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using DIR.Lib;
using static SdlVulkan.Renderer.WebView.GtkInterop;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// Linux backend: hosts a WebKitGTK <c>WebKitWebView</c> inside the SDL window. GTK runs its own
/// main loop on a dedicated thread; the WebKit GdkWindow is reparented into SDL's X11 window via
/// <c>XReparentWindow</c> (the X11-first embedding). The SDL window must therefore be on the X11
/// driver — run with <c>SDL_VIDEODRIVER=x11</c> (this works under Wayland sessions via XWayland).
/// </summary>
/// <remarks>
/// <para>Cross-thread model: all GTK/WebKit calls are marshaled onto the GTK thread with
/// <c>g_idle_add</c>; public methods are safe to call from the SDL event-loop thread. Calls made
/// before the view is ready (an early <see cref="Navigate"/>) are queued, mirroring the Win32 backend.</para>
/// <para>Events (<see cref="MessageReceived"/>, <see cref="TitleChanged"/>, …) are raised on the GTK
/// thread, not the SDL main thread — handlers must be thread-safe or marshal back themselves.</para>
/// <para>A small <c>window.chrome.webview</c> shim is injected at document-start so page code uses the
/// same JS API as the Windows (WebView2) backend; <c>console.*</c> and uncaught errors are forwarded
/// to <see cref="ConsoleMessage"/>/<see cref="PageError"/> via dedicated script-message handlers.</para>
/// <para>Process exit: WebKitGTK registers process-global teardown (an <c>atexit</c> handler that
/// unrefs its singletons) that calls <c>abort()</c> — a known WebKit trait, independent of
/// <see cref="Dispose"/>, which runs only after <c>main</c> has returned. It is harmless (all work is
/// done) but turns a clean run into exit code 134. An app that wants a deterministic exit code should
/// fast-exit past the C runtime's <c>atexit</c> handlers (e.g. <c>libc</c> <c>_exit</c>) rather than
/// relying on normal process teardown.</para>
/// </remarks>
internal sealed class GtkWebView : INativeWebView
{
    private const int GtkWindowToplevel = 0;
    private const int WebkitLoadStarted = 0, WebkitLoadRedirected = 1, WebkitLoadCommitted = 2, WebkitLoadFinished = 3;
    private const int WebkitInjectAllFrames = 0, WebkitInjectAtDocumentStart = 0;
    private const int InitTimeoutMs = 15000;

    // Injected at document-start so the page's JS API matches the Windows backend: page->host via
    // window.chrome.webview.postMessage(obj) (JSON-stringified onto the WebKit "host" handler), and
    // host->page delivered to chrome.webview's 'message' listeners through __dispatch(json).
    private const string ChromeWebViewShim = """
        (function () {
          if (window.chrome && window.chrome.webview && window.chrome.webview.__sdl) return;
          var listeners = [];
          var webview = {
            __sdl: true,
            postMessage: function (msg) { window.webkit.messageHandlers.host.postMessage(JSON.stringify(msg)); },
            addEventListener: function (type, fn) { if (type === 'message' && typeof fn === 'function') listeners.push(fn); },
            removeEventListener: function (type, fn) { if (type === 'message') { var i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); } },
            __dispatch: function (json) {
              var data; try { data = JSON.parse(json); } catch (e) { data = json; }
              var ev = { data: data };
              for (var i = 0; i < listeners.length; i++) { try { listeners[i](ev); } catch (e) { if (window.console) console.error(e); } }
            }
          };
          window.chrome = window.chrome || {};
          window.chrome.webview = webview;
        })();
        """;

    // Forwards console output + uncaught errors to the host (WebView2 surfaces these via the DevTools
    // Protocol; WebKitGTK has no equivalent stable signal, so we hook them in-page instead).
    private const string DiagnosticsScript = """
        (function () {
          function post(h, payload) { try { window.webkit.messageHandlers[h].postMessage(payload); } catch (e) {} }
          ['log', 'info', 'warn', 'error', 'debug'].forEach(function (level) {
            var orig = (window.console && console[level]) ? console[level].bind(console) : function () {};
            console[level] = function () {
              try {
                var parts = Array.prototype.map.call(arguments, function (a) {
                  try { return (typeof a === 'string') ? a : JSON.stringify(a); } catch (e) { return String(a); }
                });
                post('hostConsole', JSON.stringify({ level: (level === 'warn' ? 'warning' : level), text: parts.join(' ') }));
              } catch (e) {}
              return orig.apply(console, arguments);
            };
          });
          window.addEventListener('error', function (e) {
            var msg = e.message || (e.error && e.error.message) || 'Script error';
            var where = (e.error && e.error.stack) ? ('\n' + e.error.stack) : (' @ ' + (e.filename || '') + ':' + (e.lineno || 0));
            post('hostError', msg + where);
          });
          window.addEventListener('unhandledrejection', function (e) {
            var r = e.reason; post('hostError', 'Unhandled promise rejection: ' + ((r && r.stack) ? r.stack : String(r)));
          });
        })();
        """;

    // Native callback thunks (shared by all instances; the instance is recovered from user_data).
    private static readonly unsafe nint HostMessagePtr = (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnHostMessage;
    private static readonly unsafe nint ConsoleMessagePtr = (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnConsoleMessageRaised;
    private static readonly unsafe nint PageErrorPtr = (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnPageErrorRaised;
    private static readonly unsafe nint NotifyTitlePtr = (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnNotifyTitle;
    private static readonly unsafe nint LoadChangedPtr = (nint)(delegate* unmanaged<nint, int, nint, void>)&OnLoadChanged;
    private static readonly unsafe nint LoadFailedPtr = (nint)(delegate* unmanaged<nint, int, nint, nint, nint, int>)&OnLoadFailed;
    private static readonly unsafe nint DrainIdlePtr = (nint)(delegate* unmanaged<nint, int>)&DrainIdle;
    private static readonly unsafe nint EvalFinishedPtr = (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnEvalFinished;

    private SdlVulkanWindow? _window;
    private ulong _hostXid;
    private nint _x11Display;
    private nint _webView, _ucm, _gtkWindow;
    private ulong _gtkXid;
    private Thread? _gtkThread;
    private GCHandle _self;
    private nint _selfPtr;
    private readonly ManualResetEventSlim _initEvent = new(false);
    private readonly ConcurrentQueue<Action> _gtkQueue = new();
    private Exception? _initError;
    private volatile bool _ready;
    private bool _disposed;

    // Queued/last-known state, applied once the view is ready.
    private RectInt? _bounds;
    private string? _pendingUrl, _pendingHtml;

    public event Action<string>? TitleChanged;
    public event Action<string>? NavigationCompleted;
    public event Action<string>? MessageReceived;
    public event Action<nint, uint, nint, nint>? WndProcOverride; // always null on Linux
    public event Action<string>? Trace;
    public event Action<string, string>? ConsoleMessage;
    public event Action<string>? PageError;

    public void AttachToWindow(SdlVulkanWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;

        var driver = NativeWebViewHandle.GetLinuxHandles(window, out _, out var surfaceOrWindow);
        if (driver != NativeWebViewHandle.LinuxDriver.X11)
            throw new PlatformNotSupportedException(driver == NativeWebViewHandle.LinuxDriver.Wayland
                ? "GtkWebView embeds via X11 (XReparentWindow), but the SDL window is on the Wayland driver. " +
                  "Run with SDL_VIDEODRIVER=x11 (works under Wayland via XWayland)."
                : "SDL window exposes neither an X11 window nor a Wayland surface.");
        _hostXid = (ulong)surfaceOrWindow;

        if (_bounds is null)
        {
            window.GetSizeInPixels(out var w, out var h);
            _bounds = new RectInt(new PointInt(w, h), new PointInt(0, 0)); // (LowerRight, UpperLeft): origin (0,0), size w×h
        }

        _self = GCHandle.Alloc(this);
        _selfPtr = GCHandle.ToIntPtr(_self);
        _gtkThread = new Thread(GtkThreadMain) { IsBackground = true, Name = "GtkWebView" };
        _gtkThread.Start();

        if (!_initEvent.Wait(InitTimeoutMs))
            throw new TimeoutException($"WebKitGTK did not initialize within {InitTimeoutMs} ms.");
        if (_initError is not null)
            throw new InvalidOperationException("WebKitGTK initialization failed.", _initError);

        ApplyPendingNavigation();
    }

    // Runs on the dedicated GTK thread: init GTK on the X11 backend, build the view + bridge, reparent
    // into SDL's window, then enter gtk_main() (which blocks here until Dispose calls gtk_main_quit).
    private void GtkThreadMain()
    {
        try
        {
            gdk_set_allowed_backends("x11");
            if (gtk_init_check(nint.Zero, nint.Zero) == 0)
                throw new InvalidOperationException(
                    "gtk_init_check failed — no X display. Ensure DISPLAY is set (X11 / XWayland).");

            _x11Display = XOpenDisplay(nint.Zero);
            if (_x11Display == nint.Zero)
                throw new InvalidOperationException("XOpenDisplay failed.");

            var web = webkit_web_view_new();
            var ucm = webkit_web_view_get_user_content_manager(web);
            webkit_user_content_manager_register_script_message_handler(ucm, "host");
            webkit_user_content_manager_register_script_message_handler(ucm, "hostConsole");
            webkit_user_content_manager_register_script_message_handler(ucm, "hostError");
            g_signal_connect_data(ucm, "script-message-received::host", HostMessagePtr, _selfPtr, nint.Zero, 0);
            g_signal_connect_data(ucm, "script-message-received::hostConsole", ConsoleMessagePtr, _selfPtr, nint.Zero, 0);
            g_signal_connect_data(ucm, "script-message-received::hostError", PageErrorPtr, _selfPtr, nint.Zero, 0);
            g_signal_connect_data(web, "notify::title", NotifyTitlePtr, _selfPtr, nint.Zero, 0);
            g_signal_connect_data(web, "load-changed", LoadChangedPtr, _selfPtr, nint.Zero, 0);
            g_signal_connect_data(web, "load-failed", LoadFailedPtr, _selfPtr, nint.Zero, 0);
            AddUserScript(ucm, ChromeWebViewShim);
            AddUserScript(ucm, DiagnosticsScript);

            var (x, y, w, h) = ToXywh(_bounds!.Value);
            var gtkWin = gtk_window_new(GtkWindowToplevel);
            gtk_window_set_default_size(gtkWin, (int)w, (int)h);
            gtk_container_add(gtkWin, web);
            gtk_widget_realize(gtkWin);
            var gdkWin = gtk_widget_get_window(gtkWin);
            if (gdkWin == nint.Zero)
                throw new InvalidOperationException("GTK produced no GdkWindow (not on the X11 backend).");
            _gtkXid = gdk_x11_window_get_xid(gdkWin);

            XReparentWindow(_x11Display, _gtkXid, _hostXid, x, y);
            XMoveResizeWindow(_x11Display, _gtkXid, x, y, w, h);
            XMapWindow(_x11Display, _gtkXid);
            XSync(_x11Display, 0);
            gtk_widget_show_all(gtkWin);

            _webView = web;
            _ucm = ucm;
            _gtkWindow = gtkWin;
            _ready = true;
            _initEvent.Set();

            gtk_main();
        }
        catch (Exception ex)
        {
            _initError = ex;
            _initEvent.Set();
        }
    }

    private static void AddUserScript(nint ucm, string source)
    {
        var script = webkit_user_script_new(source, WebkitInjectAllFrames, WebkitInjectAtDocumentStart, nint.Zero, nint.Zero);
        webkit_user_content_manager_add_script(ucm, script);
        webkit_user_script_unref(script);
    }

    public void Navigate(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _pendingUrl = url;
        _pendingHtml = null;
        if (_ready)
            ApplyPendingNavigation();
    }

    public void NavigateToString(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        _pendingHtml = html;
        _pendingUrl = null;
        if (_ready)
            ApplyPendingNavigation();
    }

    private void ApplyPendingNavigation()
    {
        if (!_ready)
            return;
        if (_pendingUrl is { } url)
        {
            _pendingUrl = null;
            RunOnGtk(() => webkit_web_view_load_uri(_webView, url));
        }
        else if (_pendingHtml is { } html)
        {
            _pendingHtml = null;
            RunOnGtk(() => webkit_web_view_load_html(_webView, html, null));
        }
    }

    public void SetBounds(RectInt bounds)
    {
        _bounds = bounds;
        if (!_ready)
            return;
        var (x, y, w, h) = ToXywh(bounds);
        RunOnGtk(() =>
        {
            XMoveResizeWindow(_x11Display, _gtkXid, x, y, w, h);
            XSync(_x11Display, 0);
        });
    }

    public void Focus()
    {
        if (_ready)
            RunOnGtk(() => gtk_widget_grab_focus(_webView));
    }

    public void SetVisible(bool visible)
    {
        if (!_ready)
            return;
        RunOnGtk(() =>
        {
            if (visible)
                gtk_widget_show_all(_gtkWindow);
            else
                gtk_widget_hide(_gtkWindow);
        });
    }

    public Task<string> ExecuteScriptAsync(string javaScript)
    {
        ArgumentNullException.ThrowIfNull(javaScript);
        EnsureReady();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsHandle = GCHandle.Alloc(tcs);
        RunOnGtk(() =>
        {
            try
            {
                webkit_web_view_evaluate_javascript(_webView, javaScript, -1, null, null,
                    nint.Zero, EvalFinishedPtr, GCHandle.ToIntPtr(tcsHandle));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                tcsHandle.Free();
            }
        });
        return tcs.Task;
    }

    public void PostMessage(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        EnsureReady();
        // Wrap the JSON as a JS string literal and hand it to chrome.webview's listeners, mirroring
        // WebView2's PostWebMessageAsJson (page receives it on the 'message' event as event.data).
        var literal = $"\"{JsonEncodedText.Encode(json)}\"";
        var js = $"if(window.chrome&&window.chrome.webview)window.chrome.webview.__dispatch({literal});";
        RunOnGtk(() => webkit_web_view_evaluate_javascript(_webView, js, -1, null, null,
            nint.Zero, nint.Zero, nint.Zero));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ready = false;

        if (_gtkThread is { IsAlive: true })
        {
            RunOnGtk(() =>
            {
                if (_gtkWindow != nint.Zero)
                    gtk_widget_destroy(_gtkWindow);
                gtk_main_quit();
            });
            _gtkThread.Join(2000);
        }

        if (_x11Display != nint.Zero)
        {
            XCloseDisplay(_x11Display);
            _x11Display = nint.Zero;
        }
        if (_self.IsAllocated)
            _self.Free();
        _initEvent.Dispose();
    }

    // Marshals work onto the GTK thread. If already there (e.g. raised from a WebKit callback), run
    // inline; otherwise enqueue and wake the loop via g_idle_add (thread-safe in GLib).
    private void RunOnGtk(Action action)
    {
        if (Thread.CurrentThread == _gtkThread)
        {
            action();
            return;
        }
        _gtkQueue.Enqueue(action);
        g_idle_add(DrainIdlePtr, _selfPtr);
    }

    private void EnsureReady()
    {
        if (!_ready || _webView == nint.Zero)
            throw new InvalidOperationException("GtkWebView is not attached yet — call AttachToWindow first.");
    }

    private static (int x, int y, uint w, uint h) ToXywh(in RectInt b)
        => (Math.Min(b.UpperLeft.X, b.LowerRight.X), Math.Min(b.UpperLeft.Y, b.LowerRight.Y),
            (uint)b.Width, (uint)b.Height);

    private static string? ReadJsResultString(nint jsResult)
    {
        var value = webkit_javascript_result_get_js_value(jsResult);
        if (value == nint.Zero)
            return null;
        var p = jsc_value_to_string(value);
        if (p == nint.Zero)
            return null;
        var s = Marshal.PtrToStringUTF8(p);
        g_free(p);
        return s;
    }

    // GError layout on LP64: { guint32 domain; gint code; gchar *message; } — message ptr at offset 8.
    private static string ReadGErrorMessage(nint error)
    {
        if (error == nint.Zero)
            return "(unknown error)";
        var msgPtr = Marshal.ReadIntPtr(error, 8);
        return Marshal.PtrToStringUTF8(msgPtr) ?? "(unknown error)";
    }

    private static GtkWebView? FromUserData(nint userData)
        => userData == nint.Zero ? null : GCHandle.FromIntPtr(userData).Target as GtkWebView;

    // ---- Native callbacks (run on the GTK thread; must never let an exception escape) -------------

    [UnmanagedCallersOnly]
    private static void OnHostMessage(nint ucm, nint jsResult, nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            if (self?.MessageReceived is { } handler && ReadJsResultString(jsResult) is { } json)
                handler(json);
        }
        catch { /* swallow: an exception crossing into native code would crash the process */ }
    }

    [UnmanagedCallersOnly]
    private static void OnConsoleMessageRaised(nint ucm, nint jsResult, nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            if (self?.ConsoleMessage is not { } handler || ReadJsResultString(jsResult) is not { } payload)
                return;
            var level = "log";
            var text = payload;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("level", out var l))
                    level = l.GetString() ?? "log";
                if (root.TryGetProperty("text", out var t))
                    text = t.GetString() ?? string.Empty;
            }
            catch (JsonException) { /* fall back to the raw payload */ }
            handler(level, text);
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnPageErrorRaised(nint ucm, nint jsResult, nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            if (self?.PageError is { } handler && ReadJsResultString(jsResult) is { } text)
                handler(text);
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnNotifyTitle(nint web, nint pspec, nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            if (self is null)
                return;
            var p = webkit_web_view_get_title(web);
            var title = p == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(p) ?? string.Empty;
            self.Trace?.Invoke($"title-changed: {title}");
            self.TitleChanged?.Invoke(title);
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static void OnLoadChanged(nint web, int loadEvent, nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            if (self is null)
                return;
            var uriPtr = webkit_web_view_get_uri(web);
            var uri = uriPtr == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(uriPtr) ?? string.Empty;
            switch (loadEvent)
            {
                case WebkitLoadStarted: self.Trace?.Invoke($"load-started: {uri}"); break;
                case WebkitLoadRedirected: self.Trace?.Invoke($"load-redirected: {uri}"); break;
                case WebkitLoadCommitted: self.Trace?.Invoke($"load-committed: {uri}"); break;
                case WebkitLoadFinished:
                    self.Trace?.Invoke($"load-finished: {uri}");
                    self.NavigationCompleted?.Invoke(uri);
                    break;
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly]
    private static int OnLoadFailed(nint web, int loadEvent, nint failingUri, nint error, nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            var uri = failingUri == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(failingUri) ?? string.Empty;
            var msg = ReadGErrorMessage(error);
            self?.Trace?.Invoke($"load-failed: {uri} — {msg}");
            self?.PageError?.Invoke($"Navigation failed: {uri} — {msg}");
        }
        catch { }
        return 0; // FALSE: let WebKit load its default error page
    }

    [UnmanagedCallersOnly]
    private static int DrainIdle(nint userData)
    {
        try
        {
            var self = FromUserData(userData);
            if (self is not null)
                while (self._gtkQueue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch { /* one queued action's failure must not drop the rest */ }
                }
        }
        catch { }
        return 0; // G_SOURCE_REMOVE: this idle source runs once
    }

    [UnmanagedCallersOnly]
    private static void OnEvalFinished(nint source, nint result, nint userData)
    {
        var handle = GCHandle.FromIntPtr(userData);
        var tcs = handle.Target as TaskCompletionSource<string>;
        try
        {
            var value = webkit_web_view_evaluate_javascript_finish(source, result, out var error);
            if (value == nint.Zero || error != nint.Zero)
            {
                var msg = ReadGErrorMessage(error);
                if (error != nint.Zero)
                    g_error_free(error);
                tcs?.TrySetException(new InvalidOperationException($"ExecuteScript failed: {msg}"));
            }
            else
            {
                var jsonPtr = jsc_value_to_json(value, 0);
                var json = jsonPtr == nint.Zero ? "null" : Marshal.PtrToStringUTF8(jsonPtr) ?? "null";
                if (jsonPtr != nint.Zero)
                    g_free(jsonPtr);
                tcs?.TrySetResult(json);
            }
        }
        catch (Exception ex)
        {
            tcs?.TrySetException(ex);
        }
        finally
        {
            handle.Free();
        }
    }
}
