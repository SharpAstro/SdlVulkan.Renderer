using DIR.Lib;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Reusable SDL3+Vulkan event loop with idle suppression, swapchain recovery,
/// and font atlas dirty handling. Provides typed callbacks for input events.
/// <para>
/// Handles internally: quit, window resize (calls <see cref="VkRenderer.Resize"/>),
/// window expose, F11 fullscreen toggle, font atlas dirty scheduling.
/// </para>
/// </summary>
public sealed class SdlEventLoop(SdlVulkanWindow window, VkRenderer renderer)
{
    private bool _needsRedraw = true;
    private bool _running;

    /// <summary>Background color used for <see cref="VkRenderer.BeginFrame"/>.</summary>
    public RGBAColor32 BackgroundColor { get; set; } = new(0x1a, 0x1a, 0x2e, 0xff);

    /// <summary>Called each frame between BeginFrame and EndFrame.</summary>
    public Action? OnRender { get; set; }

    /// <summary>Called after the renderer is resized. Parameters are the new pixel dimensions.</summary>
    public Action<uint, uint>? OnResize { get; set; }

    /// <summary>Called on key down. Parameters are the mapped InputKey and InputModifier. Return true if consumed.</summary>
    public Func<InputKey, InputModifier, bool>? OnKeyDown { get; set; }

    /// <summary>Called on left mouse button down. Parameters are pixel coordinates. Return true if consumed.</summary>
    public Func<float, float, bool>? OnMouseDown { get; set; }

    /// <summary>Called on mouse motion. Parameters are pixel coordinates.</summary>
    public Action<float, float>? OnMouseMove { get; set; }

    /// <summary>Called on mouse button up.</summary>
    public Action? OnMouseUp { get; set; }

    /// <summary>Called on mouse wheel. Parameters are scrollY, mouseX, mouseY. Return true if consumed.</summary>
    public Func<float, float, float, bool>? OnMouseWheel { get; set; }

    /// <summary>Called on SDL TextInput event. Parameter is the UTF-8 text string.</summary>
    public Action<string>? OnTextInput { get; set; }

    /// <summary>
    /// Called each iteration before checking <c>needsRedraw</c>. Return true to force a redraw.
    /// Use for external state changes (e.g., game display updates, background task completions, cursor blink).
    /// </summary>
    public Func<bool>? CheckNeedsRedraw { get; set; }

    /// <summary>Called after EndFrame completes. Use for post-frame work (e.g., background task processing, state cleanup).</summary>
    public Action? OnPostFrame { get; set; }

    /// <summary>Requests a redraw on the next iteration.</summary>
    public void RequestRedraw() => _needsRedraw = true;

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
            Event evt;
            var hadEvent = _needsRedraw
                ? PollEvent(out evt)
                : WaitEventTimeout(out evt, 16);

            if (hadEvent)
            {
                do
                {
                    switch ((EventType)evt.Type)
                    {
                        case EventType.Quit:
                            _running = false;
                            break;

                        case EventType.WindowResized:
                        case EventType.WindowPixelSizeChanged:
                            window.GetSizeInPixels(out var rw, out var rh);
                            if (rw > 0 && rh > 0)
                            {
                                renderer.Resize((uint)rw, (uint)rh);
                                OnResize?.Invoke((uint)rw, (uint)rh);
                            }
                            _needsRedraw = true;
                            break;

                        case EventType.WindowExposed:
                            _needsRedraw = true;
                            break;

                        case EventType.KeyDown:
                            var inputKey = evt.Key.Scancode.ToInputKey;
                            var inputMod = evt.Key.Mod.ToInputModifier;

                            if (inputKey == InputKey.F11)
                            {
                                window.ToggleFullscreen();
                            }
                            else
                            {
                                OnKeyDown?.Invoke(inputKey, inputMod);
                            }
                            _needsRedraw = true;
                            break;

                        case EventType.MouseButtonDown:
                            if (evt.Button.Button == 1)
                            {
                                OnMouseDown?.Invoke(evt.Button.X, evt.Button.Y);
                                _needsRedraw = true;
                            }
                            break;

                        case EventType.MouseButtonUp:
                            OnMouseUp?.Invoke();
                            break;

                        case EventType.MouseMotion:
                            OnMouseMove?.Invoke(evt.Motion.X, evt.Motion.Y);
                            _needsRedraw = true;
                            break;

                        case EventType.MouseWheel:
                            OnMouseWheel?.Invoke(evt.Wheel.Y, 0, 0);
                            _needsRedraw = true;
                            break;

                        case EventType.TextInput:
                            if (OnTextInput is not null)
                            {
                                var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(evt.Text.Text);
                                if (text is not null)
                                {
                                    OnTextInput(text);
                                    _needsRedraw = true;
                                }
                            }
                            break;
                    }
                } while (_running && PollEvent(out evt));
            }

            if (CheckNeedsRedraw?.Invoke() == true)
                _needsRedraw = true;

            if (!_needsRedraw || !_running)
                continue;
            _needsRedraw = false;

            if (!renderer.BeginFrame(BackgroundColor))
            {
                window.GetSizeInPixels(out var sw, out var sh);
                if (sw > 0 && sh > 0)
                {
                    renderer.Resize((uint)sw, (uint)sh);
                    OnResize?.Invoke((uint)sw, (uint)sh);
                }
                _needsRedraw = true;
                continue;
            }

            OnRender?.Invoke();

            renderer.EndFrame();

            if (renderer.FontAtlasDirty)
                _needsRedraw = true;

            OnPostFrame?.Invoke();
        }
    }
}
