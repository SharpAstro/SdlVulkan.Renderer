using DIR.Lib;
using SharpAstro.AppShell;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Owns the SDL window and Vulkan instance/surface lifecycle.
/// </summary>
/// <remarks>
/// <para>Implements <see cref="IActivatableWindow"/> so a single-instance hand-off can bring this
/// window forward correctly without every consumer re-deriving how. The three members it needs
/// (<see cref="IsMinimized"/>, <see cref="Restore"/>, <see cref="Raise"/>) already existed; the
/// value is that <c>window.Activate()</c> now carries the RULE, which is not obvious and which two
/// separate applications got wrong in the same way: restoring before raising un-maximises the window
/// it is bringing forward, and raising without restoring leaves a minimised one off-screen while
/// holding input focus. See <see cref="WindowActivation"/> for the full reasoning.</para>
/// </remarks>
public sealed unsafe class SdlVulkanWindow : IDisposable, IActivatableWindow
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
    /// Standalone path for a window whose pixels blend with what is behind it, a desktop companion or an overlay:
    /// created with <c>SDL_WINDOW_TRANSPARENT</c>, not resizable, by default borderless and always on top. Pair it
    /// with <see cref="VulkanContext.CreateTransparent"/>, clear each frame to alpha 0, and use
    /// <see cref="SetShape"/> to let clicks through outside the silhouette (a layered window, see
    /// <see cref="AlphaPresentation.LayeredWindow"/>, lets them through wherever alpha is zero without one).
    /// </summary>
    public static SdlVulkanWindow CreateTransparent(string title, int width, int height, bool borderless = true,
        bool alwaysOnTop = true)
        => CreateInternal(InitSdlAndCreateInstance(), title, width, height, ownsSdl: true, maximized: false,
            borderless: borderless, alwaysOnTop: alwaysOnTop, transparent: true);

    /// <summary>
    /// Multi-window path used by <see cref="SdlVulkanApp"/>: creates a window + surface against an
    /// already-created shared instance. Does not init or quit SDL — the app owns that.
    /// <paramref name="maximized"/> defaults true (the main window). A tab being torn out is dragged as
    /// a small <paramref name="borderless"/> + <paramref name="alwaysOnTop"/> + non-<paramref
    /// name="focusable"/> "chip" window that <see cref="SetBordered"/>/<see cref="SetAlwaysOnTop"/>-morphs
    /// into a normal document window on drop. A <paramref name="hidden"/> window stays off screen until
    /// <see cref="Show"/>.
    /// </summary>
    internal static SdlVulkanWindow CreateForApp(VkInstance instance, string title, int width, int height,
        bool maximized = true, bool borderless = false, bool alwaysOnTop = false, bool focusable = true,
        bool hidden = false, bool transparent = false)
        => CreateInternal(instance, title, width, height, ownsSdl: false,
            maximized: maximized, borderless: borderless, alwaysOnTop: alwaysOnTop, focusable: focusable,
            hidden: hidden, transparent: transparent);

    // transparent: SDL_WINDOW_TRANSPARENT, which SDL_SetWindowShape requires, and NOT resizable. A transparent window
    // is sized to its content by the app, and SDL stretches a window's shape to the window's size, so a resize by
    // the user would stretch the silhouette with it.
    private static SdlVulkanWindow CreateInternal(VkInstance instance, string title, int width, int height,
        bool ownsSdl, bool maximized = true, bool borderless = false, bool alwaysOnTop = false,
        bool focusable = true, bool fullscreen = false, bool hidden = false, bool transparent = false)
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
            if (transparent) flags |= WindowFlags.Transparent;
            else flags |= WindowFlags.Resizable;
            if (maximized) flags |= WindowFlags.Maximized;
            if (borderless) flags |= WindowFlags.Borderless;
            if (alwaysOnTop) flags |= WindowFlags.AlwaysOnTop;
            if (!focusable) flags |= WindowFlags.NotFocusable;
            if (hidden) flags |= WindowFlags.Hidden;
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
    /// Safe-area insets in PIXELS: how far content must stay from each window edge to avoid display
    /// cutouts (camera notch/punch-hole), rounded corners, and — when not fullscreen — system bars.
    /// Wraps <c>SDL_GetWindowSafeArea</c> (window coordinates), scaled to pixel coordinates so it
    /// composes directly with <see cref="GetSizeInPixels"/>. All zeros on desktop (the whole window is
    /// safe) and whenever SDL can't report an area. Re-query on resize/rotation/foreground-return —
    /// the cutout moves with orientation.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom) GetSafeAreaInsets()
    {
        GetSizeInPixels(out var pw, out var ph);
        GetWindowSize(Handle, out var ww, out var wh);
        if (pw <= 0 || ph <= 0 || ww <= 0 || wh <= 0 || !GetWindowSafeArea(Handle, out var safe))
            return (0, 0, 0, 0);
        // Window coords -> pixels (identical on Android; hi-dpi desktops scale, though there the
        // safe area is the full window anyway).
        var sx = (float)pw / ww;
        var sy = (float)ph / wh;
        return ((int)(safe.X * sx), (int)(safe.Y * sy),
                (int)((ww - safe.X - safe.W) * sx), (int)((wh - safe.Y - safe.H) * sy));
    }

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
    /// Tells the platform where text is being typed, so an input method can put its candidate window
    /// beside the caret instead of over it. <paramref name="x"/>/<paramref name="y"/>/<paramref name="w"/>/
    /// <paramref name="h"/> bound the text being edited, in window coordinates;
    /// <paramref name="cursorOffset"/> is the caret's offset from the left of that rect.
    /// </summary>
    /// <remarks>
    /// Call it while a field is focused, whenever the caret moves. The platform has no other way to know:
    /// SDL does not track your caret, so without this the candidate window lands wherever the IME
    /// defaults to, typically covering the text being typed. Pair it with
    /// <see cref="SdlWindowView.OnTextEditing"/> -- the area tells the IME where to draw, the event tells
    /// you what it is composing, and CJK input needs BOTH.
    /// </remarks>
    public void SetTextInputArea(int x, int y, int w, int h, int cursorOffset)
    {
        var rect = new Rect { X = x, Y = y, W = w, H = h };
        // The SDL entry point takes a const pointer and copies the rect before returning, so the address
        // of this stack local is safe to hand over.
        SDL3.SDL.SetTextInputArea(Handle, (nint)(&rect), cursorOffset);
    }

    /// <summary>
    /// Sets the window's icon: the title bar mark, the taskbar or dock button, and the alt-tab entry.
    /// Pass the largest size the app has (256px is plenty) and let the platform scale down.
    /// </summary>
    /// <remarks>
    /// <para>An app that ships a Win32 icon resource in its executable already gets one on Windows, and
    /// nowhere else: X11 and Wayland read the icon from the window, and a Linux build with no call to
    /// this shows the desktop's placeholder. So this is not a Windows-redundant convenience, it is the
    /// only portable route.</para>
    /// <para>SDL copies the pixels into its own icon storage, so the surface and the caller's array are
    /// free the moment this returns. Takes <see cref="RgbaImage"/> because that is what every decoder in
    /// this stack already produces; the format handed to SDL is the little-endian spelling of
    /// memory-order RGBA.</para>
    /// </remarks>
    public void SetIcon(RgbaImage image)
    {
        fixed (byte* pixels = image.Pixels)
        {
            var surface = CreateSurfaceFrom(image.Width, image.Height,
                PixelFormat.ABGR8888, (nint)pixels, image.Width * 4);
            if (surface == nint.Zero) return;   // out of memory or a bad stride; an iconless window is not fatal
            SetWindowIcon(Handle, surface);
            DestroySurface(surface);
        }
    }

    /// <summary>Whether the window was created transparent (<see cref="CreateTransparent"/>,
    /// <see cref="SdlVulkanApp.CreateTransparentWindow"/>): <c>SDL_WINDOW_TRANSPARENT</c>, which
    /// <see cref="SetShape"/> requires.</summary>
    public bool IsTransparent => (GetWindowFlags(Handle) & WindowFlags.Transparent) != 0;

    /// <summary>
    /// Cuts the window to the silhouette in <paramref name="shape"/>'s alpha (<c>SDL_SetWindowShape</c>); null
    /// restores the full rectangle. Returns false when SDL refuses, which it does for a window not created
    /// transparent.
    /// </summary>
    /// <remarks>
    /// <para>The shape is one bit per pixel: on Windows SDL turns it into a window region (<c>SetWindowRgn</c>)
    /// holding every pixel whose alpha is not zero, after stretching the image to the window's size. Outside the
    /// region clicks go to whatever is underneath, and nothing of the window is drawn there either, so a shape for
    /// a window that <see cref="VulkanContext.PresentsAlpha"/> must cover every pixel with any alpha, soft edges and
    /// glows included, or they are cut off. Inside it, a window that presents alpha still blends exactly. One whose
    /// swapchain is opaque shows a hard-edged cut-out of an opaque frame, whose antialiased edge blends with the clear colour:
    /// clear to the colour at the sprite's edge, or draw an outline, so that fringe does not show.</para>
    /// <para>A layered window (<see cref="AlphaPresentation.LayeredWindow"/>) needs no shape: Windows passes its clicks
    /// through wherever a pixel's alpha is zero. A shape still clips it, as it does any window.</para>
    /// <para>Rebuilding the region costs about 3 ms for a 256 by 256 window (measured on Windows), so set it when
    /// the silhouette changes, not every frame.</para>
    /// </remarks>
    public bool SetShape(RgbaImage? shape)
    {
        if (shape is null)
            return SetWindowShape(Handle, nint.Zero);
        fixed (byte* pixels = shape.Pixels)
        {
            // SDL converts the surface into its own copy before building the region, so this one can go at once.
            var surface = CreateSurfaceFrom(shape.Width, shape.Height,
                PixelFormat.ABGR8888, (nint)pixels, shape.Width * 4);
            if (surface == nint.Zero) return false;
            var applied = SetWindowShape(Handle, surface);
            DestroySurface(surface);
            return applied;
        }
    }

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

    /// <summary>
    /// A size in design units (DIR.Lib's, 1 at 96 dpi) in this window's coordinates, rounded to whole ones: what
    /// <see cref="SetSize"/>, <see cref="SetMinimumSize"/> and <see cref="ResizeInPlace(int, int)"/> take. A design unit
    /// spans <see cref="DisplayScale"/> over the window's pixel density: 1.5 at 150% on Windows and X11, where window
    /// coordinates are pixels, and the display's content scale alone where they are points and the pixels come from
    /// the density.
    /// </summary>
    /// <remarks>For a window sized in the units of the layout inside it, so it is as big on a 150% display as its
    /// contents are. Each app used to multiply by a scale of its own choosing, and <see cref="DisplayScale"/> is the
    /// right one only where window coordinates are pixels.</remarks>
    public (int Width, int Height) ToWindowSize(float designWidth, float designHeight)
    {
        var density = GetWindowPixelDensity(Handle);
        var scale = new DesignScale(DisplayScale / (density > 0f ? density : 1f));
        return ((int)MathF.Round(scale.ToSurfaceX(designWidth)), (int)MathF.Round(scale.ToSurfaceY(designHeight)));
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

    /// <summary>
    /// True while the window is hidden (<see cref="Hide"/>, or created hidden): not on screen, and not on the taskbar
    /// or in Alt+Tab either. The event loop draws nothing for it, as for a minimized one.
    /// </summary>
    public bool IsHidden => (GetWindowFlags(Handle) & WindowFlags.Hidden) != 0;

    /// <summary>Minimized or hidden: nothing of the window is on screen, so the event loop draws nothing for it.</summary>
    /// <remarks>On Android, minimized only, as before hiding was added. There the loop pauses a backgrounded window
    /// itself and rebuilds its surface on return, in the render pass (the black-screen-on-resume fix); a device that
    /// reported the window hidden and then only restored would leave SDL's hidden flag set, and a render pass that
    /// skipped hidden windows would never reach that rebuild.</remarks>
    internal bool IsMinimizedOrHidden =>
#if ANDROID
        IsMinimized;
#else
        (GetWindowFlags(Handle) & (WindowFlags.Minimized | WindowFlags.Hidden)) != 0;
#endif

    /// <summary>
    /// Hides the window until <see cref="Show"/>: off the screen, the taskbar and Alt+Tab, unlike
    /// <see cref="Minimize"/>, which leaves it on the taskbar. It keeps its size and place, and its global hotkeys,
    /// which are how an app hidden like this is usually brought back. A native child (an embedded web view) goes
    /// with it, and the event loop stops drawing it.
    /// </summary>
    public void Hide() => HideWindow(Handle);

    /// <summary>
    /// Shows a hidden window again, where it was. Whether it also comes to the front with the keyboard is the OS's
    /// call: an app in the background is refused that, except in answer to the user's input, such as the press of
    /// a global hotkey (<see cref="SdlEventLoop.OnGlobalHotKey"/>), where <see cref="Raise"/> after this does it.
    /// </summary>
    public void Show() => ShowWindow(Handle);

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
    public void SetBordered(bool bordered)
    {
        SetWindowBordered(Handle, bordered);
        ApplyMaximizable(); // SDL rebuilt the style
    }

    /// <summary>Toggles the always-on-top flag. The drag chip floats on top; dropped, it drops back to
    /// normal stacking.</summary>
    public void SetAlwaysOnTop(bool onTop) => SetWindowAlwaysOnTop(Handle, onTop);

    /// <summary>Toggles whether the window is user-resizable.</summary>
    public void SetResizable(bool resizable)
    {
        SetWindowResizable(Handle, resizable);
        ApplyMaximizable(); // SDL rebuilt the style
    }

    /// <summary>Toggles whether the window can take input focus. A drag chip is created non-focusable
    /// (so it doesn't steal focus mid-drag and stays off the taskbar); morphing it into a real document
    /// window turns this back on so it activates and gets a taskbar button like any normal window.</summary>
    public void SetFocusable(bool focusable) => SetWindowFocusable(Handle, focusable);

    /// <summary>
    /// True while this is the active window, the one keyboard input goes to: on Windows the foreground
    /// window, elsewhere the window with SDL's input focus. It holds while a native child (an embedded
    /// web view) has the keyboard focus, since the window stays the foreground one. Nothing signals a
    /// change; read it from <see cref="SdlEventLoop.OnLoopIteration"/>, which runs at least every 16 ms.
    /// </summary>
    /// <remarks>For an app that wants to SHOW when it is taking input: an always-on-top or borderless
    /// window has no OS title bar to light up, so its own chrome has to.</remarks>
    public bool IsActive => OperatingSystem.IsWindows()
        ? Win32Window.IsForeground(GetNativeWindowHandle())
        : (GetWindowFlags(Handle) & WindowFlags.InputFocus) != 0;

    /// <summary>
    /// Allows or forbids maximizing. On Windows it sets the window's maximize box, which is also what
    /// decides whether a double-click on a caption maximizes (including a borderless window's
    /// draggable area under an SDL hit test, which Windows treats as a caption), and whether Win+Up or
    /// a drag to the top edge do. Elsewhere it does nothing: SDL has no such flag, and a borderless
    /// window has no maximize button to remove.
    /// </summary>
    /// <remarks>The choice is kept. SDL rebuilds a resizable window's style, maximize box included, when it
    /// restores the window from minimized, leaves fullscreen, or runs <see cref="SetBordered"/> or
    /// <see cref="SetResizable"/>. Those two put the choice back themselves, and <see cref="SdlEventLoop"/> does
    /// on the events that follow the others (restored, shown, left fullscreen). Before it did, a window minimized
    /// and restored had its box back, and a double-click on its caption maximized it. A host with its own SDL loop
    /// calls this again after those events.</remarks>
    public void SetMaximizable(bool maximizable)
    {
        _maximizable = maximizable;
        ApplyMaximizable();
    }

    // SetMaximizable's choice, null until it is made: SDL's own style stands until then.
    private bool? _maximizable;

    /// <summary>Puts <see cref="SetMaximizable"/>'s choice back after SDL rebuilt the window's style.</summary>
    internal void ApplyMaximizable()
    {
        if (_maximizable is { } maximizable && OperatingSystem.IsWindows())
            Win32Window.SetMaximizeBox(GetNativeWindowHandle(), maximizable);
    }

    /// <summary>The smallest size the user can resize the window to, in window coordinates. A window
    /// already smaller grows to it.</summary>
    public void SetMinimumSize(int width, int height) => SetWindowMinimumSize(Handle, width, height);

    /// <summary>
    /// The usable area of the display the window is on: the display less its taskbar, dock or panels,
    /// in desktop coordinates. For keeping a window that moves or resizes itself on screen.
    /// </summary>
    public bool TryGetUsableDisplayBounds(out int x, out int y, out int width, out int height)
    {
        if (GetDisplayUsableBounds(GetDisplayForWindow(Handle), out var r))
        {
            (x, y, width, height) = (r.X, r.Y, r.W, r.H);
            return true;
        }
        (x, y, width, height) = (0, 0, 0, 0);
        return false;
    }

    /// <summary>
    /// Resizes the window where it stands, in window coordinates: the corner nearest its display's own corner stays
    /// put, so a window parked bottom right grows up and to the left, and the window is kept inside the display's
    /// usable area (<see cref="TryGetUsableDisplayBounds"/>). Never under its minimum size.
    /// </summary>
    /// <remarks>For a window that changes its own size, a floating player switching modes or folding to a strip.
    /// <see cref="SetSize"/> keeps the top-left corner, which walks a window parked at the bottom of the screen off
    /// it.</remarks>
    public void ResizeInPlace(int width, int height)
    {
        GetWindowMinimumSize(Handle, out var minimumWidth, out var minimumHeight);
        Place(Math.Max(width, minimumWidth), Math.Max(height, minimumHeight));
    }

    /// <summary>
    /// <see cref="ResizeInPlace(int, int)"/> with a new minimum size (<see cref="SetMinimumSize"/>), which the size is
    /// never under.
    /// </summary>
    /// <remarks>One call, because the order matters and differs by direction. A raised minimum grows the window at
    /// once, from its top-left corner, before the resize could keep another corner where it was; a lowered one has to
    /// come first, or it holds the window up. So the minimum is lowered, the window placed, and only then
    /// raised.</remarks>
    public void ResizeInPlace(int width, int height, int minimumWidth, int minimumHeight)
    {
        GetWindowMinimumSize(Handle, out var oldWidth, out var oldHeight);
        SetWindowMinimumSize(Handle, Math.Min(oldWidth, minimumWidth), Math.Min(oldHeight, minimumHeight));
        Place(Math.Max(width, minimumWidth), Math.Max(height, minimumHeight));
        SetWindowMinimumSize(Handle, minimumWidth, minimumHeight);
    }

    private void Place(int width, int height)
    {
        GetWindowPosition(Handle, out var x, out var y);
        GetWindowSize(Handle, out var w, out var h);
        var current = new WindowRect(x, y, w, h);
        var placed = TryGetUsableDisplayBounds(out var ux, out var uy, out var uw, out var uh)
            ? WindowPlacement.Place(current, width, height, new WindowRect(ux, uy, uw, uh))
            : current with { Width = width, Height = height };
        SetWindowSize(Handle, placed.Width, placed.Height);
        SetWindowPosition(Handle, placed.X, placed.Y);
    }

    /// <summary>Brings the window to the front and gives it input focus. Used after a tear-out/relocate
    /// so the resulting window surfaces where the user dropped it (and isn't lost off-screen/behind).</summary>
    public void Raise() => RaiseWindow(Handle);

    private readonly HashSet<int> _globalHotKeys = [];

    /// <summary>
    /// Registers a key combination for the whole desktop: pressed while any app is active, it reaches this one, as
    /// <see cref="SdlEventLoop.OnGlobalHotKey"/> with <paramref name="id"/> (a host running its own SDL loop reads it
    /// with <see cref="IsGlobalHotKey"/>). Windows only for now; elsewhere it returns false.
    /// </summary>
    /// <param name="id">The app's own number for the combination, passed back on each press; 0 to 0xBFFF.</param>
    /// <param name="modifiers">Any of <see cref="Keymod.Ctrl"/>, <see cref="Keymod.Shift"/>, <see cref="Keymod.Alt"/>
    /// and <see cref="Keymod.GUI"/> (the Windows key).</param>
    /// <param name="key">A character, such as <c>Keycode.Slash</c>, found on the keyboard layout active now (where the
    /// layout needs Shift for it, Shift is added), or F1 to F24.</param>
    /// <returns>False when another app holds the combination or Windows reserves it (Win+S, Win+F), when the layout
    /// has no key for the character, or off Windows.</returns>
    /// <remarks>
    /// <para>Call it on the thread that created the window (Windows binds a hotkey to a window of the calling thread).
    /// The registration lasts until <see cref="UnregisterGlobalHotKey"/> or <see cref="Dispose"/>.</para>
    /// <para>A press is input to this app, so in its handler <see cref="Raise"/> can bring the window to the front
    /// and take the keyboard, which an app in the background is otherwise refused.</para>
    /// <para>On Windows this takes SDL's Windows message hook (<c>SDL_SetWindowsMessageHook</c>), of which there is
    /// one per process: an app that sets its own replaces this one.</para>
    /// </remarks>
    public bool TryRegisterGlobalHotKey(int id, Keymod modifiers, Keycode key)
    {
        if (!OperatingSystem.IsWindows() || _globalHotKeys.Contains(id)
            || !GlobalHotKeys.TryRegister(GetNativeWindowHandle(), id, modifiers, key))
            return false;
        _globalHotKeys.Add(id);
        return true;
    }

    /// <summary>Gives back a combination registered with <see cref="TryRegisterGlobalHotKey"/>.</summary>
    public void UnregisterGlobalHotKey(int id)
    {
        if (OperatingSystem.IsWindows() && _globalHotKeys.Remove(id))
            GlobalHotKeys.Unregister(GetNativeWindowHandle(), id);
    }

    /// <summary>For a host that runs its own SDL event loop: whether <paramref name="evt"/> is the press of a
    /// combination registered with <see cref="TryRegisterGlobalHotKey"/>, and its id.
    /// <see cref="SdlEventLoop"/> does this itself and calls <see cref="SdlEventLoop.OnGlobalHotKey"/>.</summary>
    public static bool IsGlobalHotKey(in Event evt, out int id)
    {
        var isHotKey = GlobalHotKeys.EventType != 0 && evt.Type == GlobalHotKeys.EventType;
        id = isHotKey ? evt.User.Code : 0;
        return isHotKey;
    }

    /// <summary>
    /// Reports each double-click on the window's caption, as <see cref="SdlWindowView.OnCaptionDoubleClick"/> (a host
    /// running its own SDL loop reads it with <see cref="IsCaptionDoubleClick"/>). The caption is the OS title bar,
    /// or on a borderless window the area its SDL hit test calls draggable, which Windows treats as one. SDL gives
    /// the app neither click: Windows takes them, to move the window. Windows only for now; elsewhere it returns false.
    /// </summary>
    /// <remarks>
    /// <para>Windows' own handling goes ahead as well: a window that may maximize does. One that wants the
    /// double-click for itself calls <see cref="SetMaximizable"/>(false).</para>
    /// <para>On Windows this takes SDL's Windows message hook, as <see cref="TryRegisterGlobalHotKey"/> does: one per
    /// process, so an app that sets its own replaces this one. It lasts until <see cref="Dispose"/>.</para>
    /// </remarks>
    public bool TryReportCaptionDoubleClicks()
        => OperatingSystem.IsWindows()
           && WindowsMessageHook.TryReportCaptionDoubleClicks(GetNativeWindowHandle(), WindowId);

    /// <summary>For a host that runs its own SDL event loop: whether <paramref name="evt"/> is a double-click on this
    /// window's caption (<see cref="TryReportCaptionDoubleClicks"/>). <see cref="SdlEventLoop"/> does this itself and
    /// calls <see cref="SdlWindowView.OnCaptionDoubleClick"/>.</summary>
    public bool IsCaptionDoubleClick(in Event evt)
        => WindowsMessageHook.CaptionDoubleClickEvent != 0 && evt.Type == WindowsMessageHook.CaptionDoubleClickEvent
           && evt.User.WindowID == WindowId;

    private MediaControls? _mediaControls;
    private bool _mediaControlsAsked;

    /// <summary>
    /// The window's entry in the system's media controls (the media flyout, the lock screen, the keyboard's media
    /// keys; see <see cref="MediaControls"/>), made on the first call and gone with the window. Its presses arrive as
    /// <see cref="SdlWindowView.OnMediaButton"/> (a host running its own SDL loop reads them with
    /// <see cref="IsMediaButton"/>). Windows only for now; elsewhere, or when the system refuses, null.
    /// </summary>
    /// <remarks>Call it on the window's thread, as every <see cref="MediaControls"/> call after it.</remarks>
    public MediaControls? TryGetMediaControls()
    {
        if (!_mediaControlsAsked)
        {
            _mediaControlsAsked = true;
            if (OperatingSystem.IsWindows())
                _mediaControls = MediaControls.TryCreate(GetNativeWindowHandle(), WindowId);
        }
        return _mediaControls;
    }

    /// <summary>For a host that runs its own SDL event loop: whether <paramref name="evt"/> is a press of this
    /// window's media controls (<see cref="TryGetMediaControls"/>), and which button. <see cref="SdlEventLoop"/> does
    /// this itself and calls <see cref="SdlWindowView.OnMediaButton"/>.</summary>
    public bool IsMediaButton(in Event evt, out MediaButton button)
        => MediaControls.IsPress(evt, out var windowId, out button) && windowId == WindowId;

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
        foreach (var id in _globalHotKeys.ToArray())
            UnregisterGlobalHotKey(id);
        if (OperatingSystem.IsWindows())
            WindowsMessageHook.StopReportingCaptionDoubleClicks(GetNativeWindowHandle());
        // Out of the media controls before the HWND they were made for goes.
        _mediaControls?.Dispose();
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

    /// <summary>
    /// The instance API version to request: what the loader supports, capped at 1.1.
    ///
    /// <para>1.1 is where the <c>*2</c> physical-device queries and the pNext chains they carry became
    /// core, which is all this renderer needs from a version bump; asking for more would refuse to
    /// create an instance on an older loader for no gain. <c>vkEnumerateInstanceVersion</c> is itself a
    /// 1.1 function, so a loader that does not export it IS 1.0 and gets 1.0.</para>
    /// </summary>
    internal static VkVersion InstanceApiVersion()
    {
        uint raw = 0;
        try
        {
            if (vkEnumerateInstanceVersion(&raw) != VkResult.Success) return VkVersion.Version_1_0;
        }
        catch (EntryPointNotFoundException)
        {
            // A strictly 1.0 loader does not export it. 1.0 it is.
            return VkVersion.Version_1_0;
        }
        var supported = new VkVersion(raw);
        return supported >= VkVersion.Version_1_1 ? VkVersion.Version_1_1 : VkVersion.Version_1_0;
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

        // Ask for the highest instance version the loader offers, capped at what this renderer needs.
        // With no pApplicationInfo at all the instance is Vulkan 1.0 BY SPEC, and the failure mode of
        // that is quiet rather than loud: a core-1.1 physical-device query like
        // vkGetPhysicalDeviceProperties2 still fills the base struct -- through the 1.0 entry point --
        // and simply ignores the pNext chain, so every chained struct reads back zeroed and looks like
        // a driver that does not implement the feature. VK_EXT_memory_budget is the case that found
        // this: the extension is advertised, the device enables it, and the budget came back 0 MB.
        VkApplicationInfo appInfo = new() { apiVersion = InstanceApiVersion() };
        VkInstanceCreateInfo instanceCI = new()
        {
            pApplicationInfo = &appInfo,
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
