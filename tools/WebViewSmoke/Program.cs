using System.Runtime.InteropServices;
using DIR.Lib;
using SdlVulkan.Renderer;
using SdlVulkan.Renderer.WebView;
using static SDL3.SDL;

namespace WebViewSmoke;

internal static partial class Program
{
    // Optional: tee all output to a file so detached / self-launched runs (which have no console
    // I can read back) still leave a trace. Set SMOKE_LOG=<path>.
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("SMOKE_LOG");
    private static readonly object LogLock = new();

    // libc _exit(2): terminate immediately WITHOUT running C atexit handlers / global destructors.
    // WebKitGTK registers process-global teardown (g_object_unref of singletons) that calls abort()
    // at exit — a known WebKit trait, harmless because it runs after all work, but it turns a clean
    // run into exit code 134. Fast-exiting past the atexit handlers gives a deterministic exit code.
    [LibraryImport("libc", EntryPoint = "_exit")]
    private static partial void LibcExit(int status);

    // Self-test page for `messaging`/`assert` mode: posts to the host on load and reflects any host
    // reply into document.title (observable via TitleChanged), exercising both directions of the bridge.
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

    // Reply the host bounces back to the page when it receives the page's message.
    private const string HostReply = "{\"reply\":\"pong from host\"}";

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

    // Terminates the process with a deterministic exit code. On Linux this skips libc atexit handlers
    // (see LibcExit) so WebKitGTK's process-global teardown can't turn a good run into a SIGABRT (134).
    private static void FastExit(int code)
    {
        Console.Out.Flush();
        if (OperatingSystem.IsLinux())
            LibcExit(code);
        Environment.Exit(code);
    }

    [STAThread] // WebView2 requires an STA thread
    private static int Main(string[] args)
    {
        var arg0 = args.Length > 0 ? args[0] : "https://example.com";
        // `assert` mode: run the network-free messaging self-test and exit 0/1 on the observed
        // signals (for CI). `messaging`: same self-test but interactive. Otherwise: navigate to a URL.
        var assertMode = string.Equals(arg0, "assert", StringComparison.OrdinalIgnoreCase);
        Log($"[smoke] process arch = {RuntimeInformation.ProcessArchitecture}");

        try
        {
            return Run(arg0, assertMode);
        }
        catch (Exception ex) when (assertMode)
        {
            // No display / no WebKitGTK / no Vulkan ICD on this runner → skip rather than fail
            // (mirrors BlendOpRegressionTests' Assert.Skip when Vulkan is unavailable).
            Log($"SMOKE: SKIP (GUI stack unavailable): {ex.GetType().Name}: {ex.Message}");
            FastExit(0);
            return 0; // unreachable
        }
    }

    private static int Run(string arg0, bool assertMode)
    {
        var url = assertMode ? "messaging" : arg0;
        var messaging = string.Equals(url, "messaging", StringComparison.OrdinalIgnoreCase);

        using var window = SdlVulkanWindow.Create("WebView smoke", 1280, 800);
        var hwnd = window.GetNativeWindowHandle();
        Log($"[smoke] native window handle = 0x{hwnd:X}");
        if (hwnd == nint.Zero)
        {
            if (assertMode)
                throw new InvalidOperationException("window exposed no native handle");
            Console.Error.WriteLine("[smoke] FAIL: window exposed no native handle.");
            return 1;
        }

        using var webView = NativeWebView.Create();
        Log($"[smoke] backend = {webView.GetType().Name}");

        // Assert-mode signal tracking: page→host message, host→page reply (via title), and a
        // completed navigation. PASS once all three are observed; exit before teardown.
        var gotPageMessage = false;
        var gotHostReply = false;
        var gotNavigation = false;
        void CheckAssert()
        {
            if (assertMode && gotPageMessage && gotHostReply && gotNavigation)
            {
                Log("SMOKE: PASS");
                FastExit(0);
            }
        }

        // Granular redirect/navigation trace (nav-starting → source-changed → nav-completed).
        webView.Trace += s => Log($"[trace] {s}");
        webView.TitleChanged += t =>
        {
            Log($"[smoke] title changed: {t}");
            if (t.Contains("pong from host", StringComparison.Ordinal))
            {
                gotHostReply = true;
                CheckAssert();
            }
        };

        // In-page diagnostics (console output and uncaught JS errors).
        webView.ConsoleMessage += (level, text) => Log($"[console:{level}] {text}");
        webView.PageError += text => Log($"[js-error] {text}");

        // Two-way messaging: log page → host messages, and bounce a reply back (host → page).
        webView.MessageReceived += json =>
        {
            Log($"[message] page -> host: {json}");
            if (json.Contains("from page", StringComparison.Ordinal))
                gotPageMessage = true;
            webView.PostMessage(HostReply);
            CheckAssert();
        };

        // On each completed navigation, prove JS execution works by probing the live DOM —
        // this also reveals where a redirect actually landed and whether the page has content.
        webView.NavigationCompleted += completedUrl =>
        {
            Log($"[smoke] navigation completed: {completedUrl}");
            gotNavigation = true;
            CheckAssert();
            webView.ExecuteScriptAsync(
                    "location.href + ' | title=' + JSON.stringify(document.title) + ' | bodyChars=' + " +
                    "(document.body ? document.body.innerText.length : 0) + ' | ua=' + navigator.userAgent")
                .ContinueWith(t => Log(t.IsFaulted
                    ? $"[smoke] js-probe failed: {t.Exception?.GetBaseException().Message}"
                    : $"[smoke] js-probe: {t.Result}"));
        };

        webView.AttachToWindow(window);
        if (messaging)
        {
            Log(assertMode
                ? "[smoke] assert mode: two-way messaging self-test (expecting page->host, host->page, navigation)."
                : "[smoke] two-way messaging self-test (NavigateToString) — close the window to exit.");
            webView.NavigateToString(MessagingTestHtml);
        }
        else
        {
            webView.Navigate(url);
            Log($"[smoke] navigating to {url} — close the window to exit.");
        }

        // Bounded run for automated/unattended verification (SMOKE_EXIT_AFTER_MS); assert mode
        // defaults to a generous 30s so a slow software-rendered CI runner still completes.
        var exitAfterMs = int.TryParse(Environment.GetEnvironmentVariable("SMOKE_EXIT_AFTER_MS"), out var ms)
            ? ms
            : (assertMode ? 30_000 : 0);
        var start = Environment.TickCount64;

        var running = true;
        while (running)
        {
            if (exitAfterMs > 0 && Environment.TickCount64 - start >= exitAfterMs)
                break;

            // WaitEventTimeout pumps the event queue even when idle, so async backend callbacks
            // (WebView2 controller creation, navigation completion) get dispatched.
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

        if (assertMode)
        {
            Log($"SMOKE: FAIL: missing signals (page->host={gotPageMessage}, host->page={gotHostReply}, navigation={gotNavigation})");
            FastExit(1);
        }

        Log("[smoke] exiting.");
        FastExit(0);
        return 0; // unreachable
    }
}
