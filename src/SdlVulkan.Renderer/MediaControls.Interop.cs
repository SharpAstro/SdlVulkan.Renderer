using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SdlVulkan.Renderer;

// The WinRT interfaces MediaControls calls, declared for the COM source generator ([GeneratedComInterface]), which
// writes the vtable calls -- and, for ButtonHandler ([GeneratedComClass]), the vtable -- from these declarations. That
// is AOT-safe and takes no WinRT projection. The generator knows nothing of WinRT, so:
//
// - Each interface lists its methods in the order of the SDK header that declares it (winrt\windows.media.h,
//   windows.foundation.h, windows.storage.streams.h; um\SystemMediaTransportControlsInterop.h): the declaration order
//   IS the vtable, so a method never called is still declared when one after it is. Each stops at the last one called.
// - IInspectable is declared here as the base of every WinRT interface, for its three slots after IUnknown's.
// - An HSTRING goes over as an nint (see HString), a WinRT boolean as a one-byte bool, an enum as an int, an
//   EventRegistrationToken as a long.
// - Every interface a method hands back is marshalled as a UNIQUE wrapper, so Release can drop its reference at once
//   (ComObject.FinalRelease) instead of whenever a finalizer runs, without pulling a wrapper out from under anyone
//   else that holds the same object.
public sealed partial class MediaControls
{
    [GeneratedComInterface]
    [Guid("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90")]
    internal partial interface IInspectable
    {
        [PreserveSig] int GetIids(out uint count, out nint iids);
        [PreserveSig] int GetRuntimeClassName(out nint className);
        [PreserveSig] int GetTrustLevel(out int trustLevel);
    }

    [GeneratedComInterface]
    [Guid(Iid)]
    internal partial interface ISystemMediaTransportControlsInterop : IInspectable
    {
        public const string Iid = "ddb0472d-c911-4a1f-86d9-dc3d71a95f5a";

        [PreserveSig]
        int GetForWindow(nint appWindow, in Guid iid,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<ISystemMediaTransportControls>))] out ISystemMediaTransportControls? controls);
    }

    [GeneratedComInterface]
    [Guid(Iid)]
    internal partial interface ISystemMediaTransportControls : IInspectable
    {
        public const string Iid = "99fa3ff4-1742-42a6-902e-087d41f965ec";

        [PreserveSig] int get_PlaybackStatus(out int value);
        [PreserveSig] int put_PlaybackStatus(int value);
        [PreserveSig]
        int get_DisplayUpdater(
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<ISystemMediaTransportControlsDisplayUpdater>))] out ISystemMediaTransportControlsDisplayUpdater? value);
        [PreserveSig] int get_SoundLevel(out int value);
        [PreserveSig] int get_IsEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsPlayEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsPlayEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsStopEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsStopEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsPauseEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsPauseEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsRecordEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsRecordEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsFastForwardEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsFastForwardEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsRewindEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsRewindEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsPreviousEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsPreviousEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsNextEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsNextEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsChannelUpEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsChannelUpEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int get_IsChannelDownEnabled([MarshalAs(UnmanagedType.U1)] out bool value);
        [PreserveSig] int put_IsChannelDownEnabled([MarshalAs(UnmanagedType.U1)] bool value);
        [PreserveSig] int add_ButtonPressed(IButtonPressedHandler handler, out long token);
        [PreserveSig] int remove_ButtonPressed(long token);
    }

    [GeneratedComInterface]
    [Guid("8abbc53e-fa55-4ecf-ad8e-c984e5dd1550")]
    internal partial interface ISystemMediaTransportControlsDisplayUpdater : IInspectable
    {
        [PreserveSig] int get_Type(out int value);
        [PreserveSig] int put_Type(int value);
        [PreserveSig] int get_AppMediaId(out nint value);
        [PreserveSig] int put_AppMediaId(nint value);
        [PreserveSig]
        int get_Thumbnail(
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IRandomAccessStreamReference>))] out IRandomAccessStreamReference? value);
        [PreserveSig] int put_Thumbnail(IRandomAccessStreamReference? value);
        [PreserveSig]
        int get_MusicProperties(
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IMusicDisplayProperties>))] out IMusicDisplayProperties? value);
        [PreserveSig] int get_VideoProperties(out nint value);          // never called: a slot to skip
        [PreserveSig] int get_ImageProperties(out nint value);          // never called: a slot to skip
        [PreserveSig] int CopyFromFileAsync(int type, nint source, out nint operation); // never called: a slot to skip
        [PreserveSig] int ClearAll();
        [PreserveSig] int Update();
    }

    [GeneratedComInterface]
    [Guid("6bbf0c59-d0a0-4d26-92a0-f978e1d18e7b")]
    internal partial interface IMusicDisplayProperties : IInspectable
    {
        [PreserveSig] int get_Title(out nint value);
        [PreserveSig] int put_Title(nint value);
        [PreserveSig] int get_AlbumArtist(out nint value);
        [PreserveSig] int put_AlbumArtist(nint value);
        [PreserveSig] int get_Artist(out nint value);
        [PreserveSig] int put_Artist(nint value);
    }

    [GeneratedComInterface]
    [Guid("b7f47116-a56f-4dc8-9e11-92031f4a87c2")]
    internal partial interface ISystemMediaTransportControlsButtonPressedEventArgs : IInspectable
    {
        [PreserveSig] int get_Button(out int value);
    }

    // TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>: a WinRT
    // delegate, so IUnknown-based, with Invoke at slot 3. The IID is the parameterised one for those two arguments.
    [GeneratedComInterface]
    [Guid("0557e996-7b23-5bae-aa81-ea0d671143a4")]
    internal partial interface IButtonPressedHandler
    {
        [PreserveSig]
        int Invoke(nint sender,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<ISystemMediaTransportControlsButtonPressedEventArgs>))] ISystemMediaTransportControlsButtonPressedEventArgs? args);
    }

    // No methods: answering a QueryInterface for it is the promise that the object may be called from any thread.
    [GeneratedComInterface]
    [Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
    internal partial interface IAgileObject
    {
    }

    [GeneratedComInterface]
    [Guid("9e365e57-48b2-4160-956f-c7385120bbfc")]
    internal partial interface IUriRuntimeClass : IInspectable
    {
        // Only ever passed on (to CreateFromUri), never called: the declaration is for the IID.
    }

    [GeneratedComInterface]
    [Guid(Iid)]
    internal partial interface IUriRuntimeClassFactory : IInspectable
    {
        public const string Iid = "44a9796f-723e-4fdf-a218-033e75b0c084";

        [PreserveSig]
        int CreateUri(nint uri, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IUriRuntimeClass>))] out IUriRuntimeClass? instance);
    }

    [GeneratedComInterface]
    [Guid("33ee3134-1dd6-4e3a-8067-d1c162e8642b")]
    internal partial interface IRandomAccessStreamReference : IInspectable
    {
        // Only ever passed on (to put_Thumbnail), never called: the declaration is for the IID.
    }

    [GeneratedComInterface]
    [Guid(Iid)]
    internal partial interface IRandomAccessStreamReferenceStatics : IInspectable
    {
        public const string Iid = "857309dc-3fbf-4e7d-986f-ef3b1a07a964";

        [PreserveSig] int CreateFromFile(nint file, out nint streamReference);   // never called: a slot to skip
        [PreserveSig]
        int CreateFromUri(IUriRuntimeClass? uri,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IRandomAccessStreamReference>))] out IRandomAccessStreamReference? streamReference);
    }
}
