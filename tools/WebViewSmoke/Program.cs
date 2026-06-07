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

    // Self-test page for `autosize`/`assert-autosize` mode: a fixed-size content box the host can resize
    // by posting { setHeight: N }. The body shrink-wraps the box vertically, so the content height the
    // sizer reports tracks the box height for both growth and shrink (the property the sizer relies on).
    // The status <pre> is position:fixed so it stays out of flow and doesn't affect the measured height.
    private const string AutoSizeTestHtml = """
        <!doctype html><meta charset="utf-8"><title>autosize-test</title>
        <style>html,body{margin:0;padding:0}#box{width:600px;height:400px;background:#0088ff}</style>
        <div id="box"></div>
        <pre id="log" style="position:fixed;right:0;bottom:0;margin:0;font:12px monospace"></pre>
        <script>
          var log = function (m) { var el = document.getElementById('log'); if (el) el.textContent += m + '\n'; };
          window.chrome.webview.addEventListener('message', function (e) {
            var d = e.data;
            if (d && typeof d.setHeight === 'number') {
              document.getElementById('box').style.height = d.setHeight + 'px';
              log('set box height ' + d.setHeight);
            }
          });
          log('autosize page ready');
        </script>
        """;

    // The host asks the page to shrink its content box to this many CSS px mid-test. The assert only
    // checks that the reported content height grows initially and then shrinks well below it once the
    // box is shrunk — DPI-independent, and proof that shrink-to-fit below the viewport works.
    private const int AutoSizeShrinkToCssPx = 250;
    private const string AutoSizeShrinkMessage = "{\"setHeight\":250}";

    private enum Scenario { Navigate, Messaging, AutoSize }

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
        // Modes: `assert` = messaging self-test, exit 0/1 (CI). `assert-autosize` = content-sizer
        // self-test, exit 0/1 (CI). `messaging`/`autosize` = the same self-tests but interactive.
        // Anything else = navigate to that URL.
        var (scenario, assertMode) = arg0.ToLowerInvariant() switch
        {
            "assert" => (Scenario.Messaging, true),
            "assert-autosize" => (Scenario.AutoSize, true),
            "messaging" => (Scenario.Messaging, false),
            "autosize" => (Scenario.AutoSize, false),
            _ => (Scenario.Navigate, false),
        };
        Log($"[smoke] process arch = {RuntimeInformation.ProcessArchitecture}, scenario = {scenario}");

        try
        {
            return Run(scenario, assertMode, arg0);
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

    private static int Run(Scenario scenario, bool assertMode, string navigateUrl)
    {
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

        // Common diagnostics: redirect/navigation trace, console output, uncaught JS errors.
        webView.Trace += s => Log($"[trace] {s}");
        webView.ConsoleMessage += (level, text) => Log($"[console:{level}] {text}");
        webView.PageError += text => Log($"[js-error] {text}");

        // assertMode PASS bookkeeping; each scenario sets its own predicate and calls Pass() when met.
        void Pass()
        {
            Log("SMOKE: PASS");
            FastExit(0);
        }

        Func<string> failReason = () => "no scenario signals tracked";

        switch (scenario)
        {
            case Scenario.Messaging:
                failReason = WireMessaging(webView, assertMode, Pass);
                break;
            case Scenario.AutoSize:
                failReason = WireAutoSize(webView, window, assertMode, Pass);
                break;
            case Scenario.Navigate:
                WireNavigateProbe(webView);
                break;
        }

        webView.AttachToWindow(window);

        switch (scenario)
        {
            case Scenario.Messaging:
                Log(assertMode
                    ? "[smoke] assert mode: two-way messaging self-test (expecting page->host, host->page, navigation)."
                    : "[smoke] two-way messaging self-test (NavigateToString) — close the window to exit.");
                webView.NavigateToString(MessagingTestHtml);
                break;
            case Scenario.AutoSize:
                Log(assertMode
                    ? "[smoke] assert mode: content-sizer self-test (expecting an initial content height, then a smaller one after shrink)."
                    : "[smoke] content-sizer self-test (NavigateToString) — close the window to exit.");
                webView.NavigateToString(AutoSizeTestHtml);
                break;
            case Scenario.Navigate:
                webView.Navigate(navigateUrl);
                Log($"[smoke] navigating to {navigateUrl} — close the window to exit.");
                break;
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
                            // Only the navigate/messaging scenarios fill the window; autosize owns the
                            // bounds itself via the content-sizer, so don't fight it here.
                            if (scenario != Scenario.AutoSize)
                            {
                                window.GetSizeInPixels(out var w, out var h);
                                if (w > 0 && h > 0)
                                    webView.SetBounds(new RectInt(new PointInt(w, h), new PointInt(0, 0)));
                            }
                            break;
                    }
                } while (running && PollEvent(out evt));
            }
        }

        if (assertMode)
        {
            Log($"SMOKE: FAIL: {failReason()}");
            FastExit(1);
        }

        Log("[smoke] exiting.");
        FastExit(0);
        return 0; // unreachable
    }

    // Two-way messaging: log page→host messages, bounce a reply (host→page), and prove JS execution on
    // each completed navigation. In assert mode, PASS once page->host, host->page, and a navigation are
    // all observed. Returns the failure-reason provider used when the signals don't arrive in time.
    private static Func<string> WireMessaging(INativeWebView webView, bool assertMode, Action pass)
    {
        var gotPageMessage = false;
        var gotHostReply = false;
        var gotNavigation = false;
        void CheckAssert()
        {
            if (assertMode && gotPageMessage && gotHostReply && gotNavigation)
                pass();
        }

        webView.TitleChanged += t =>
        {
            Log($"[smoke] title changed: {t}");
            if (t.Contains("pong from host", StringComparison.Ordinal))
            {
                gotHostReply = true;
                CheckAssert();
            }
        };

        webView.MessageReceived += json =>
        {
            Log($"[message] page -> host: {json}");
            if (json.Contains("from page", StringComparison.Ordinal))
                gotPageMessage = true;
            webView.PostMessage(HostReply);
            CheckAssert();
        };

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

        return () => $"missing signals (page->host={gotPageMessage}, host->page={gotHostReply}, navigation={gotNavigation})";
    }

    // Content-sizer: attach a WebViewContentSizer, let it auto-size the webview's height to the page, and
    // verify the content→host channel reacts to a content change. In assert mode, PASS once an initial
    // content height is reported and then a clearly smaller one arrives after the host shrinks the box.
    private static Func<string> WireAutoSize(INativeWebView webView, SdlVulkanWindow window, bool assertMode, Action pass)
    {
        window.GetSizeInPixels(out var winW, out _);

        // Keep the sizer rooted for the lifetime of the run (it also stays reachable via the webview's
        // event subscriptions). Height-axis auto-size with a fixed width = the common fit-to-content embed.
        var sizer = new WebViewContentSizer(webView);
        sizer.EnableAutoSize(origin: new PointInt(0, 0), fixedExtent: new PointInt(winW, 0), axis: AutoSizeAxis.Height);

        var initialHeight = 0;
        var requestedShrink = false;
        var gotShrink = false;

        webView.NavigationCompleted += completedUrl => Log($"[smoke] navigation completed: {completedUrl}");

        sizer.ContentSizeChanged += size =>
        {
            Log($"[autosize] content size reported = {size.X}x{size.Y} (device px)");

            if (initialHeight == 0 && size.Y >= 100)
            {
                initialHeight = size.Y;
                // Ask the page to shrink its content box well below the initial height. Posts via the
                // raw webview (the sizer consumes only inbound __sdlLayout envelopes).
                requestedShrink = true;
                Log($"[autosize] requesting shrink to {AutoSizeShrinkToCssPx} CSS px (initial reported height {initialHeight})");
                webView.PostMessage(AutoSizeShrinkMessage);
                return;
            }

            // A height clearly smaller than the initial proves reactivity + shrink-to-fit below the
            // viewport (which documentElement.scrollHeight could not report). 250 vs 400 CSS px = 0.625.
            if (requestedShrink && initialHeight > 0 && size.Y <= initialHeight * 0.8 && size.Y >= 50)
            {
                gotShrink = true;
                Log($"[autosize] shrink observed: {size.Y} <= {initialHeight} * 0.8");
                if (assertMode)
                    pass();
            }
        };

        return () => $"missing signals (initialHeight={initialHeight}, requestedShrink={requestedShrink}, gotShrink={gotShrink})";
    }

    // Navigate-to-URL: on each completed navigation, probe the live DOM to prove JS execution and reveal
    // where a redirect actually landed.
    private static void WireNavigateProbe(INativeWebView webView)
    {
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
    }
}
