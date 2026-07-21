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

    /// <summary>
    /// Unified pointer callback: the loop synthesizes a DIR.Lib <see cref="InputEvent"/> for mouse
    /// down / move / up / wheel and delivers it here, so consumers wire ONE handler instead of four
    /// lambdas. The synthesis owns the per-event details every hand-wired consumer had to rediscover:
    /// <see cref="InputEvent.MouseUp"/> carries the REAL release coordinates (SDL's button-up event has
    /// X/Y; the legacy <see cref="OnMouseUp"/> signature drops them, which is how a consumer once
    /// shipped <c>MouseUp(0, 0)</c> and broke every position-dependent release check), the SDL button
    /// byte is mapped to <see cref="MouseButton"/>, and down/wheel carry the live keyboard modifiers.
    /// Return true if consumed (drives the same redraw semantics as the per-event callbacks). Fires
    /// independently of the legacy per-event callbacks — a consumer wires one style or the other.
    /// Keyboard/text stay on <see cref="OnKeyDown"/>/<see cref="OnTextInput"/> (they carry app-level
    /// chords like quit/fullscreen and have no synthesis pitfalls).
    /// </summary>
    public Func<InputEvent, bool>? OnPointerInput { get; set; }

    // ---------------------------------------------------------------------------------------------
    // Pointer dispatch: the ONE place a pointer event (real SDL or inspector-synthesized) fans out
    // to the legacy per-event callback AND the unified OnPointerInput. SdlEventLoop's pump and
    // DebugInspector both route through these, so synthesized input can never drift from real input.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Fan a button press out to <see cref="OnMouseDown"/> + <see cref="OnPointerInput"/>. Returns consumed.</summary>
    internal bool DispatchPointerDown(byte sdlButton, float x, float y, byte clicks, InputModifier mods)
    {
        var consumed = OnMouseDown?.Invoke(sdlButton, x, y, clicks, mods) == true;
        if (OnPointerInput?.Invoke(new InputEvent.MouseDown(x, y, ToMouseButton(sdlButton), mods, clicks)) == true)
        {
            consumed = true;
        }
        return consumed;
    }

    /// <summary>Fan a pointer move out to <see cref="OnMouseMove"/> + <see cref="OnPointerInput"/>. Returns consumed.</summary>
    internal bool DispatchPointerMove(float x, float y)
    {
        var consumed = OnMouseMove?.Invoke(x, y) == true;
        if (OnPointerInput?.Invoke(new InputEvent.MouseMove(x, y)) == true)
        {
            consumed = true;
        }
        return consumed;
    }

    /// <summary>
    /// Fan a button release out to <see cref="OnMouseUp"/> + <see cref="OnPointerInput"/>. The release
    /// coordinates are real (SDL's button-up event carries X/Y; only the legacy <see cref="OnMouseUp"/>
    /// signature drops them). Returns consumed (legacy <see cref="OnMouseUp"/> is void, so only
    /// <see cref="OnPointerInput"/> can consume).
    /// </summary>
    internal bool DispatchPointerUp(byte sdlButton, float x, float y)
    {
        OnMouseUp?.Invoke(sdlButton);
        return OnPointerInput?.Invoke(new InputEvent.MouseUp(x, y, ToMouseButton(sdlButton))) == true;
    }

    /// <summary>Fan a wheel tick out to <see cref="OnMouseWheel"/> + <see cref="OnPointerInput"/>. Returns consumed.</summary>
    internal bool DispatchPointerWheel(float scrollY, float x, float y, InputModifier mods)
    {
        var consumed = OnMouseWheel?.Invoke(scrollY, x, y) == true;
        if (OnPointerInput?.Invoke(new InputEvent.Scroll(scrollY, x, y, mods)) == true)
        {
            consumed = true;
        }
        return consumed;
    }

    /// <summary>SDL button byte (1=left, 2=middle, 3=right) to the DIR.Lib button enum; side buttons map to Left.</summary>
    internal static MouseButton ToMouseButton(byte sdlButton) => sdlButton switch
    {
        2 => MouseButton.Middle,
        3 => MouseButton.Right,
        _ => MouseButton.Left,
    };

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

    /// <summary>
    /// Called (on the render thread, at most once) when this window's GPU is wedged beyond recovery:
    /// the in-flight fence stayed stuck past the escalation window AND the sacrificial recovery
    /// attempt did not complete within its deadline — i.e. the driver itself is blocking inside
    /// teardown calls (the observed Adreno failure: vkFreeMemory never returns while the GPU spins
    /// at 100% on a hung submission). The event loop stops after this fires. The consumer should
    /// persist session state and exit/relaunch; there is no in-process way back from a hung device.
    /// </summary>
    public Action? OnGpuWedged { get; set; }

    // --- per-window loop state (managed by SdlEventLoop) ---
    internal bool NeedsRedraw = true;

    // Android app lifecycle (see SdlEventLoop.Dispatch/RenderView): the native surface is destroyed
    // when the app is backgrounded and handed back fresh on foreground. Paused skips all rendering
    // while backgrounded (never present to a dead surface); NeedsSurfaceRebuild makes the first frame
    // after returning rebuild the swapchain against the new surface, then resume.
    internal bool Paused;
    internal bool NeedsSurfaceRebuild;
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

    // Sacrificial GPU-error recovery, per window (see SdlEventLoop.RenderView). When the fence is
    // known stuck the recovery teardown runs on a background task instead of the render thread —
    // on a truly hung GPU the driver can block INSIDE vkDestroy*/vkFreeMemory indefinitely, and a
    // blocked background task is abandonable (thread leak) where a blocked render thread is a
    // frozen window. Deadline exceeded => OnGpuWedged + clean loop stop.
    internal Task? GpuRecoveryTask;
    internal long GpuRecoveryDeadlineTick;
    // Stuck-fence escalations since the last clean frame. Bounds the stuck→recover→stuck ping-pong:
    // at SdlEventLoop.GpuStuckEscalationLimit the device is declared wedged (OnGpuWedged + stop).
    internal int StuckEscalations;

    /// <summary>Number of active touch fingers on this window. Use to suppress mouse drag during pinch.</summary>
    public int ActiveFingerCount => ActiveFingers.Count;

    /// <summary>Requests a redraw of this window on the next loop iteration.</summary>
    public void RequestRedraw() => NeedsRedraw = true;
}
