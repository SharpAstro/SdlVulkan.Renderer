using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// SDL's Windows message hook (<c>SDL_SetWindowsMessageHook</c>), for the messages SDL has no event for: the press of a
/// global hotkey (<see cref="GlobalHotKeys"/>) and a double-click on a window's caption
/// (<see cref="SdlVulkanWindow.TryReportCaptionDoubleClicks"/>). The hook sees each message in the window thread's
/// queue ahead of SDL's own handling and turns these into SDL user events, so they wake a waiting event loop and are
/// handled on the loop's thread like any other event, not inside SDL's message pump.
/// </summary>
/// <remarks>The hook is one per process: the first registration that needs it installs it.</remarks>
internal static unsafe partial class WindowsMessageHook
{
    private const uint WmHotKey = 0x0312;
    private const uint WmNcLButtonDblClk = 0x00A3;
    private const nuint HtCaption = 2;

    // The windows whose caption double-clicks are reported: HWND to SDL window id. Read in the hook, on the window
    // thread; written by registration and dispose.
    private static readonly ConcurrentDictionary<nint, uint> CaptionWindows = new();

    /// <summary>The SDL event type a hotkey's press arrives as (its id in <c>User.Code</c>); 0 until the hook is
    /// installed.</summary>
    internal static uint HotKeyEvent { get; private set; }

    /// <summary>The SDL event type a caption double-click arrives as (its window in <c>User.WindowID</c>); 0 until the
    /// hook is installed.</summary>
    internal static uint CaptionDoubleClickEvent { get; private set; }

    [SupportedOSPlatform("windows")]
    internal static bool TryInstall()
    {
        if (HotKeyEvent != 0)
            return true;
        var first = RegisterEvents(2);
        if (first == 0)
            return false;
        HotKeyEvent = first;
        CaptionDoubleClickEvent = first + 1;
        // The hook is SDL's one Windows message hook for the process: this library takes it.
        SetWindowsMessageHook(&OnWindowsMessage, nint.Zero);
        return true;
    }

    [SupportedOSPlatform("windows")]
    internal static bool TryReportCaptionDoubleClicks(nint hwnd, uint windowId)
    {
        if (hwnd == nint.Zero || !TryInstall())
            return false;
        CaptionWindows[hwnd] = windowId;
        return true;
    }

    internal static void StopReportingCaptionDoubleClicks(nint hwnd) => CaptionWindows.TryRemove(hwnd, out _);

    // Every message SDL takes from the window thread's queue passes through here before SDL handles it. Keep it to
    // a comparison or two for everything else: it runs for each mouse move.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnWindowsMessage(nint userdata, Msg* msg)
    {
        if (msg->Message == WmHotKey)
        {
            Push(HotKeyEvent, windowId: 0, code: (int)msg->WParam); // the id it was registered with
            return 0; // SDL has no use for it
        }
        // A double-click on a caption (WParam is where it landed, the hit test's answer). Windows' own handling
        // goes ahead: it maximizes a window that may maximize.
        if (msg->Message == WmNcLButtonDblClk && msg->WParam == HtCaption
            && CaptionWindows.TryGetValue(msg->Hwnd, out var windowId))
            Push(CaptionDoubleClickEvent, windowId, code: 0);
        return 1;
    }

    private static void Push(uint type, uint windowId, int code)
    {
        var evt = default(Event);
        evt.User.Type = type;
        evt.User.WindowID = windowId;
        evt.User.Code = code;
        PushEvent(ref evt);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    // SDL3-CS binds the hook as a marshalled delegate; a function pointer to an UnmanagedCallersOnly method needs no
    // marshalling under native AOT and nothing the GC could collect while SDL holds it.
    [LibraryImport("SDL3", EntryPoint = "SDL_SetWindowsMessageHook")]
    private static partial void SetWindowsMessageHook(delegate* unmanaged[Cdecl]<nint, Msg*, byte> callback, nint userdata);
}
