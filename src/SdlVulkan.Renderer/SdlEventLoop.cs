using System.Diagnostics;
using DIR.Lib;
using Vortice.Vulkan;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// SDL3+Vulkan event loop with idle suppression, swapchain recovery, and font-atlas dirty handling.
/// Drives one or more windows: each window is an <see cref="SdlWindowView"/> (window + renderer +
/// callbacks), and every SDL event is routed to the matching view by <see cref="SdlVulkanWindow.WindowId"/>.
/// <para>
/// For a single window, use the <see cref="SdlEventLoop(SdlVulkanWindow, VkRenderer)"/> constructor and
/// set callbacks on the loop directly — those forward to the one "primary" view. For multiple windows,
/// build the loop with the parameterless constructor and call <see cref="AddWindow"/>, setting callbacks
/// on the returned view.
/// </para>
/// </summary>
public sealed class SdlEventLoop
{
    // windowID -> view, plus a stable iteration list (render order, also survives dict churn).
    private readonly Dictionary<uint, SdlWindowView> _views = new();
    private readonly List<SdlWindowView> _viewList = new();
    private readonly SdlWindowView? _primary; // set by the single-window convenience ctor
    private bool _running;

    private static readonly ulong MouseRedrawInterval = GetPerformanceFrequency() / 30; // ~30fps

#if DEBUG
    // Slow-frame diagnostics: a rolling average of real frame time (BeginFrame->EndFrame) plus a
    // threshold, so ANY stall (atlas evict/grow drain, heavy tessellation, a present hitch) logs one
    // [rdiag] frame.slow line. Shared across windows — it's only diagnostics.
    private double _frameAvgMs;
    private const double SlowFrameFloorMs = 40;  // stay quiet below this — normal frames don't log
    private const double SlowFrameFactor = 3;    // flag a spike that's >3x the rolling average...
    private const double HardStallMs = 150;      // ...or any outright freeze, regardless of average
#endif

    /// <summary>Multi-window constructor. Add windows with <see cref="AddWindow"/>.</summary>
    public SdlEventLoop() { }

    /// <summary>Single-window convenience: registers one window whose callbacks are the loop's own
    /// forwarding properties (<see cref="OnRender"/>, <see cref="OnResize"/>, …).</summary>
    public SdlEventLoop(SdlVulkanWindow window, VkRenderer renderer)
    {
        _primary = AddWindow(window, renderer);
    }

    /// <summary>
    /// Registers a window + renderer as a new <see cref="SdlWindowView"/> and returns it so callbacks
    /// can be set. Safe to call while the loop is running (e.g. when tearing a tab out into a new
    /// window): events for the new window route to it on the next iteration.
    /// </summary>
    public SdlWindowView AddWindow(SdlVulkanWindow window, VkRenderer renderer)
    {
        var view = new SdlWindowView(window, renderer);
        _views[window.WindowId] = view;
        _viewList.Add(view);
        return view;
    }

    /// <summary>Unregisters a window's view (e.g. when its window is closed). Does not dispose the
    /// window/renderer — the caller owns that. Safe to call from within a callback.</summary>
    public void RemoveWindow(SdlWindowView view)
    {
        _views.Remove(view.Window.WindowId);
        _viewList.Remove(view);
    }

    /// <summary>Number of windows currently registered.</summary>
    public int WindowCount => _viewList.Count;

    // --- Loop-level callbacks (process-wide, not per-window) ---

    /// <summary>
    /// Called when SDL posts a <c>Quit</c> (app should exit — typically the last window closed, or an
    /// OS quit). Return true to intercept and keep running (e.g. for graceful shutdown); null or false
    /// stops the loop. Per-window close buttons fire <see cref="SdlWindowView.OnCloseRequested"/> instead.
    /// </summary>
    public Func<bool>? OnQuit { get; set; }

    /// <summary>Called once per loop iteration after any windows render. Use for process-wide post-frame
    /// work (background task completions, state cleanup).</summary>
    public Action? OnPostFrame { get; set; }

    // --- Single-window forwarding properties (delegate to the primary view) ---

    private SdlWindowView Primary => _primary
        ?? throw new InvalidOperationException(
            "Loop-level callbacks require the single-window constructor. For multiple windows, set callbacks on the SdlWindowView returned by AddWindow.");

    /// <summary>Background color for the primary window's frame clear.</summary>
    public RGBAColor32 BackgroundColor { get => Primary.BackgroundColor; set => Primary.BackgroundColor = value; }
    public Action? OnRender { get => Primary.OnRender; set => Primary.OnRender = value; }
    public Action<uint, uint>? OnResize { get => Primary.OnResize; set => Primary.OnResize = value; }
    public Func<InputKey, InputModifier, bool>? OnKeyDown { get => Primary.OnKeyDown; set => Primary.OnKeyDown = value; }
    public Func<byte, float, float, byte, InputModifier, bool>? OnMouseDown { get => Primary.OnMouseDown; set => Primary.OnMouseDown = value; }
    public Func<float, float, bool>? OnMouseMove { get => Primary.OnMouseMove; set => Primary.OnMouseMove = value; }
    public Action<byte>? OnMouseUp { get => Primary.OnMouseUp; set => Primary.OnMouseUp = value; }
    public Func<float, float, float, bool>? OnMouseWheel { get => Primary.OnMouseWheel; set => Primary.OnMouseWheel = value; }
    public Action<float, float, float>? OnPinch { get => Primary.OnPinch; set => Primary.OnPinch = value; }
    public Action? OnPinchEnd { get => Primary.OnPinchEnd; set => Primary.OnPinchEnd = value; }
    public Action<string>? OnTextInput { get => Primary.OnTextInput; set => Primary.OnTextInput = value; }
    public Action<string>? OnDropFile { get => Primary.OnDropFile; set => Primary.OnDropFile = value; }
    public Func<bool>? CheckNeedsRedraw { get => Primary.CheckNeedsRedraw; set => Primary.CheckNeedsRedraw = value; }

    /// <summary>Active touch fingers on the primary window.</summary>
    public int ActiveFingerCount => Primary.ActiveFingerCount;

    /// <summary>Requests a redraw on the next iteration for all windows.</summary>
    public void RequestRedraw()
    {
        foreach (var v in _viewList) v.NeedsRedraw = true;
    }

    /// <summary>Stops the event loop. Safe to call from any callback.</summary>
    public void Stop() => _running = false;

    /// <summary>
    /// Runs the event loop until <see cref="Stop"/> is called or the cancellation token is triggered.
    /// Blocks the calling thread.
    /// </summary>
    public void Run(CancellationToken ct = default)
    {
        _running = true;

        while (_running && !ct.IsCancellationRequested)
        {
            var anyNeedsRedraw = false;
            foreach (var v in _viewList)
                if (v.NeedsRedraw) { anyNeedsRedraw = true; break; }

            Event evt;
            var hadEvent = anyNeedsRedraw
                ? PollEvent(out evt)
                : WaitEventTimeout(out evt, 16);

            if (hadEvent)
            {
                do
                {
                    Dispatch(ref evt);
                } while (_running && PollEvent(out evt));
            }

            // Per-window external redraw checks (background task completions, cursor blink, …).
            foreach (var v in _viewList)
                if (v.CheckNeedsRedraw?.Invoke() == true)
                    v.NeedsRedraw = true;

            if (!_running)
                break;

            var renderedAny = false;
            // Snapshot the view list — a render callback could add/remove a window (tab tear-out).
            for (var i = 0; i < _viewList.Count; i++)
            {
                var v = _viewList[i];
                if (!v.NeedsRedraw) continue;
                v.NeedsRedraw = false;
                if (RenderView(v))
                    renderedAny = true;
            }

            if (renderedAny)
                OnPostFrame?.Invoke();
        }
    }

    // Renders one window. Returns true if a frame was actually drawn (false if the swapchain needed
    // recreation or recovery ran). Mirrors the original single-window frame body, scoped to a view.
    private bool RenderView(SdlWindowView v)
    {
        var renderer = v.Renderer;
        try
        {
#if DEBUG
            var frameStart = Stopwatch.GetTimestamp();
#endif
            if (!renderer.BeginFrame(v.BackgroundColor))
            {
                v.Window.GetSizeInPixels(out var sw, out var sh);
                if (sw > 0 && sh > 0)
                {
                    renderer.Resize((uint)sw, (uint)sh);
                    v.OnResize?.Invoke((uint)sw, (uint)sh);
                }
                v.NeedsRedraw = true;
                return false;
            }

            v.OnRender?.Invoke();

            renderer.EndFrame();

#if DEBUG
            // Whole-frame time (the BeginFrame atlas evict/grow drain included). Flag a frame
            // that's over the floor AND a big spike over the rolling average, or any hard freeze.
            var frameMs = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
            var prevAvg = _frameAvgMs;
            _frameAvgMs = prevAvg <= 0 ? frameMs : prevAvg * 0.9 + frameMs * 0.1;
            if (frameMs > SlowFrameFloorMs && (prevAvg <= 0 || frameMs > prevAvg * SlowFrameFactor || frameMs > HardStallMs))
                RenderDiag.Log("frame.slow", $"{frameMs:F0}ms avg={prevAvg:F0}ms");
#endif

            if (renderer.FontAtlasDirty)
                v.NeedsRedraw = true;

            return true;
        }
        catch (VkException vk)
        {
            // A Vulkan call threw mid-frame — most commonly vkQueueSubmit/Present in EndFrame
            // returning a non-success status that CheckResult turns into a throw (ErrorOutOfDateKHR
            // after a window resize during submit, or driver bugs that surface
            // ErrorInitializationFailed). Killing the process on every recoverable hiccup is too
            // aggressive — try to rebuild sync + swapchain for this window and continue.
            Console.Error.WriteLine($"[SdlEventLoop] Vulkan error mid-frame (window {v.Window.WindowId}): {vk.Result}. Recovering swapchain.");
            try
            {
                // Track consecutive recoveries (errors within 1s of each other) so we can back off
                // a runaway recover->fail->recover loop instead of spinning at frame rate.
                var now = Environment.TickCount64;
                v.RecoverStreak = (now - v.LastRecoverTick < 1000) ? v.RecoverStreak + 1 : 0;
                v.LastRecoverTick = now;

                v.Window.GetSizeInPixels(out var sw, out var sh);
                if (sw > 0 && sh > 0)
                {
                    renderer.RecoverFromGpuError();
                    // Only re-run layout when the size actually changed — during a recovery storm
                    // the size is unchanged, and re-notifying every cycle is pure churn.
                    if ((uint)sw != v.LastRecoverW || (uint)sh != v.LastRecoverH)
                    {
                        v.OnResize?.Invoke((uint)sw, (uint)sh);
                        v.LastRecoverW = (uint)sw;
                        v.LastRecoverH = (uint)sh;
                    }
                }
                v.NeedsRedraw = true;

                // If recovery keeps failing, throttle the retry rate with an increasing sleep (capped
                // at 1s) so we don't hammer the GPU. The loop still polls events between attempts, so
                // the window stays closable.
                if (v.RecoverStreak >= 4)
                    System.Threading.Thread.Sleep((int)Math.Min(1000, 100 * (v.RecoverStreak - 3)));
            }
            catch (Exception inner)
            {
                // Recovery itself failed (likely a true device-lost) — there's no sensible way to
                // continue, so bail out of the loop cleanly so the caller can dispose state.
                Console.Error.WriteLine($"[SdlEventLoop] Vulkan recovery failed: {inner.GetType().Name}: {inner.Message}. Stopping event loop.");
                _running = false;
            }
            return false;
        }
    }

    private bool TryView(uint windowId, out SdlWindowView view) => _views.TryGetValue(windowId, out view!);

    private void Dispatch(ref Event evt)
    {
        switch ((EventType)evt.Type)
        {
            case EventType.Quit:
                if (OnQuit?.Invoke() != true)
                    _running = false;
                break;

            case EventType.WindowCloseRequested:
                if (TryView(evt.Window.WindowID, out var vc))
                {
                    // Per-window close. When unhandled, ignore it here and let an eventual SDL Quit
                    // drive shutdown (preserves single-window behavior, which relies on OnQuit).
                    vc.OnCloseRequested?.Invoke();
                }
                break;

            case EventType.WindowResized:
            case EventType.WindowPixelSizeChanged:
                if (TryView(evt.Window.WindowID, out var vr))
                {
                    vr.Window.GetSizeInPixels(out var rw, out var rh);
                    if (rw > 0 && rh > 0)
                    {
                        vr.Renderer.Resize((uint)rw, (uint)rh);
                        vr.OnResize?.Invoke((uint)rw, (uint)rh);
                    }
                    vr.NeedsRedraw = true;
                }
                break;

            case EventType.WindowExposed:
                if (TryView(evt.Window.WindowID, out var ve))
                    ve.NeedsRedraw = true;
                break;

            case EventType.KeyDown:
                if (TryView(evt.Key.WindowID, out var vk))
                {
                    vk.OnKeyDown?.Invoke(evt.Key.Scancode.ToInputKey, evt.Key.Mod.ToInputModifier);
                    vk.NeedsRedraw = true;
                }
                break;

            case EventType.MouseButtonDown:
                if (TryView(evt.Button.WindowID, out var vmd))
                {
                    vmd.OnMouseDown?.Invoke(evt.Button.Button, evt.Button.X, evt.Button.Y, evt.Button.Clicks, GetModState().ToInputModifier);
                    vmd.NeedsRedraw = true;
                }
                break;

            case EventType.MouseButtonUp:
                if (TryView(evt.Button.WindowID, out var vmu))
                    vmu.OnMouseUp?.Invoke(evt.Button.Button);
                break;

            case EventType.MouseMotion:
                if (TryView(evt.Motion.WindowID, out var vmm))
                {
                    var newMx = evt.Motion.X;
                    var newMy = evt.Motion.Y;
                    if (newMx != vmm.MouseX || newMy != vmm.MouseY)
                    {
                        vmm.MouseX = newMx;
                        vmm.MouseY = newMy;
                        if (vmm.OnMouseMove?.Invoke(vmm.MouseX, vmm.MouseY) == true)
                        {
                            // Throttle mouse-driven redraws to ~30fps (per window).
                            var now = GetPerformanceCounter();
                            if (now - vmm.LastMouseRedrawCounter >= MouseRedrawInterval)
                            {
                                vmm.LastMouseRedrawCounter = now;
                                vmm.NeedsRedraw = true;
                            }
                        }
                    }
                }
                break;

            case EventType.MouseWheel:
                if (TryView(evt.Wheel.WindowID, out var vw))
                {
                    vw.OnMouseWheel?.Invoke(evt.Wheel.Y, vw.MouseX, vw.MouseY);
                    vw.NeedsRedraw = true;
                }
                break;

            case EventType.FingerDown:
            case EventType.FingerUp:
            case EventType.FingerMotion:
            {
                // SDL3-CS Event union doesn't expose a TouchFingerEvent field directly.
                // Reinterpret the raw event as TouchFingerEvent via Unsafe.As.
                ref var tfe = ref System.Runtime.CompilerServices.Unsafe.As<Event, TouchFingerEvent>(ref evt);
                if (!TryView(tfe.WindowID, out var vf))
                    break;
                var fid = (long)tfe.FingerID;

                if ((EventType)evt.Type == EventType.FingerUp)
                {
                    var wasPinching = vf.ActiveFingers.Count >= 2;
                    vf.ActiveFingers.Remove(fid);
                    if (wasPinching && vf.ActiveFingers.Count < 2)
                        vf.OnPinchEnd?.Invoke();
                    break;
                }

                var fx = tfe.X * vf.Renderer.Width;
                var fy = tfe.Y * vf.Renderer.Height;
                vf.ActiveFingers[fid] = (fx, fy);

                if ((EventType)evt.Type == EventType.FingerDown && vf.ActiveFingers.Count == 2)
                    vf.PinchStartDist = GetFingerDistance(vf);

                if (vf.ActiveFingers.Count >= 2 && vf.PinchStartDist > 1f
                    && (EventType)evt.Type == EventType.FingerMotion)
                {
                    var dist = GetFingerDistance(vf);
                    // Absolute scale since pinch began (not relative per-frame)
                    var scale = dist / vf.PinchStartDist;
                    var (cx, cy) = GetFingerCenter(vf);
                    vf.OnPinch?.Invoke(scale, cx, cy);
                    vf.PinchStartDist = dist; // relative per-frame for scroll conversion
                    vf.NeedsRedraw = true;
                }
                break;
            }

            case EventType.TextInput:
                if (TryView(evt.Text.WindowID, out var vt) && vt.OnTextInput is not null)
                {
                    var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(evt.Text.Text);
                    if (text is not null)
                    {
                        vt.OnTextInput(text);
                        vt.NeedsRedraw = true;
                    }
                }
                break;

            case EventType.DropFile:
                if (TryView(evt.Drop.WindowID, out var vd) && vd.OnDropFile is not null)
                {
                    var path = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(evt.Drop.Data);
                    if (path is not null)
                    {
                        vd.OnDropFile(path);
                        vd.NeedsRedraw = true;
                    }
                }
                break;
        }
    }

    private static float GetFingerDistance(SdlWindowView v)
    {
        if (v.ActiveFingers.Count < 2) return 0f;
        using var e = v.ActiveFingers.Values.GetEnumerator();
        e.MoveNext(); var a = e.Current;
        e.MoveNext(); var b = e.Current;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static (float cx, float cy) GetFingerCenter(SdlWindowView v)
    {
        if (v.ActiveFingers.Count < 2) return (v.MouseX, v.MouseY);
        using var e = v.ActiveFingers.Values.GetEnumerator();
        e.MoveNext(); var a = e.Current;
        e.MoveNext(); var b = e.Current;
        return ((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
    }
}
