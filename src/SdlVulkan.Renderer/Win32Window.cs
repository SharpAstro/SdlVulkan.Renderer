using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SdlVulkan.Renderer;

/// <summary>The few window-style and activation calls SDL has no API for, on Windows.</summary>
[SupportedOSPlatform("windows")]
internal static partial class Win32Window
{
    private const int GwlStyle = -16;
    private const long WsMaximizeBox = 0x00010000;

    public static bool IsForeground(nint hwnd) => hwnd != nint.Zero && GetForegroundWindow() == hwnd;

    public static void SetMaximizeBox(nint hwnd, bool enabled)
    {
        if (hwnd == nint.Zero)
            return;
        var style = (long)GetWindowLongPtrW(hwnd, GwlStyle);
        var updated = enabled ? style | WsMaximizeBox : style & ~WsMaximizeBox;
        if (updated != style)
            SetWindowLongPtrW(hwnd, GwlStyle, (nint)updated);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}
