using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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
/// <para>Underneath are WinRT's SystemMediaTransportControls for the window's HWND, called through interfaces declared
/// for the COM source generator (MediaControls.Interop.cs) rather than through a WinRT projection, which would be
/// megabytes for a dozen calls. The thumbnail goes over as a URL that Windows reads itself, so no image is decoded in
/// this process.</para>
/// </remarks>
public sealed partial class MediaControls : IDisposable
{
    private const int MediaPlaybackTypeMusic = 1, MediaPlaybackStatusPlaying = 3, MediaPlaybackStatusPaused = 4;

    // Wraps what RoGetActivationFactory hands back; everything else is wrapped by the generated marshallers. Made on
    // first use, on Windows: ComWrappers does not exist on Android.
    private static StrategyBasedComWrappers? s_wrappers;

    private ISystemMediaTransportControls? _controls;
    private ISystemMediaTransportControlsDisplayUpdater? _updater;
    private long _token;
    private bool _enabled;

    /// <summary>The SDL event type a press arrives as (its window in <c>User.WindowID</c>, the button in
    /// <c>User.Code</c>); 0 until a window first has media controls.</summary>
    internal static uint EventType { get; private set; }

    private MediaControls(ISystemMediaTransportControls controls, ISystemMediaTransportControlsDisplayUpdater updater)
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
        // Only ever non-null on Windows; the platform check says so to the analyzer.
        if (_controls is null || _updater is null || !OperatingSystem.IsWindows())
            return;
        // The details are best effort: a call the system refuses leaves the entry as it was.
        _updater.put_Type(MediaPlaybackTypeMusic);
        if (_updater.get_MusicProperties(out var music) >= 0 && music is not null)
        {
            using (var h = new HString(title))
                music.put_Title(h.Handle);
            using (var h = new HString(artist))
                music.put_Artist(h.Handle);
            Release(music);
        }
        var thumbnail = thumbnailUrl is not null && Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out _)
            ? StreamReference(thumbnailUrl)
            : null;
        _updater.put_Thumbnail(thumbnail);
        Release(thumbnail);
        _updater.Update();
        if (!_enabled)
        {
            _enabled = true;
            _controls.put_IsEnabled(true);
        }
    }

    /// <summary>Whether the media plays (true) or is paused. A play/pause key sends <see cref="MediaButton.Pause"/>
    /// while it plays and <see cref="MediaButton.Play"/> while it does not.</summary>
    public void SetPlaying(bool playing)
        => _controls?.put_PlaybackStatus(playing ? MediaPlaybackStatusPlaying : MediaPlaybackStatusPaused);

    /// <summary>Which of the previous and next buttons are offered. Neither is, at first.</summary>
    public void SetSkipEnabled(bool previous, bool next)
    {
        if (_controls is null)
            return;
        _controls.put_IsPreviousEnabled(previous);
        _controls.put_IsNextEnabled(next);
    }

    /// <summary>Takes the entry out of the controls, until the next <see cref="SetTrack"/>: nothing is playing, nor
    /// about to.</summary>
    public void Clear()
    {
        if (_controls is null || _updater is null || !_enabled)
            return;
        _enabled = false;
        _updater.ClearAll();
        _controls.put_IsEnabled(false);
    }

    /// <summary>Takes the entry out of the controls for good. The window does this when it goes.</summary>
    public void Dispose()
    {
        if (_controls is null)
            return;
        _controls.remove_ButtonPressed(_token);
        _controls.put_IsEnabled(false);
        Release(_updater);
        Release(_controls);
        _controls = null;
        _updater = null;
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
        if (TryGetFactory<ISystemMediaTransportControlsInterop>("Windows.Media.SystemMediaTransportControls",
                ISystemMediaTransportControlsInterop.Iid) is not { } interop)
            return null;
        ISystemMediaTransportControls? controls = null;
        ISystemMediaTransportControlsDisplayUpdater? updater = null;
        try
        {
            if (interop.GetForWindow(hwnd, new Guid(ISystemMediaTransportControls.Iid), out controls) < 0 || controls is null
                || controls.get_DisplayUpdater(out updater) < 0 || updater is null)
            {
                Release(updater);
                Release(controls);
                return null;
            }
            if (EventType == 0)
                EventType = RegisterEvents(1);
            // The controls hold the handler's only reference while it is registered.
            if (EventType == 0 || controls.add_ButtonPressed(new ButtonHandler(windowId), out var token) < 0)
            {
                Release(updater);
                Release(controls);
                return null;
            }
            // Out of the controls until there is a track: a window's controls start out enabled, an empty entry.
            controls.put_IsEnabled(false);
            controls.put_IsPlayEnabled(true);
            controls.put_IsPauseEnabled(true);
            return new MediaControls(controls, updater) { _token = token };
        }
        finally
        {
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

    [SupportedOSPlatform("windows")]
    private static IRandomAccessStreamReference? StreamReference(string url)
    {
        if (TryGetFactory<IUriRuntimeClassFactory>("Windows.Foundation.Uri", IUriRuntimeClassFactory.Iid) is not { } uriFactory)
            return null;
        if (TryGetFactory<IRandomAccessStreamReferenceStatics>("Windows.Storage.Streams.RandomAccessStreamReference",
                IRandomAccessStreamReferenceStatics.Iid) is not { } statics)
        {
            Release(uriFactory);
            return null;
        }
        IUriRuntimeClass? uri = null;
        IRandomAccessStreamReference? reference = null;
        using (var h = new HString(url))
        {
            if (h.Handle != 0 && uriFactory.CreateUri(h.Handle, out uri) >= 0 && uri is not null)
                statics.CreateFromUri(uri, out reference);
        }
        Release(uri);
        Release(statics);
        Release(uriFactory);
        return reference;
    }

    // An activation factory (or statics) for a runtime class, as the interface asked for; null when the system has none.
    [SupportedOSPlatform("windows")]
    private static T? TryGetFactory<T>(string className, string iid) where T : class
    {
        using var h = new HString(className);
        var id = new Guid(iid);
        if (h.Handle == 0 || RoGetActivationFactory(h.Handle, in id, out var factory) < 0 || factory == 0)
            return null;
        // A unique wrapper of its own, which takes a reference of its own: Release drops it, and this one goes now.
        var wrapper = (s_wrappers ??= new()).GetOrCreateObjectForComInstance(factory, CreateObjectFlags.UniqueInstance);
        Marshal.Release(factory);
        return (T)wrapper;
    }

    // Drops the wrapper's reference now rather than at finalization. Every wrapper here is unique (see the interop
    // declarations), so no one else holds it.
    private static void Release(object? wrapper)
    {
        if (wrapper is ComObject com)
            com.FinalRelease();
    }

    // An HSTRING for the length of a using; 0 for the empty string, which is what WinRT takes it to be.
    private readonly ref struct HString
    {
        public readonly nint Handle;

        public HString(string s) => Handle = WindowsCreateString(s, (uint)s.Length, out var h) >= 0 ? h : 0;

        public void Dispose() => WindowsDeleteString(Handle);
    }

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string s, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    /// <summary>
    /// The ButtonPressed handler (a <c>TypedEventHandler&lt;SystemMediaTransportControls,
    /// SystemMediaTransportControlsButtonPressedEventArgs&gt;</c>), as a COM object the generator builds the vtable for,
    /// that turns each press into an SDL user event for its window. The controls call it on a thread of their own, so
    /// it says it is agile (callable from any thread), and touches nothing but SDL's thread-safe event queue.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class ButtonHandler(uint windowId) : IButtonPressedHandler, IAgileObject
    {
        public int Invoke(nint sender, ISystemMediaTransportControlsButtonPressedEventArgs? args)
        {
            if (args is null)
                return 0;
            if (args.get_Button(out var pressed) >= 0 && FromSystemButton(pressed) is { } button)
            {
                var evt = default(Event);
                evt.User.Type = EventType;
                evt.User.WindowID = windowId;
                evt.User.Code = (int)button;
                PushEvent(ref evt);
            }
            Release(args);
            return 0;
        }
    }
}
