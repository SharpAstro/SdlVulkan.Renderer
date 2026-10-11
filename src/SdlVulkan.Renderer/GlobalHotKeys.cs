using System.Runtime.Versioning;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Key combinations that reach the app whichever app is active (<see cref="SdlVulkanWindow.TryRegisterGlobalHotKey"/>),
/// which SDL has no API for. On Windows a press arrives as <c>WM_HOTKEY</c> in the window thread's queue; SDL's
/// Windows message hook sees it there, ahead of SDL's own handling, and turns it into an SDL user event, so it wakes
/// a waiting event loop and is handled on the loop's thread like any other event, not inside SDL's message pump
/// (<see cref="WindowsMessageHook"/>).
/// </summary>
internal static class GlobalHotKeys
{
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;
    private const uint ScancodeMask = 1u << 30; // SDLK_SCANCODE_MASK: a keycode with no character

    /// <summary>The SDL event type a press arrives as; 0 until the first registration installs the hook.</summary>
    internal static uint EventType => WindowsMessageHook.HotKeyEvent;

    [SupportedOSPlatform("windows")]
    internal static bool TryRegister(nint hwnd, int id, Keymod modifiers, Keycode key)
    {
        if (hwnd == nint.Zero || !TryResolve(modifiers, key, out var mods, out var vk) || !WindowsMessageHook.TryInstall())
            return false;
        // NOREPEAT: holding the keys down is one press, not a stream of them.
        return Win32Window.RegisterHotKey(hwnd, id, mods | ModNoRepeat, vk);
    }

    [SupportedOSPlatform("windows")]
    internal static void Unregister(nint hwnd, int id) => Win32Window.UnregisterHotKey(hwnd, id);

    /// <summary>
    /// The Windows modifiers and virtual key for a combination. A character is looked up on the keyboard layout
    /// active now, and the modifiers it needs there are added: '/' is a key of its own on a US layout and Shift+7
    /// on a German one. Of the keys with no character, F1 to F24.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryResolve(Keymod modifiers, Keycode key, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if ((modifiers & Keymod.Alt) != 0) mods |= ModAlt;
        if ((modifiers & Keymod.Ctrl) != 0) mods |= ModControl;
        if ((modifiers & Keymod.Shift) != 0) mods |= ModShift;
        if ((modifiers & Keymod.GUI) != 0) mods |= ModWin;

        var code = (uint)key;
        if ((code & ScancodeMask) == 0)
        {
            if (code is 0 or > 0xFFFF)
                return false;
            var scan = Win32Window.VkKeyScanW((ushort)code);
            if (scan == -1)
                return false; // no key on this layout types it
            vk = (uint)(scan & 0xFF);
            var shiftState = (scan >> 8) & 0xFF;
            if ((shiftState & 1) != 0) mods |= ModShift;
            if ((shiftState & 2) != 0) mods |= ModControl;
            if ((shiftState & 4) != 0) mods |= ModAlt;
            return true;
        }
        var scancode = code & ~ScancodeMask;
        if (scancode is >= (uint)Scancode.F1 and <= (uint)Scancode.F12)
            vk = 0x70 + (scancode - (uint)Scancode.F1); // VK_F1
        else if (scancode is >= (uint)Scancode.F13 and <= (uint)Scancode.F24)
            vk = 0x7C + (scancode - (uint)Scancode.F13); // VK_F13
        return vk != 0;
    }
}
