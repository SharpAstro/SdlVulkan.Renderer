using System.Text.Json;
using DIR.Lib;

namespace SdlVulkan.Renderer.WebView;

/// <summary>
/// Which axis (or axes) <see cref="WebViewContentSizer.EnableAutoSize"/> drives from the page's
/// reported content size.
/// </summary>
public enum AutoSizeAxis
{
    /// <summary>Track content height; keep the width fixed. The default and safe choice — a fixed
    /// width never reflows the page's text wrapping, so resizing can't re-trigger a report and loop.</summary>
    Height,

    /// <summary>Track content width; keep the height fixed.</summary>
    Width,

    /// <summary>Track both axes. Prone to reflow oscillation: changing the width reflows content,
    /// which changes the height, which reports again. Use only for content of intrinsic fixed size.</summary>
    Both,
}

/// <summary>
/// Adds content→host sizing to an <see cref="INativeWebView"/>: the page reports its intrinsic content
/// size and the host can grow/shrink the webview to fit it ("fit-to-content"). This is the reverse of
/// <see cref="INativeWebView.SetBounds"/> (host→browser) and is built entirely on the existing JS bridge —
/// it adds nothing to <see cref="INativeWebView"/>. A small reporter script (a <c>ResizeObserver</c>) posts
/// the content size over <c>window.chrome.webview.postMessage</c>, which arrives on
/// <see cref="INativeWebView.MessageReceived"/> and is surfaced here as <see cref="ContentSizeChanged"/>.
/// </summary>
/// <remarks>
/// <para>Wrap a created webview and keep the sizer for its lifetime:
/// <c>using var sizer = new WebViewContentSizer(webView);</c>. The reporter is (re)injected on every
/// <see cref="INativeWebView.NavigationCompleted"/> — a <c>ResizeObserver</c> does not survive a document
/// load — so construct the sizer before the first navigation.</para>
/// <para>Reported sizes are in <b>device pixels</b>, the same space as <see cref="INativeWebView.SetBounds"/>
/// and <c>SdlVulkanWindow.GetSizeInPixels</c> (the reporter pre-multiplies by <c>devicePixelRatio</c>),
/// so a reported size can be fed straight back into bounds.</para>
/// <para>The <c>__sdlLayout</c> message key is <b>reserved</b> for this protocol. Layout envelopes are
/// consumed here and never reach this class's own <see cref="MessageReceived"/> — subscribe to that
/// (instead of the webview's) to receive only your application's messages.</para>
/// <para>Threading mirrors the wrapped backend: <see cref="ContentSizeChanged"/> and
/// <see cref="MessageReceived"/> are raised on whatever thread the backend raises
/// <see cref="INativeWebView.MessageReceived"/> on (the GTK thread on Linux, the SDL UI thread on Windows) —
/// marshal back if a handler touches render state. Auto-size's <see cref="INativeWebView.SetBounds"/> call is
/// safe from there on both backends (GTK marshals onto its loop; WebView2's message event already runs on
/// the controller's UI thread).</para>
/// </remarks>
public sealed class WebViewContentSizer : IDisposable
{
    // Injected after each navigation. A ResizeObserver posts the content size (device px) to the host
    // whenever it changes, plus once immediately on install. It measures the <body> content box rather
    // than documentElement.scrollHeight, because the latter is clamped to the webview's own viewport and
    // so can't report content that is *smaller* than the current bounds (which would break shrink-to-fit);
    // the body shrink-wraps its content vertically, so its height tracks content for both growth and
    // shrink. (Body width still tends to fill the viewport — height is the reliable axis, hence the
    // AutoSizeAxis.Height default.) Namespaced under __sdlLayout so the host can tell layout reports apart
    // from application messages. Self-guards against double-install.
    private const string ReporterScript = """
        (function () {
          if (window.__sdlSizerInstalled) return;
          window.__sdlSizerInstalled = true;
          function report() {
            var el = document.body || document.documentElement;
            if (!el) return;
            var rect = el.getBoundingClientRect();
            var w = Math.max(el.scrollWidth, el.offsetWidth, Math.ceil(rect.width));
            var h = Math.max(el.scrollHeight, el.offsetHeight, Math.ceil(rect.height));
            var dpr = window.devicePixelRatio || 1;
            try {
              window.chrome.webview.postMessage({ __sdlLayout: { w: Math.ceil(w * dpr), h: Math.ceil(h * dpr) } });
            } catch (e) {}
          }
          try {
            var ro = new ResizeObserver(report);
            ro.observe(document.documentElement);
            if (document.body) ro.observe(document.body);
          } catch (e) {}
          report();
        })();
        """;

    private readonly INativeWebView _webView;
    private readonly object _gate = new();

    // Auto-size state — guarded by _gate (reports can arrive on the backend thread while a caller
    // is (re)configuring auto-size, and the initial apply runs on the caller's thread).
    private bool _autoSize;
    private AutoSizeAxis _axis;
    private PointInt _origin;
    private PointInt _fixedExtent;
    private PointInt? _min;
    private PointInt? _max;
    private int _epsilon = 1;
    private RectInt? _lastApplied;

    private volatile bool _disposed;

    /// <summary>The most recently reported content size in device pixels, or <c>null</c> before the
    /// first report.</summary>
    public PointInt? LastContentSize { get; private set; }

    /// <summary>Raised when the page reports a new intrinsic content size (device pixels).</summary>
    public event Action<PointInt>? ContentSizeChanged;

    /// <summary>Application messages from the page, with reserved <c>__sdlLayout</c> envelopes filtered out.
    /// Subscribe here instead of <see cref="INativeWebView.MessageReceived"/> to avoid seeing the sizer's
    /// own traffic.</summary>
    public event Action<string>? MessageReceived;

    /// <summary>Wraps <paramref name="webView"/> and starts injecting the reporter after each navigation.</summary>
    public WebViewContentSizer(INativeWebView webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Drives <see cref="INativeWebView.SetBounds"/> from each reported content size. The webview's
    /// top-left is pinned to <paramref name="origin"/>; the chosen <paramref name="axis"/> tracks the
    /// content while the other axis keeps the corresponding extent from <paramref name="fixedExtent"/>
    /// (its <c>X</c> is the fixed width for <see cref="AutoSizeAxis.Height"/>, its <c>Y</c> the fixed
    /// height for <see cref="AutoSizeAxis.Width"/>; both are ignored for <see cref="AutoSizeAxis.Both"/>).
    /// Results are clamped to <paramref name="min"/>/<paramref name="max"/> (device px) when supplied, and
    /// changes within <paramref name="epsilonPx"/> are ignored to damp reflow jitter.
    /// </summary>
    public void EnableAutoSize(PointInt origin, PointInt fixedExtent, AutoSizeAxis axis = AutoSizeAxis.Height,
        PointInt? min = null, PointInt? max = null, int epsilonPx = 1)
    {
        lock (_gate)
        {
            _autoSize = true;
            _origin = origin;
            _fixedExtent = fixedExtent;
            _axis = axis;
            _min = min;
            _max = max;
            _epsilon = Math.Max(0, epsilonPx);
            _lastApplied = null; // force the next report (or the immediate apply below) to take effect
        }
        if (LastContentSize is { } size)
            ApplyAutoSize(size);
    }

    /// <summary>Stops driving bounds from content size. Current bounds are left untouched.</summary>
    public void DisableAutoSize()
    {
        lock (_gate)
            _autoSize = false;
    }

    private void OnNavigationCompleted(string url)
    {
        if (_disposed)
            return;
        // The ResizeObserver is gone after a document load — re-inject. The script self-guards against
        // a double install, so a same-document completion is harmless. Fire-and-forget: the first report
        // arrives via MessageReceived.
        _ = SafeInjectAsync();
    }

    private async Task SafeInjectAsync()
    {
        try
        {
            await _webView.ExecuteScriptAsync(ReporterScript).ConfigureAwait(false);
        }
        catch
        {
            // The page may have navigated away again, or the backend torn down; the next
            // NavigationCompleted re-injects. Never surface an injection failure as an app error.
        }
    }

    private void OnMessageReceived(string json)
    {
        if (_disposed)
            return;
        if (TryReadLayout(json, out var size))
        {
            LastContentSize = size;
            ContentSizeChanged?.Invoke(size);
            ApplyAutoSize(size);
            return; // consumed — layout envelopes never reach the application's message stream
        }
        MessageReceived?.Invoke(json);
    }

    // Parses { "__sdlLayout": { "w": <int>, "h": <int> } }. Returns false for any other message (or
    // malformed JSON) so it passes through unchanged.
    private static bool TryReadLayout(string json, out PointInt size)
    {
        size = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("__sdlLayout", out var layout) ||
                layout.ValueKind != JsonValueKind.Object)
                return false;
            var w = layout.TryGetProperty("w", out var wv) && wv.TryGetInt32(out var wi) ? wi : 0;
            var h = layout.TryGetProperty("h", out var hv) && hv.TryGetInt32(out var hi) ? hi : 0;
            size = new PointInt(Math.Max(0, w), Math.Max(0, h));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ApplyAutoSize(PointInt content)
    {
        RectInt bounds;
        lock (_gate)
        {
            if (!_autoSize)
                return;

            var w = _axis is AutoSizeAxis.Width or AutoSizeAxis.Both ? content.X : _fixedExtent.X;
            var h = _axis is AutoSizeAxis.Height or AutoSizeAxis.Both ? content.Y : _fixedExtent.Y;
            if (_min is { } mn) { w = Math.Max(w, mn.X); h = Math.Max(h, mn.Y); }
            if (_max is { } mx) { w = Math.Min(w, mx.X); h = Math.Min(h, mx.Y); }

            // Skip near-no-op resizes: applying them would just reflow and re-trigger a report.
            if (_lastApplied is { } prev &&
                Math.Abs(prev.Width - w) <= _epsilon && Math.Abs(prev.Height - h) <= _epsilon)
                return;

            // RectInt is (LowerRight exclusive, UpperLeft inclusive): top-left at origin, size w×h.
            bounds = new RectInt(new PointInt(_origin.X + w, _origin.Y + h), _origin);
            _lastApplied = bounds;
        }
        _webView.SetBounds(bounds); // outside the lock: never hold it across a native/marshaled call
    }

    /// <summary>Unsubscribes from the wrapped webview. Does not dispose the webview itself.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _webView.NavigationCompleted -= OnNavigationCompleted;
        _webView.MessageReceived -= OnMessageReceived;
    }
}
