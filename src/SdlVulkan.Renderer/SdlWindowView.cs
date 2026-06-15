using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// One window's slice of an <see cref="SdlEventLoop"/>: the window, its renderer, the input
/// callbacks for events routed to this window (by <see cref="SdlVulkanWindow.WindowId"/>), and the
/// per-window frame/input state. The loop owns a set of these and dispatches each SDL event to the
/// matching view. For a single window the loop's own callback properties forward here, so simple
/// consumers never touch this type directly.
/// </summary>
public sealed class SdlWindowView(SdlVulkanWindow window, VkRenderer renderer)
{
    public SdlVulkanWindow Window => window;
    public VkRenderer Renderer => renderer;

    /// <summary>Background clear color used for this window's <see cref="VkRenderer.BeginFrame"/>.</summary>
    public RGBAColor32 BackgroundColor { get; set; } = new(0x1a, 0x1a, 0x2e, 0xff);

    /// <summary>Called each frame between BeginFrame and EndFrame for this window.</summary>
    public Action? OnRender { get; set; }

    /// <summary>Called after this window's renderer is resized. Parameters are the new pixel dimensions.</summary>
    public Action<uint, uint>? OnResize { get; set; }

    /// <summary>Called on key down in this window. Returns true if consumed.</summary>
    public Func<InputKey, InputModifier, bool>? OnKeyDown { get; set; }

    /// <summary>Called on mouse button down. Parameters: button (1=left,2=middle,3=right), pixel X, pixel Y, click count, modifiers.</summary>
    public Func<byte, float, float, byte, InputModifier, bool>? OnMouseDown { get; set; }

    /// <summary>Called on mouse motion (pixel coords). Return true to trigger a redraw.</summary>
    public Func<float, float, bool>? OnMouseMove { get; set; }

    /// <summary>Called on mouse button up. Parameter is button (1=left,2=middle,3=right).</summary>
    public Action<byte>? OnMouseUp { get; set; }

    /// <summary>Called on mouse wheel. Parameters: scrollY, mouseX, mouseY. Return true if consumed.</summary>
    public Func<float, float, float, bool>? OnMouseWheel { get; set; }

    /// <summary>Called on trackpad/touch pinch. Parameters: scale (absolute since start), anchorX, anchorY
    /// (pixels), source. For a touchscreen (<see cref="PinchSource.Touchscreen"/>) the anchor is the real
    /// finger midpoint; for a touchpad (<see cref="PinchSource.Touchpad"/>) it is the mouse cursor, since
    /// touchpad touch coordinates are touchpad-relative and don't map to a screen location.</summary>
    public Action<float, float, float, PinchSource>? OnPinch { get; set; }

    /// <summary>Called when a pinch gesture ends (fingers lifted).</summary>
    public Action? OnPinchEnd { get; set; }

    /// <summary>Called on SDL TextInput. Parameter is the UTF-8 text string.</summary>
    public Action<string>? OnTextInput { get; set; }

    /// <summary>Called when a file is dropped onto this window. Parameter is the file path.</summary>
    public Action<string>? OnDropFile { get; set; }

    /// <summary>
    /// Called when this window's close button is clicked (SDL <c>WindowCloseRequested</c>). Multi-window
    /// consumers use this to close just this window (and quit when it's the last). When null, the close
    /// request is ignored here and an eventual SDL <c>Quit</c> drives shutdown via
    /// <see cref="SdlEventLoop.OnQuit"/> — preserving the single-window behavior.
    /// </summary>
    public Action? OnCloseRequested { get; set; }

    /// <summary>
    /// Called each loop iteration before checking redraw for this window. Return true to force a
    /// redraw (external state changes: background task completions, cursor blink, etc.).
    /// </summary>
    public Func<bool>? CheckNeedsRedraw { get; set; }

    /// <summary>
    /// Called (on the render thread, once per recovery storm) when this window's GPU work has had to
    /// recover from a fence stall / mid-frame error repeatedly in quick succession — i.e. the swapchain
    /// recovery isn't sticking because the workload keeps re-wedging the GPU. The consumer should SHED
    /// LOAD: switch to a cheap/safe view (e.g. a notifications tab) and reset whatever produced the
    /// runaway (e.g. clamp a runaway zoom) so the next frame is trivial and the GPU drains. Fires again
    /// only after a clean frame has ended the storm.
    /// </summary>
    public Action? OnRenderDegraded { get; set; }

    // --- per-window loop state (managed by SdlEventLoop) ---
    internal bool NeedsRedraw = true;
    internal float MouseX, MouseY;
    internal ulong LastMouseRedrawCounter;
    internal readonly Dictionary<long, (float X, float Y)> ActiveFingers = new();
    internal float PinchStartDist;

    // Swapchain-recovery storm tracking, per window (see SdlEventLoop.Run's catch).
    internal long LastRecoverTick;
    internal int RecoverStreak;
    internal uint LastRecoverW, LastRecoverH;
    // Set when OnRenderDegraded has fired for the current storm; cleared by a clean frame so the
    // load-shed request fires once per storm rather than every recovery iteration.
    internal bool RenderDegradedNotified;

    // GPU fence-timeout pacing, per window (see SdlEventLoop.RenderView's catch). While the
    // in-flight fence is late the loop retries rendering on a timestamp backoff instead of
    // blocking or sleeping, so SDL events keep pumping and the window stays responsive.
    // FenceStuckSinceTick anchors the escalation deadline (0 = not stuck); NextRenderAttemptTick
    // gates the next render attempt for BOTH the gentle retry and the recovery-storm backoff.
    internal long FenceStuckSinceTick;
    internal long NextRenderAttemptTick;

    /// <summary>Number of active touch fingers on this window. Use to suppress mouse drag during pinch.</summary>
    public int ActiveFingerCount => ActiveFingers.Count;

    /// <summary>Requests a redraw of this window on the next loop iteration.</summary>
    public void RequestRedraw() => NeedsRedraw = true;
}
