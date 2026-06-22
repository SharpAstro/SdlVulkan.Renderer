using DIR.Lib;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Maps SDL3 scancodes and key modifiers to the platform-agnostic
/// <see cref="InputKey"/> and <see cref="InputModifier"/> types from DIR.Lib.
/// </summary>
public static class SdlInputMapping
{
    extension(Scancode scancode)
    {
        public InputKey ToInputKey => scancode switch
        {
            Scancode.Up => InputKey.Up,
            Scancode.Down => InputKey.Down,
            Scancode.Left => InputKey.Left,
            Scancode.Right => InputKey.Right,
            Scancode.Home => InputKey.Home,
            Scancode.End => InputKey.End,
            Scancode.Pageup => InputKey.PageUp,
            Scancode.Pagedown => InputKey.PageDown,
            Scancode.Return => InputKey.Enter,
            Scancode.Escape => InputKey.Escape,
            Scancode.Tab => InputKey.Tab,
            Scancode.Space => InputKey.Space,
            Scancode.Backspace => InputKey.Backspace,
            Scancode.Delete => InputKey.Delete,
            >= Scancode.A and <= Scancode.Z => (InputKey)((int)InputKey.A + (scancode - Scancode.A)),
            // Alpha0 must come first: SDL orders the digit-row scancodes 1..9 then 0 (Alpha0 = Alpha9 + 1),
            // so folding it into the Alpha1..Alpha9 arithmetic would land on D9+1 == F1, not D0.
            Scancode.Alpha0 => InputKey.D0,
            >= Scancode.Alpha1 and <= Scancode.Alpha9 => (InputKey)((int)InputKey.D1 + (scancode - Scancode.Alpha1)),
            >= Scancode.F1 and <= Scancode.F12 => (InputKey)((int)InputKey.F1 + (scancode - Scancode.F1)),
            Scancode.Equals => InputKey.Plus,
            Scancode.Minus => InputKey.Minus,
            Scancode.Period => InputKey.Period,
            Scancode.Comma => InputKey.Comma,
            Scancode.Slash => InputKey.Slash,
            Scancode.Backslash => InputKey.Backslash,
            Scancode.Semicolon => InputKey.Semicolon,
            Scancode.Apostrophe => InputKey.Quote,
            Scancode.Grave => InputKey.Grave,
            _ => InputKey.None,
        };
    }

    extension(Keymod keymod)
    {
        public InputModifier ToInputModifier
        {
            get
            {
                var mod = InputModifier.None;
                if ((keymod & Keymod.Shift) != 0) mod |= InputModifier.Shift;
                if ((keymod & Keymod.Ctrl) != 0) mod |= InputModifier.Ctrl;
                if ((keymod & Keymod.Alt) != 0) mod |= InputModifier.Alt;
                return mod;
            }
        }
    }
}
