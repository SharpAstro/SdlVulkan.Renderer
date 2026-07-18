using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Owns the SDL window and Vulkan instance/surface lifecycle.
/// </summary>
public sealed unsafe class SdlVulkanWindow : IDisposable
{
    public nint Handle { get; }
    public VkInstance Instance { get; }
    public VkSurfaceKHR Surface { get; private set; }

    /// <summary>The SDL window id this window's events carry (<c>SDL_GetWindowID</c>). Multi-window
    /// event dispatch routes each SDL event to the matching window by this id.</summary>
    public uint WindowId { get; }

    // True only for the standalone Create() path, which initialized SDL itself and so must SDL_Quit
    // on dispose. Windows made via CreateForApp share SdlVulkanApp's SDL lifecycle and must NOT quit
    // SDL out from under their sibling windows.
    private readonly bool _ownsSdl;

    private SdlVulkanWindow(nint handle, VkInstance instance, VkSurfaceKHR surface, bool ownsSdl)
    {
        Handle = handle;
        Instance = instance;
        Surface = surface;
        WindowId = GetWindowID(handle);
        _ownsSdl = ownsSdl;
    }

    /// <summary>
    /// Standalone single-window path: initializes SDL, creates the Vulkan instance, then a window +
    /// surface. The window owns the SDL lifecycle (SDL_Quit on dispose); the instance is torn down by
    /// the owning <see cref="VulkanContext"/>/<see cref="VulkanDevice"/>. For multiple windows use
    /// <see cref="SdlVulkanApp"/> instead, which owns one SDL lifecycle + instance + shared device.
    /// </summary>
    public static SdlVulkanWindow Create(string title, int width, int height, bool fullscreen = false)
        => CreateInternal(InitSdlAndCreateInstance(), title, width, height, ownsSdl: true, fullscreen: fullscreen);

    /// <summary>
    /// Multi-window path used by <see cref="SdlVulkanApp"/>: creates a window + surface against an
    /// already-created shared instance. Does not init or quit SDL — the app owns that.
    /// <paramref name="maximized"/> defaults true (the main window). A tab being torn out is dragged as
    /// a small <paramref name="borderless"/> + <paramref name="alwaysOnTop"/> + non-<paramref
    /// name="focusable"/> "chip" window that <see cref="SetBordered"/>/<see cref="SetAlwaysOnTop"/>-morphs
    /// into a normal document window on drop.
    /// </summary>
    internal static SdlVulkanWindow CreateForApp(VkInstance instance, string title, int width, int height,
        bool maximized = true, bool borderless = false, bool alwaysOnTop = false, bool focusable = true)
        => CreateInternal(instance, title, width, height, ownsSdl: false,
            maximized: maximized, borderless: borderless, alwaysOnTop: alwaysOnTop, focusable: focusable);

    private static SdlVulkanWindow CreateInternal(VkInstance instance, string title, int width, int height,
        bool ownsSdl, bool maximized = true, bool borderless = false, bool alwaysOnTop = false,
        bool focusable = true, bool fullscreen = false)
    {
        var flags = WindowFlags.Vulkan;
        if (fullscreen)
        {
            // Android needs a FULLSCREEN Vulkan window. A windowed/maximized one receives surface-destroy
            // callbacks that null the native window, so building the swapchain hits VK_ERROR_SURFACE_LOST_KHR
            // (libsdl-org/SDL#12957). Fullscreen keeps the native surface attached.
            flags |= WindowFlags.Fullscreen;
        }
        else
        {
            flags |= WindowFlags.Resizable;
            if (maximized) flags |= WindowFlags.Maximized;
            if (borderless) flags |= WindowFlags.Borderless;
            if (alwaysOnTop) flags |= WindowFlags.AlwaysOnTop;
            if (!focusable) flags |= WindowFlags.NotFocusable;
        }
        var window = CreateWindow(title, width, height, flags);
        if (window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {GetError()}");

        // Pump events so the window manager processes the maximize before we read pixel size
        PumpEvents();

        if (!VulkanCreateSurface(window, instance.Handle, nint.Zero, out var surfaceHandle))
            throw new InvalidOperationException($"SDL_Vulkan_CreateSurface failed: {GetError()}");
        var surface = new VkSurfaceKHR((ulong)surfaceHandle);

        return new SdlVulkanWindow(window, instance, surface, ownsSdl);
    }

    /// <summary>
    /// Initializes SDL (video + events), loads Vulkan, and creates the shared instance. Called once
    /// by the standalone <see cref="Create"/> path and once by <see cref="SdlVulkanApp.Create"/>.
    /// </summary>
    internal static VkInstance InitSdlAndCreateInstance()
    {
        if (!Init(InitFlags.Video | InitFlags.Events))
            throw new InvalidOperationException($"SDL_Init failed: {GetError()}");

        VulkanLoadLibrary(null);
        vkInitialize().CheckResult();

        return CreateVulkanInstance();
    }

    public void GetSizeInPixels(out int w, out int h) => GetWindowSizeInPixels(Handle, out w, out h);

    /// <summary>
    /// Destroys the current Vulkan surface and creates a fresh one against the window's native
    /// surface. Needed on Android, where the native window is recreated during the splash-&gt;app
    /// handoff: the surface SDL created at window construction becomes <c>VK_ERROR_SURFACE_LOST_KHR</c>
    /// by the time the swapchain is built, so the host recreates it once the window has settled.
    /// Must not be called while the surface is in use by a live swapchain.
    /// </summary>
    public void RecreateSurface()
    {
        // Destroy the stale surface FIRST: it still binds the native window, so creating a second one
        // on the same window fails with VK_ERROR_NATIVE_WINDOW_IN_USE_KHR. Nothing may be using it —
        // the host only calls this during startup settling, before the swapchain exists.
        if (Surface != VkSurfaceKHR.Null)
            GetApi(Instance).vkDestroySurfaceKHR(Surface);
        if (!VulkanCreateSurface(Handle, Instance.Handle, nint.Zero, out var surfaceHandle))
            throw new InvalidOperationException($"SDL_Vulkan_CreateSurface failed: {GetError()}");
        Surface = new VkSurfaceKHR((ulong)surfaceHandle);
    }

    /// <summary>
    /// Returns the platform-native window handle backing this SDL window: an HWND on Windows,
    /// an NSWindow on macOS, a Wayland <c>wl_surface</c> (preferred) or X11 window on Linux.
    /// Used to host native child surfaces such as an embedded webview. Returns
    /// <see cref="nint.Zero"/> if the handle is unavailable for this platform/driver.
    /// </summary>
    public nint GetNativeWindowHandle()
    {
        var props = GetWindowProperties(Handle);
        if (OperatingSystem.IsWindows())
            return GetPointerProperty(props, Props.WindowWin32HWNDPointer, nint.Zero);
        if (OperatingSystem.IsMacOS())
            return GetPointerProperty(props, Props.WindowCocoaWindowPointer, nint.Zero);
        if (OperatingSystem.IsLinux())
        {
            // Prefer Wayland; fall back to X11. The X11 window is a NUMBER property (the XID),
            // not a pointer.
            var wlSurface = GetPointerProperty(props, Props.WindowWaylandSurfacePointer, nint.Zero);
            return wlSurface != nint.Zero
                ? wlSurface
                : (nint)GetNumberProperty(props, Props.WindowX11WindowNumber, 0);
        }
        return nint.Zero;
    }

    public void SetTitle(string title) => SDL3.SDL.SetWindowTitle(Handle, title);

    /// <summary>
    /// Returns the DPI display scale factor for this window (e.g. 1.5 for 150% scaling).
    /// Uses SDL3's <c>GetWindowDisplayScale</c> which correctly handles per-monitor DPI.
    /// </summary>
    public float DisplayScale
    {
        get
        {
            var scale = GetWindowDisplayScale(Handle);
            return scale > 0f ? scale : 1f;
        }
    }

    public void ToggleFullscreen()
    {
        var flags = GetWindowFlags(Handle);
        SetWindowFullscreen(Handle, (flags & WindowFlags.Fullscreen) == 0);
    }

    // --- Window placement / state (used by tab tear-out to float a new window at the cursor) ---

    /// <summary>True if the window is currently maximized.</summary>
    public bool IsMaximized => (GetWindowFlags(Handle) & WindowFlags.Maximized) != 0;

    /// <summary>
    /// True if the window is currently minimized. When minimized the presentation surface is
    /// 0x0 and every <c>vkAcquireNextImageKHR</c>/<c>vkQueuePresentKHR</c> returns
    /// <c>ErrorOutOfDateKHR</c>, so the event loop must skip rendering + swapchain recreation
    /// entirely (SDL keeps reporting a non-zero pixel size on Windows, so a size check alone
    /// does not detect the minimized state).
    /// </summary>
    public bool IsMinimized => (GetWindowFlags(Handle) & WindowFlags.Minimized) != 0;

    /// <summary>Restores the window to its floating (un-maximized / un-minimized) size and position.</summary>
    public void Restore() => RestoreWindow(Handle);

    /// <summary>Maximizes the window.</summary>
    public void Maximize() => MaximizeWindow(Handle);

    /// <summary>Minimizes (iconifies) the window. The presentation surface goes 0x0, so the event
    /// loop idles rendering (see <see cref="IsMinimized"/>) until it is restored.</summary>
    public void Minimize() => MinimizeWindow(Handle);

    /// <summary>Sets the window's top-left position in desktop coordinates.</summary>
    public void SetPosition(int x, int y) => SetWindowPosition(Handle, x, y);

    /// <summary>Gets the window's top-left position in desktop coordinates.</summary>
    public void GetPosition(out int x, out int y) => GetWindowPosition(Handle, out x, out y);

    /// <summary>Sets the window's logical (point) size.</summary>
    public void SetSize(int width, int height) => SetWindowSize(Handle, width, height);

    /// <summary>Adds or removes the OS window border/title bar. Used to morph a borderless drag "chip"
    /// into a normal bordered document window when a torn-out tab is dropped.</summary>
    public void SetBordered(bool bordered) => SetWindowBordered(Handle, bordered);

    /// <summary>Toggles the always-on-top flag. The drag chip floats on top; dropped, it drops back to
    /// normal stacking.</summary>
    public void SetAlwaysOnTop(bool onTop) => SetWindowAlwaysOnTop(Handle, onTop);

    /// <summary>Toggles whether the window is user-resizable.</summary>
    public void SetResizable(bool resizable) => SetWindowResizable(Handle, resizable);

    /// <summary>Toggles whether the window can take input focus. A drag chip is created non-focusable
    /// (so it doesn't steal focus mid-drag and stays off the taskbar); morphing it into a real document
    /// window turns this back on so it activates and gets a taskbar button like any normal window.</summary>
    public void SetFocusable(bool focusable) => SetWindowFocusable(Handle, focusable);

    /// <summary>Brings the window to the front and gives it input focus. Used after a tear-out/relocate
    /// so the resulting window surfaces where the user dropped it (and isn't lost off-screen/behind).</summary>
    public void Raise() => RaiseWindow(Handle);

    /// <summary>The global mouse position in desktop coordinates (across all displays), as integer
    /// pixels. Used to place a torn-out window under the cursor.</summary>
    public static void GetGlobalMousePosition(out int x, out int y)
    {
        GetGlobalMouseState(out var fx, out var fy);
        x = (int)fx;
        y = (int)fy;
    }

    // SDL cursor functions are documented as main-thread-only, so concurrent
    // calls would be a misuse anyway -- but a lock is cheap insurance against
    // the lazy-create racing with itself (which would leak the loser's handle)
    // and matches how we'd want to behave if a Dispose runs concurrent with a
    // SetSystemCursor.
    private readonly Dictionary<SystemCursor, nint> _cursorCache = [];
    private readonly Lock _cursorLock = new();
    private SystemCursor _currentCursor = SystemCursor.Default;

    /// <summary>
    /// Sets the active SDL system cursor (e.g. <see cref="SystemCursor.EWResize"/>
    /// for a horizontal resize handle). Cursors are lazy-created and cached for
    /// the lifetime of the window. No-op when <paramref name="cursor"/> matches
    /// the currently active cursor, so callers can fire this every frame from a
    /// hover test without churning SDL state.
    /// </summary>
    public void SetSystemCursor(SystemCursor cursor)
    {
        lock (_cursorLock)
        {
            if (cursor == _currentCursor) return;
            if (!_cursorCache.TryGetValue(cursor, out var handle))
            {
                handle = CreateSystemCursor(cursor);
                if (handle == nint.Zero) return; // SDL failure -- leave the cursor as-is
                _cursorCache[cursor] = handle;
            }
            SetCursor(handle);
            _currentCursor = cursor;
        }
    }

    public void Dispose()
    {
        lock (_cursorLock)
        {
            foreach (var handle in _cursorCache.Values)
            {
                DestroyCursor(handle);
            }
            _cursorCache.Clear();
        }
        DestroyWindow(Handle);

        // Only the standalone Create() window initialized SDL and may shut it down. App-owned windows
        // leave SDL_Quit to SdlVulkanApp, so closing one window doesn't tear SDL down for the others.
        if (_ownsSdl)
            Quit();
    }

    private static VkInstance CreateVulkanInstance()
    {
        var sdlExtensionNames = VulkanGetInstanceExtensions(out _)
            ?? throw new InvalidOperationException("SDL_Vulkan_GetInstanceExtensions failed");

        // DEBUG build or SDLVK_VALIDATION=1: enable the Khronos validation layer + a debug-utils
        // messenger so the layer's output is captured (routed to stderr + the inspector ring buffer)
        // instead of dropped. SDLVK_SYNC_VALIDATION=1 additionally turns on synchronization
        // validation, the GPU memory-hazard checker. All a no-op if the layer isn't installed.
        bool useValidation = VulkanValidation.Enabled && VulkanValidation.LayerAvailable();

        using var extensionArray = useValidation
            ? new VkStringArray(WithValidationExtensions(sdlExtensionNames))
            : new VkStringArray(sdlExtensionNames);
        using var validationLayers = useValidation ? new VkStringArray([VulkanValidation.LayerName]) : default;

        VkInstanceCreateInfo instanceCI = new()
        {
            enabledExtensionCount = extensionArray.Length,
            ppEnabledExtensionNames = extensionArray
        };
        if (useValidation)
        {
            instanceCI.enabledLayerCount = validationLayers.Length;
            instanceCI.ppEnabledLayerNames = validationLayers;
        }

        // pNext chain: synchronization-validation feature (opt-in) -> messenger CI (captures messages
        // emitted during vkCreateInstance / vkDestroyInstance too). A persistent messenger for the
        // rest of the instance's life is installed right after creation.
        var syncEnables = stackalloc VkValidationFeatureEnableEXT[1] { VkValidationFeatureEnableEXT.SynchronizationValidation };
        VkValidationFeaturesEXT syncFeatures = new()
        {
            enabledValidationFeatureCount = 1,
            pEnabledValidationFeatures = syncEnables
        };
        VkDebugUtilsMessengerCreateInfoEXT debugCI = VulkanValidation.MessengerCreateInfo();
        if (useValidation)
        {
            if (VulkanValidation.SyncEnabled)
            {
                syncFeatures.pNext = &debugCI;
                instanceCI.pNext = &syncFeatures;
            }
            else
            {
                instanceCI.pNext = &debugCI;
            }
        }

        vkCreateInstance(&instanceCI, null, out var instance).CheckResult();
        if (useValidation)
            VulkanValidation.InstallMessenger(instance, GetApi(instance));
        return instance;
    }

    // The SDL-required instance extensions plus VK_EXT_debug_utils (for the messenger) and, when sync
    // validation is requested, VK_EXT_validation_features. Kept off the hot path — only built when
    // validation is actually enabled.
    private static string[] WithValidationExtensions(string[] sdlExtensions)
    {
        // Vortice's VK_EXT_*_EXTENSION_NAME constants are UTF-8 ReadOnlySpan<byte>, not strings, so use
        // the canonical name literals here to merge with SDL's string[] extension list.
        var list = new List<string>(sdlExtensions) { "VK_EXT_debug_utils" };
        if (VulkanValidation.SyncEnabled)
            list.Add("VK_EXT_validation_features");
        return list.ToArray();
    }
}
