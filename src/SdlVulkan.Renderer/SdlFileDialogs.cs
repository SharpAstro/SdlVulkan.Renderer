using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DIR.Lib;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// DIR.Lib's <see cref="IFileDialogs"/> over SDL's dialogs: the platform's own open, save and folder dialogs
/// (the common item dialog on Windows, the desktop portal or zenity on Linux, NSOpenPanel on macOS), shown
/// without blocking the thread that asked and answered on the event loop's thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Call it on the main thread</b>, which is the event loop's: SDL requires it. SDL may answer on another
/// thread, so the answer is copied there and handed back as an SDL user event, and the task completes when the
/// loop handles that event. <see cref="SdlEventLoop"/> does this itself; a host with its own loop passes each
/// event to <see cref="TryDeliver"/>. A continuation that does not leave the loop's thread (an <c>await</c>
/// with no synchronization context, as there is none on an SDL loop) therefore runs on it.
/// </para>
/// <para>
/// <b>Nothing is marshalled.</b> SDL keeps the filter array until it calls back, which SDL3-CS's
/// <c>DialogFileFilter[]</c> overload does not guarantee (it frees what it marshalled when the call returns), and
/// an asynchronous callback through a marshalled delegate has to be kept alive by hand. So the filters, their
/// strings and the properties live in native memory until the callback, the callback is an
/// <see cref="UnmanagedCallersOnlyAttribute"/> function pointer, and the request travels as a
/// <see cref="GCHandle"/>. That is also what native AOT needs.
/// </para>
/// <para>
/// A cancelled token completes the task as cancelled at once. SDL has no way to close a dialog it has shown,
/// so the dialog stays up, and whatever it answers is dropped.
/// </para>
/// </remarks>
/// <param name="owner">The window the dialog belongs to, which it is modal for where the platform allows; null
/// for none.</param>
public sealed unsafe partial class SdlFileDialogs(SdlVulkanWindow? owner) : IFileDialogs
{
    // SDL_FileDialogType.
    internal const int OpenFile = 0, SaveFile = 1, OpenFolder = 2;

    /// <summary>The SDL event type an answer arrives as; 0 until the first dialog asks SDL for one.</summary>
    private static uint s_eventType;

    /// <inheritdoc />
    public Task<FileDialogResult> OpenAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
        => Show(OpenFile, request, cancellationToken);

    /// <inheritdoc />
    public Task<FileDialogResult> SaveAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
        => Show(SaveFile, request, cancellationToken);

    /// <inheritdoc />
    public Task<FileDialogResult> OpenFolderAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
        => Show(OpenFolder, request, cancellationToken);

    /// <summary>
    /// For a host that runs its own SDL event loop: completes the dialog <paramref name="evt"/> answers, and says
    /// whether it was one. <see cref="SdlEventLoop"/> does this itself.
    /// </summary>
    public static bool TryDeliver(in Event evt)
    {
        if (s_eventType == 0 || evt.Type != s_eventType)
        {
            return false;
        }

        var handle = GCHandle.FromIntPtr(evt.User.Data1);
        var pending = (Pending)handle.Target!;
        handle.Free();
        pending.Complete();
        return true;
    }

    private Task<FileDialogResult> Show(int kind, FileDialogRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<FileDialogResult>(cancellationToken);
        }

        if (s_eventType == 0)
        {
            var type = RegisterEvents(1);
            if (type == 0)
            {
                return Task.FromResult(new FileDialogResult { Error = $"No SDL event type for the answer: {GetError()}" });
            }

            s_eventType = type;
        }

        var filters = kind == OpenFolder ? [] : request.Filters;
        var pending = new Pending(kind, filters, cancellationToken);
        var props = CreateProperties();
        pending.Properties = props;

        if (filters.Count > 0)
        {
            pending.NativeFilters = (NativeFilter*)NativeMemory.AllocZeroed((nuint)filters.Count, (nuint)sizeof(NativeFilter));
            for (var i = 0; i < filters.Count; i++)
            {
                pending.NativeFilters[i].Name = Utf8(filters[i].Name);
                pending.NativeFilters[i].Pattern = Utf8(Pattern(filters[i]));
            }

            SetPointerProperty(props, "SDL.filedialog.filters", (nint)pending.NativeFilters);
            SetNumberProperty(props, "SDL.filedialog.nfilters", filters.Count);
        }

        if (owner is { Handle: not 0 } window)
        {
            SetPointerProperty(props, "SDL.filedialog.window", window.Handle);
        }

        if (Location(kind, request) is { } location)
        {
            SetStringProperty(props, "SDL.filedialog.location", location);
        }

        if (request.Title is { Length: > 0 } title)
        {
            SetStringProperty(props, "SDL.filedialog.title", title);
        }

        if (kind == OpenFile && request.AllowMany)
        {
            SetBooleanProperty(props, "SDL.filedialog.many", true);
        }

        var handle = GCHandle.Alloc(pending);
        ShowFileDialogWithProperties(kind, &OnAnswer, GCHandle.ToIntPtr(handle), props);
        return pending.Task;
    }

    /// <summary>A filter as SDL's pattern: the extensions joined by semicolons, without dots, or "*".</summary>
    internal static string Pattern(FileDialogFilter filter)
    {
        var pattern = string.Join(';', filter.ExtensionList);
        return pattern.Length == 0 ? "*" : pattern;
    }

    /// <summary>
    /// Where the dialog starts. SDL takes one location, a folder or a file: a save dialog is given the suggested
    /// name inside the start folder, so the name field is filled in; the others the folder alone.
    /// </summary>
    /// <remarks>
    /// A folder is given a trailing separator, which is how SDL tells one from a file. Without it the Windows
    /// dialog took the last component for a file name and opened in the folder ABOVE: asked to start in
    /// <c>…\Tests\Assets</c>, it showed <c>…\Tests</c> with "Assets" in the name box.
    /// </remarks>
    internal static string? Location(int kind, FileDialogRequest request)
    {
        var folder = request.InitialDirectory is { Length: > 0 } dir
            ? Path.EndsInDirectorySeparator(dir) ? dir : dir + Path.DirectorySeparatorChar
            : null;
        if (kind == SaveFile && request.SuggestedFileName is { Length: > 0 } name)
        {
            return folder is null ? name : Path.Combine(folder, name);
        }

        return folder;
    }

    private static byte* Utf8(string s)
    {
        var bytes = Encoding.UTF8.GetByteCount(s);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes + 1);
        Encoding.UTF8.GetBytes(s, new Span<byte>(p, bytes));
        p[bytes] = 0;
        return p;
    }

    // SDL calls this once per dialog, from whichever thread the platform answers on: on Windows the dialog's own
    // thread, on Linux the one pumping D-Bus. It copies what SDL lends it, frees what SDL no longer needs, and
    // hands the answer to the loop; nothing here touches the caller's state.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAnswer(nint userdata, byte** files, int filter)
    {
        var pending = (Pending)GCHandle.FromIntPtr(userdata).Target!;
        if (files is null)
        {
            pending.Answer = new FileDialogResult { Error = GetError(), FilterIndex = filter };
        }
        else
        {
            var paths = new List<string>();
            for (var i = 0; files[i] is not null; i++)
            {
                var path = Marshal.PtrToStringUTF8((nint)files[i]) ?? "";
                paths.Add(pending.Kind == SaveFile ? FileDialogFilter.WithDefaultExtension(path, pending.Filters, filter) : path);
            }

            pending.Answer = new FileDialogResult { Paths = paths, FilterIndex = filter };
        }

        pending.FreeNative();

        var evt = default(Event);
        evt.User.Type = s_eventType;
        evt.User.Data1 = userdata;
        PushEvent(ref evt);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFilter
    {
        public byte* Name;
        public byte* Pattern;
    }

    /// <summary>One dialog in flight: what it was asked, what it answered, and the native memory it holds until SDL
    /// lets go of it.</summary>
    private sealed class Pending
    {
        // Not RunContinuationsAsynchronously: completing on the loop's thread is the point, and a continuation that
        // stays there is what makes the answer safe to act on without a lock.
        private readonly TaskCompletionSource<FileDialogResult> _completion = new();
        private readonly CancellationTokenRegistration _cancellation;

        public Pending(int kind, IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken)
        {
            Kind = kind;
            Filters = filters;
            _cancellation = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state =>
                {
                    var (completion, token) = ((TaskCompletionSource<FileDialogResult>, CancellationToken))state!;
                    completion.TrySetCanceled(token);
                }, (_completion, cancellationToken))
                : default;
        }

        public int Kind { get; }

        public IReadOnlyList<FileDialogFilter> Filters { get; }

        public Task<FileDialogResult> Task => _completion.Task;

        public uint Properties { get; set; }

        public NativeFilter* NativeFilters { get; set; }

        /// <summary>Written by the callback's thread before it pushes the event, read on the loop's after the event
        /// arrives; the event queue orders the two.</summary>
        public FileDialogResult? Answer { get; set; }

        public void FreeNative()
        {
            if (NativeFilters is not null)
            {
                for (var i = 0; i < Filters.Count; i++)
                {
                    NativeMemory.Free(NativeFilters[i].Name);
                    NativeMemory.Free(NativeFilters[i].Pattern);
                }

                NativeMemory.Free(NativeFilters);
                NativeFilters = null;
            }

            if (Properties != 0)
            {
                DestroyProperties(Properties);
                Properties = 0;
            }
        }

        /// <summary>On the loop's thread. A cancelled task has already completed, and the answer goes nowhere.</summary>
        public void Complete()
        {
            _cancellation.Dispose();
            _completion.TrySetResult(Answer ?? new FileDialogResult { Error = "The dialog answered nothing" });
        }
    }

    // SDL3-CS binds the callback as a marshalled delegate; a function pointer to an UnmanagedCallersOnly method needs
    // no marshalling under native AOT and nothing the GC could collect while SDL holds it.
    [LibraryImport("SDL3", EntryPoint = "SDL_ShowFileDialogWithProperties")]
    private static partial void ShowFileDialogWithProperties(int type,
        delegate* unmanaged[Cdecl]<nint, byte**, int, void> callback, nint userdata, uint props);
}
