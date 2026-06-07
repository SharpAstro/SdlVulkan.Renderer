using System.Runtime.InteropServices;
using DIR.Lib;
using SdlVulkan.Renderer;
using SdlVulkan.Renderer.WebView;
using static SDL3.SDL;

namespace WebViewSmoke;

internal static class Program
{
    // Optional: tee all output to a file so detached / self-launched runs (which have no console
    // I can read back) still leave a trace. Set SMOKE_LOG=<path>.
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("SMOKE_LOG");
    private static readonly object LogLock = new();

    // Self-test page for `messaging` mode: posts to the host on load and reflects any host reply
    // into document.title (observable via TitleChanged), exercising both directions of the bridge.
    private const string MessagingTestHtml = """
        <!doctype html><meta charset="utf-8"><title>msg-test</title>
        <h1>WebView two-way messaging test</h1><pre id="log"></pre>
        <script>
          const log = m => { document.getElementById('log').textContent += m + '\n'; };
          window.chrome.webview.addEventListener('message', e => {
            log('page <- host: ' + JSON.stringify(e.data));
            document.title = 'host said: ' + (e.data && e.data.reply);
          });
          window.chrome.webview.postMessage({ hello: 'from page', n: 7 });
          log('page -> host posted');
        </script>
        """;

    private static void Log(string line)
    {
        Console.WriteLine(line);
        Console.Out.Flush();
        if (LogPath is { Length: > 0 })
        {
            try { lock (LogLock) File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { /* best-effort diagnostic log; never fail the run over it */ }
        }
    }

    [STAThread] // WebView2 requires an STA thread
    private static void Main(string[] args)
    {
        var url = args.Length > 0 ? args[0] : "https://example.com";
        Log($"[smoke] process arch = {RuntimeInformation.ProcessArchitecture}");

        using var window = SdlVulkanWindow.Create("WebView smoke", 1280, 800);
        var hwnd = window.GetNativeWindowHandle();
        Log($"[smoke] native window handle = 0x{hwnd:X}");
        if (hwnd == nint.Zero)
        {
            Console.Error.WriteLine("[smoke] FAIL: window exposed no native handle.");
            return;
        }

        using var webView = NativeWebView.Create();
        Log($"[smoke] backend = {webView.GetType().Name}");

        // Granular redirect/navigation trace (nav-starting → source-changed → nav-completed).
        webView.Trace += s => Log($"[trace] {s}");
        webView.TitleChanged += t => Log($"[smoke] title changed: {t}");

        // In-page diagnostics (via the DevTools Protocol): console output and uncaught JS errors.
        webView.ConsoleMessage += (level, text) => Log($"[console:{level}] {text}");
        webView.PageError += text => Log($"[js-error] {text}");

        // Two-way messaging: log page → host messages, and bounce a reply back (host → page).
        webView.MessageReceived += json =>
        {
            Log($"[message] page -> host: {json}");
            webView.PostMessage("{\"reply\":\"pong from host\"}");
        };

        // On each completed navigation, prove JS execution works by probing the live DOM —
        // this also reveals where a redirect actually landed and whether the page has content.
        webView.NavigationCompleted += completedUrl =>
        {
            Log($"[smoke] navigation completed: {completedUrl}");
            webView.ExecuteScriptAsync(
                    "location.href + ' | title=' + JSON.stringify(document.title) + ' | bodyChars=' + " +
                    "(document.body ? document.body.innerText.length : 0) + ' | ua=' + navigator.userAgent")
                .ContinueWith(t => Log(t.IsFaulted
                    ? $"[smoke] js-probe failed: {t.Exception?.GetBaseException().Message}"
                    : $"[smoke] js-probe: {t.Result}"));
        };

        webView.AttachToWindow(window);
        if (string.Equals(url, "messaging", StringComparison.OrdinalIgnoreCase))
        {
            Log("[smoke] two-way messaging self-test (NavigateToString) — close the window to exit.");
            webView.NavigateToString(MessagingTestHtml);
        }
        else
        {
            webView.Navigate(url);
            Log($"[smoke] navigating to {url} — close the window to exit.");
        }

        // Optional bounded run for automated/unattended verification: SMOKE_EXIT_AFTER_MS=8000.
        var exitAfterMs = int.TryParse(Environment.GetEnvironmentVariable("SMOKE_EXIT_AFTER_MS"), out var ms) ? ms : 0;
        var start = Environment.TickCount64;

        var running = true;
        while (running)
        {
            if (exitAfterMs > 0 && Environment.TickCount64 - start >= exitAfterMs)
                break;

            // WaitEventTimeout pumps Win32 messages even when idle, so WebView2's async
            // environment/controller-creation and navigation callbacks get dispatched.
            if (WaitEventTimeout(out var evt, 16))
            {
                do
                {
                    switch ((EventType)evt.Type)
                    {
                        case EventType.Quit:
                        case EventType.WindowCloseRequested:
                            running = false;
                            break;

                        case EventType.WindowResized:
                        case EventType.WindowPixelSizeChanged:
                            window.GetSizeInPixels(out var w, out var h);
                            if (w > 0 && h > 0)
                                webView.SetBounds(new RectInt(new PointInt(w, h), new PointInt(0, 0)));
                            break;
                    }
                } while (running && PollEvent(out evt));
            }
        }

        Log("[smoke] exiting.");
    }
}
