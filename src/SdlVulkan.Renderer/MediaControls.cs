using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>A button of the system's media controls (<see cref="MediaControls"/>): the media flyout's, the lock
/// screen's, or a media key on the keyboard.</summary>
public enum MediaButton
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
}

/// <summary>
/// A window's entry in the system's media controls, which show what the app plays and send it their buttons: on
/// Windows the media flyout (in quick settings, and beside the volume when it changes), the lock screen, and the
/// keyboard's media keys. Get it with <see cref="SdlVulkanWindow.TryGetMediaControls"/>; the presses arrive on the
/// event loop's thread, as <see cref="SdlWindowView.OnMediaButton"/>. Windows only for now. Call it on the window's
/// thread.
/// </summary>
/// <remarks>
/// <para>The entry stays out of the controls until the first <see cref="SetTrack"/>, and leaves them on
/// <see cref="Clear"/> or when the window goes. Play and pause are offered from the start; which of the two a play/pause
/// key sends follows <see cref="SetPlaying"/>, so keep that true to the media.</para>
/// <para>Windows names the entry after the app id of the window's process. An unpackaged app gets one, and the Start
/// menu shortcut that names it, from SharpAstro.AppShell's <c>AppIdentity.Claim</c>, called before the first window;
/// without one the entry is "Unknown app".</para>
/// <para>A web view that publishes its own entry (WebView2 does, for its page's media) puts a second one beside this:
/// SdlVulkan.Renderer.WebView's <c>NativeWebViewOptions.SystemMediaControls</c> turns that off.</para>
/// <para>Underneath are WinRT's SystemMediaTransportControls for the window's HWND, called by vtable slot rather than
/// through a WinRT projection, which would be megabytes for a dozen calls. The thumbnail goes over as a URL that
/// Windows reads itself, so no image is decoded in this process.</para>
/// </remarks>
public sealed unsafe partial class MediaControls : IDisposable
{
    private static readonly Guid IidInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid IidControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    private static readonly Guid IidUriFactory = new("44a9796f-723e-4fdf-a218-033e75b0c084");
    private static readonly Guid IidStreamReferenceStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");

    // ISystemMediaTransportControls, ISystemMediaTransportControlsDisplayUpdater and IMusicDisplayProperties: the
    // slots used here, after IUnknown's three and IInspectable's three.
    private const int PutPlaybackStatus = 7, GetDisplayUpdater = 8, PutIsEnabled = 11, PutIsPlayEnabled = 13,
        PutIsPauseEnabled = 17, PutIsPreviousEnabled = 25, PutIsNextEnabled = 27, AddButtonPressed = 32,
        RemoveButtonPressed = 33;
    private const int PutType = 7, PutThumbnail = 11, GetMusicProperties = 12, ClearAll = 16, Update = 17;
    private const int PutTitle = 7, PutArtist = 11;
    private const int MediaPlaybackTypeMusic = 1, MediaPlaybackStatusPlaying = 3, MediaPlaybackStatusPaused = 4;

    private nint _controls;
    private nint _updater;
    private long _token;
    private bool _enabled;

    /// <summary>The SDL event type a press arrives as (its window in <c>User.WindowID</c>, the button in
    /// <c>User.Code</c>); 0 until a window first has media controls.</summary>
    internal static uint EventType { get; private set; }

    private MediaControls(nint controls, nint updater)
    {
        _controls = controls;
        _updater = updater;
    }

    /// <summary>Shows a track in the controls: its title, its artist (or channel), and a picture of it, given as an
    /// <c>http</c>, <c>https</c> or <c>file</c> URL that Windows reads itself, or null for none.</summary>
    public void SetTrack(string title, string artist, string? thumbnailUrl)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(artist);
        if (_controls == 0)
            return;
        // The details are best effort: a call the system refuses leaves the entry as it was.
        Invoke(_updater, PutType, MediaPlaybackTypeMusic);
        nint music;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_updater, GetMusicProperties))(_updater, &music) >= 0)
        {
            SetString(music, PutTitle, title);
            SetString(music, PutArtist, artist);
            Release(music);
        }
        var thumbnail = thumbnailUrl is not null && Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out _)
            ? StreamReference(thumbnailUrl)
            : 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_updater, PutThumbnail))(_updater, thumbnail);
        Release(thumbnail);
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_updater, Update))(_updater);
        if (!_enabled)
        {
            _enabled = true;
            Invoke(_controls, PutIsEnabled, 1);
        }
    }

    /// <summary>Whether the media plays (true) or is paused. A play/pause key sends <see cref="MediaButton.Pause"/>
    /// while it plays and <see cref="MediaButton.Play"/> while it does not.</summary>
    public void SetPlaying(bool playing)
    {
        if (_controls != 0)
            Invoke(_controls, PutPlaybackStatus, playing ? MediaPlaybackStatusPlaying : MediaPlaybackStatusPaused);
    }

    /// <summary>Which of the previous and next buttons are offered. Neither is, at first.</summary>
    public void SetSkipEnabled(bool previous, bool next)
    {
        if (_controls == 0)
            return;
        Invoke(_controls, PutIsPreviousEnabled, previous ? 1 : 0);
        Invoke(_controls, PutIsNextEnabled, next ? 1 : 0);
    }

    /// <summary>Takes the entry out of the controls, until the next <see cref="SetTrack"/>: nothing is playing, nor
    /// about to.</summary>
    public void Clear()
    {
        if (_controls == 0 || !_enabled)
            return;
        _enabled = false;
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_updater, ClearAll))(_updater);
        Invoke(_controls, PutIsEnabled, 0);
    }

    /// <summary>Takes the entry out of the controls for good. The window does this when it goes.</summary>
    public void Dispose()
    {
        if (_controls == 0)
            return;
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(_controls, RemoveButtonPressed))(_controls, _token);
        Invoke(_controls, PutIsEnabled, 0);
        Release(_updater);
        Release(_controls);
        _controls = 0;
        _updater = 0;
    }

    /// <summary>The window's controls, or null when the system has none to give.</summary>
    [SupportedOSPlatform("windows")]
    internal static MediaControls? TryCreate(nint hwnd, uint windowId)
    {
        if (hwnd == 0)
            return null;
        // WinRT needs COM on this thread. A UI thread is normally a single-threaded apartment already ([STAThread],
        // which a WebView2 host must be); one that is nothing yet becomes one, and one that is multithreaded stays so.
        CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        if (!TryGetFactory("Windows.Media.SystemMediaTransportControls", IidInterop, out var interop))
            return null;
        nint controls = 0, updater = 0, handler = 0;
        try
        {
            var iid = IidControls;
            // ISystemMediaTransportControlsInterop::GetForWindow
            if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(interop, 6))(interop, hwnd, &iid, &controls) < 0
                || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(controls, GetDisplayUpdater))(controls, &updater) < 0)
            {
                Release(controls);
                return null;
            }
            if (EventType == 0)
                EventType = RegisterEvents(1);
            handler = ButtonHandler.Create(windowId);
            long token;
            if (EventType == 0
                || ((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(controls, AddButtonPressed))(controls, handler, &token) < 0)
            {
                Release(updater);
                Release(controls);
                return null;
            }
            // Out of the controls until there is a track: a window's controls start out enabled, an empty entry.
            Invoke(controls, PutIsEnabled, 0);
            Invoke(controls, PutIsPlayEnabled, 1);
            Invoke(controls, PutIsPauseEnabled, 1);
            return new MediaControls(controls, updater) { _token = token };
        }
        finally
        {
            Release(handler); // the controls hold their own reference while it is registered
            Release(interop);
        }
    }

    /// <summary>Whether <paramref name="evt"/> is a press of some window's media controls: the window, and the button.</summary>
    internal static bool IsPress(in Event evt, out uint windowId, out MediaButton button)
    {
        var isPress = EventType != 0 && evt.Type == EventType;
        windowId = isPress ? evt.User.WindowID : 0;
        button = isPress ? (MediaButton)evt.User.Code : default;
        return isPress;
    }

    /// <summary>The <see cref="MediaButton"/> for a SystemMediaTransportControlsButton, or null for one that is never
    /// offered here (record, fast forward, rewind, channel up and down).</summary>
    internal static MediaButton? FromSystemButton(int button) => button switch
    {
        0 => MediaButton.Play,
        1 => MediaButton.Pause,
        2 => MediaButton.Stop,
        6 => MediaButton.Next,
        7 => MediaButton.Previous,
        _ => null,
    };

    private static nint StreamReference(string url)
    {
        if (!TryGetFactory("Windows.Foundation.Uri", IidUriFactory, out var uriFactory))
            return 0;
        if (!TryGetFactory("Windows.Storage.Streams.RandomAccessStreamReference", IidStreamReferenceStatics, out var statics))
        {
            Release(uriFactory);
            return 0;
        }
        nint uri = 0, reference = 0;
        var h = CreateString(url);
        // IUriRuntimeClassFactory::CreateUri, then IRandomAccessStreamReferenceStatics::CreateFromUri
        if (h != 0 && ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(uriFactory, 6))(uriFactory, h, &uri) >= 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(statics, 7))(statics, uri, &reference);
        WindowsDeleteString(h);
        Release(uri);
        Release(statics);
        Release(uriFactory);
        return reference;
    }

    private static void SetString(nint target, int slot, string value)
    {
        var h = CreateString(value);
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(target, slot))(target, h);
        WindowsDeleteString(h);
    }

    private static bool TryGetFactory(string className, Guid iid, out nint factory)
    {
        nint f = 0;
        var h = CreateString(className);
        var ok = h != 0 && RoGetActivationFactory(h, &iid, &f) >= 0;
        WindowsDeleteString(h);
        factory = ok ? f : 0;
        return ok;
    }

    // An HSTRING; 0 for the empty string, which is what WinRT takes it to be.
    private static nint CreateString(string s)
    {
        nint h;
        fixed (char* p = s)
            return WindowsCreateString(p, (uint)s.Length, &h) >= 0 ? h : 0;
    }

    // A put_ of an enum or a boolean: both go over as a 32-bit value (a WinRT boolean is a byte, zero-extended by the
    // calling convention the same way).
    private static void Invoke(nint target, int slot, int value)
        => ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(target, slot))(target, value);

    private static void* Slot(nint unknown, int slot) => (*(void***)unknown)[slot];

    private static void Release(nint unknown)
    {
        if (unknown != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);
    }

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* s, uint length, nint* hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    /// <summary>
    /// The ButtonPressed handler (a <c>TypedEventHandler&lt;SystemMediaTransportControls,
    /// SystemMediaTransportControlsButtonPressedEventArgs&gt;</c>): a COM object of our own, IUnknown and Invoke, that
    /// turns each press into an SDL user event for its window. The controls call it on a thread of their own, so it
    /// says it is agile (callable from any thread), and touches nothing but SDL's thread-safe event queue.
    /// </summary>
    private struct ButtonHandler
    {
        private static readonly Guid IidHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
        private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid IidAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
        private static readonly void** Vtable = CreateVtable();

        private void** _vtable;
        private int _references;
        private uint _windowId;

        public static nint Create(uint windowId)
        {
            var handler = (ButtonHandler*)NativeMemory.Alloc((nuint)sizeof(ButtonHandler));
            handler->_vtable = Vtable;
            handler->_references = 1;
            handler->_windowId = windowId;
            return (nint)handler;
        }

        private static void** CreateVtable()
        {
            var vtable = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
            vtable[0] = (delegate* unmanaged[Stdcall]<ButtonHandler*, Guid*, void**, int>)&QueryInterface;
            vtable[1] = (delegate* unmanaged[Stdcall]<ButtonHandler*, uint>)&AddRef;
            vtable[2] = (delegate* unmanaged[Stdcall]<ButtonHandler*, uint>)&ReleaseHandler;
            vtable[3] = (delegate* unmanaged[Stdcall]<ButtonHandler*, nint, nint, int>)&Invoke;
            return vtable;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(ButtonHandler* self, Guid* iid, void** result)
        {
            if (*iid == IidHandler || *iid == IidUnknown || *iid == IidAgileObject)
            {
                Interlocked.Increment(ref self->_references);
                *result = self;
                return 0;
            }
            *result = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(ButtonHandler* self) => (uint)Interlocked.Increment(ref self->_references);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseHandler(ButtonHandler* self)
        {
            var references = Interlocked.Decrement(ref self->_references);
            if (references == 0)
                NativeMemory.Free(self);
            return (uint)references;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(ButtonHandler* self, nint sender, nint args)
        {
            int pressed; // ISystemMediaTransportControlsButtonPressedEventArgs::get_Button
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(args, 6))(args, &pressed) >= 0
                && FromSystemButton(pressed) is { } button)
            {
                var evt = default(Event);
                evt.User.Type = EventType;
                evt.User.WindowID = self->_windowId;
                evt.User.Code = (int)button;
                PushEvent(ref evt);
            }
            return 0;
        }
    }
}
