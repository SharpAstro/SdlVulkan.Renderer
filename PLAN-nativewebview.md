# PLAN: Cross-Platform Native WebView Abstraction

## Goal

Add a platform-abstracted browser view (`INativeWebView`) that renders web content
**inside an existing SDL3 window**. The webview sits as a native child surface on
top of the Vulkan swapchain — composited by the window system, not by the Vulkan
render pass. This mirrors how game-UI middleware (Scaleform, Coherent) works.

The library stays **.NET 10 Native-AOT-compatible** on all platforms.

## Non-Goals (out of scope for v1)

- Offscreen rendering into a Vulkan texture (CEF offscreen mode). That is a valid
  future path but adds complexity around texture sharing and frame sync.
- A managed C# HTML renderer. We use the platform's native browser engine.
- `webview.h` as a dependency. It brings its own event loop and window creation;
  we use the platform APIs directly so we control window lifecycle and input routing.

---

## Package / Assembly Layout (decided)

The WebView feature ships as **separate packages** so core renderer consumers
(e.g. TianWen) never pull a browser dependency. Core isolation is the driver:
the moment WebView is its own package, core stays clean.

```
SdlVulkan.Renderer                    core renderer — UNCHANGED, no webview deps
                                      (+ GetNativeWindowHandle() helper, see below)

SdlVulkan.Renderer.WebView            managed abstraction + all three backends
  ├─ ProjectReference → SdlVulkan.Renderer   (for SdlVulkanWindow / native handle)
  ├─ PackageReference → WebView2Aot          (net10.0-windows TFM only)
  └─ PackageReference → SdlVulkan.Renderer.WebView.Native  (net10.0-windows TFM only)

SdlVulkan.Renderer.WebView.Native     native-assets-only package (no managed code)
  └─ runtimes/win-x64|win-arm64|win-x86/native/WebView2Loader.dll
```

### Cross-platform packaging: multi-target the WebView package

`SdlVulkan.Renderer.WebView` multi-targets **`net10.0;net10.0-windows`** so a single
NuGet package carries every backend and NuGet hands each consumer the right TFM
(resolves old Open Question #5):

| TFM | Contains | Extra deps |
|-----|----------|------------|
| `net10.0` (base) | `INativeWebView`, `NativeWebView` factory, `NativeWebViewHandle`, `GtkWebView`, `CocoaWebView` | none |
| `net10.0-windows` | base **+** `Win32WebView` | `WebView2Aot`, `SdlVulkan.Renderer.WebView.Native` |

`net10.0-windows` can be **built on any OS** (Linux/macOS CI can still produce the
full package), and `WebView2Aot` is a plain `net10.0` package restorable anywhere.
`Win32WebView.cs` and the two Windows-only `PackageReference`s are gated to the
`net10.0-windows` TFM via `Condition="'$(TargetFramework)' == 'net10.0-windows'"`.

### Why a *separate* native-assets package (not bundled / not Microsoft.Web.WebView2)

- The loader binary `rarely changes` (per WebView2Aot docs) — versioning it apart
  from the managed binding logic keeps churn out of the binding package.
- It is the canonical .NET pattern (`*.NativeAssets.*`).
- We deliberately avoid taking `Microsoft.Web.WebView2` as a dependency (it carries
  WinForms/WPF loaders and managed interop we don't want). We only need the loader
  DLL, which we package ourselves.

The loader DLLs are sourced from the `Microsoft.Web.WebView2` package's
`runtimes/<rid>/native/WebView2Loader.dll` (vendored into the repo under the native
project). The **Edge WebView2 Runtime** itself is a system component (shipped with
Windows / Edge) — we do not redistribute it.

---

## Architecture

```
┌──────────────────────────────────────────────────┐
│                  INativeWebView                   │
│  (namespace SdlVulkan.Renderer.WebView)           │
│                                                   │
│  void AttachToWindow(SdlVulkanWindow window)      │
│  void Navigate(string url)                        │
│  void NavigateToString(string html)               │
│  void SetBounds(Rect rect)                        │
│  void Focus()                                     │
│  void SetVisible(bool visible)                    │
│  Task<string> ExecuteScriptAsync(string js)       │
│  void Dispose()                                   │
│                                                   │
│  event Action<string>? TitleChanged;              │
│  event Action<string>? NavigationCompleted;       │
│  event Action<string, string>? MessageReceived;   │
│  event Action<IntPtr, uint, IntPtr, IntPtr>?      │
│      WndProcOverride;  // HWND only (Windows)     │
├──────────────┬────────────────┬──────────────────┤
│  Windows     │     Linux      │      macOS       │
│ Win32WebView │  GtkWebView    │  CocoaWebView    │
│              │                │                  │
│ WebView2Aot  │ WebKitGTK      │ WKWebView        │
│ 1.4.1 (pkg)  │ (libwebkit2gtk │ (WebKit.framework│
│              │  P/Invoke)     │  P/Invoke)       │
└──────────────┴────────────────┴──────────────────┘

Factory:

    public static class NativeWebView
    {
        public static INativeWebView Create();
        // ^^ OperatingSystem.IsWindows() / IsLinux() / IsMacOS() dispatch.
        //    The Windows branch only resolves Win32WebView in the
        //    net10.0-windows TFM; the base TFM throws PlatformNotSupported
        //    for Windows (a base-TFM consumer on Windows is non-standard —
        //    NuGet hands Windows consumers the net10.0-windows assembly).
    }
```

### Why not `#if` in a single file?

The platform backends have **zero shared code** — different APIs, different object
lifecycles, different input models. Three separate files (`Win32WebView.cs`,
`GtkWebView.cs`, `CocoaWebView.cs`) behind a common interface is cleaner than one
file with `#if` blocks. The `NativeWebView.Create()` factory isolates the dispatch
to one place. (The only TFM gating is excluding `Win32WebView.cs` from the base
`net10.0` build, since it depends on the Windows-only `WebView2Aot`.)

---

## Platform Details

### 1. Windows — `Win32WebView` (WebView2 via WebView2Aot 1.4.1)

**NuGet dependency:** `WebView2Aot` 1.4.1 (source: <https://github.com/smourier/WebView2Aot>),
referenced only in the `net10.0-windows` TFM. MIT-licensed (compatible).

**How it works:**

1. Get the native HWND from the SDL window via `SdlVulkanWindow.GetNativeWindowHandle()`
   (see Integration Points — uses the confirmed `SDL.Props.WindowWin32HWNDPointer` key).

2. Create the WebView2 environment, then a controller as a child of that HWND:
   ```csharp
   WebView2Utilities.Initialize(Assembly.GetEntryAssembly());

   WebView2.Functions.CreateCoreWebView2EnvironmentWithOptions(
       PWSTR.Null, PWSTR.Null, null!,
       new CoreWebView2CreateCoreWebView2EnvironmentCompletedHandler((result, env) =>
       {
           env.CreateCoreWebView2Controller(
               hwnd,  // <-- SDL window's HWND, WebView2 becomes a child
               new CoreWebView2CreateCoreWebView2ControllerCompletedHandler((r, ctrl) =>
               {
                   _controller = new ComObject<ICoreWebView2Controller>(ctrl);
                   ctrl.put_Bounds(tagRECT.From(/* initial rect */));
                   ctrl.get_CoreWebView2(out var wv);
                   wv.Navigate(PWSTR.From(url));
               }));
       }));
   ```
   *(Exact WebView2Aot API surface to be verified against the 1.4.1 package when the
   Phase 1 body is implemented — the env/controller creation handlers and `ComObject<>`
   wrappers are the names from the project samples.)*

3. **Input routing:** The WebView2 HWND child captures mouse/keyboard messages that
   land on it. When the webview is hidden or the user clicks outside it, SDL
   receives events normally. This is the **simplest platform** — no manual input
   forwarding needed.
   - One gotcha: SDL's `PumpEvents()` / `PollEvent()` will miss messages that
     the WebView2 child window consumes. This is usually correct behavior (you
     *want* the browser to handle its own clicks). But if you need to intercept
     before WebView2, you'd subclass the child HWND or use a message hook.

4. **Resize:** Forward `SdlEventLoop` resize events to `put_Bounds()`.

5. **Visibility:** Show/hide the WebView2 child HWND with `put_IsVisible()` or
   `ShowWindow()`.

6. **JS ↔ .NET:** WebView2Aot supports this via `DispatchObject` + IDispatch COM
   interop (see `ScriptHostObjectWebView2` sample):
   ```csharp
   var hostObject = new HostObject();
   webView.AddHostObjectToScript("dotnet", hostObject);
   // JS: chrome.webview.hostObjects.dotnet.getInfoAsync(2000).then(...);
   ```

7. **AOT:** WebView2Aot is purpose-built for Native AOT. The `[GeneratedComClass]`
   attribute on host objects generates AOT-safe COM wrappers. The caller must
   unwrap `Task<T>.Result` explicitly (see `DispatchObject.GetTaskResult`).

8. **Loader discovery:** `WebView2Utilities.Initialize()` finds `WebView2Loader.dll`.
   We supply it via the `SdlVulkan.Renderer.WebView.Native` package's
   `runtimes/<rid>/native/` payload, which the SDK copies to the app's output/publish
   dir per RID. (Embedding-as-resource is the alternative WebView2Aot supports;
   we chose the runtimes package per the decision above.)

**Input forwarding consideration (optional future enhancement):**

If the composition mode (`ICoreWebView2CompositionController`) is preferred for
finer control, input must be forwarded manually from SDL events to `SendMouseInput`
/ `SendPointerInput` (the `HelloCompositionWebView2` sample). Start with HWND mode —
simpler and sufficient for a browser overlay.

### 2. Linux — `GtkWebView` (WebKitGTK via P/Invoke), Wayland-preferred

**Runtime dependency:** `libwebkit2gtk-4.1` (package `libwebkit2gtk-4.1-dev` on
Debian/Ubuntu, `webkit2gtk4.1` on Fedora, `webkit2gtk-4.1` on Arch) + GTK3.

**Decision: do NOT force X11.** SDL runs on its native backend (Wayland where
available — forcing `SDL_VIDEO_DRIVER=x11` would downgrade to XWayland and lose
native fractional scaling / HiDPI crispness). `GtkWebView.AttachToWindow()` detects
the **actual** SDL video driver at runtime and embeds accordingly:

```csharp
// Probe the window's properties (see Integration Points for the SDL.Props keys):
//   Wayland present?  ->  WindowWaylandSurfacePointer + WindowWaylandDisplayPointer
//   X11 present?      ->  WindowX11WindowNumber       + WindowX11DisplayPointer
```

**Wayland path (preferred):** Wayland has no XEmbed/reparent equivalent and forbids
cross-client reparenting by design. The only in-process route is a `wl_subsurface`:
share SDL's `wl_display` (`WindowWaylandDisplayPointer`) and create the WebKit
widget's surface as a **subsurface of SDL's `wl_surface`** (`WindowWaylandSurfacePointer`),
then drive its position/size and commit/damage in step with the SDL window. GTK and
SDL each open their own Wayland connection by default, so wiring WebKitGTK to render
into SDL's surface is the genuinely-hard part of this backend and **may land as a
follow-up** after the X11 path proves the abstraction. (`xdg_foreign` /
`xdg_toplevel_export_handle` is for toplevel parenting, not child-region embedding —
not applicable here.)

**X11 path (fallback, incl. XWayland):** create a `WebKitWebView`, realize it, then
`XReparentWindow` its X11 window into SDL's X11 window (`WindowX11WindowNumber`)
at the target offset:
```csharp
// Pseudo-code (X11 path):
[DllImport("libwebkit2gtk-4.1")] static extern IntPtr webkit_web_view_new();
[DllImport("libgtk-3")]          static extern IntPtr gtk_widget_get_window(IntPtr widget);
// ... create + realize WebKitWebView, then:
XReparentWindow(display, gdk_x11_window_get_xid(webviewGdkWindow), sdlX11Window, x, y);
```

**Input routing:** SDL events forwarded to GTK. On X11, synthesize via `XSendEvent`;
the reparented widget may need explicit focus forwarding. On Wayland the subsurface
surfaces its own input region.

**JS ↔ .NET:** `webkit_web_view_evaluate_javascript()` for C# → JS; a custom URI
scheme handler (`webkit_web_context_register_uri_scheme()`) for JS → C#.

**P/Invoke surface needed (minimal):**
```csharp
// libwebkit2gtk-4.1
webkit_web_view_new()
webkit_web_view_load_uri(webview, uri)
webkit_web_view_load_html(webview, html, base_uri)
webkit_web_view_evaluate_javascript(webview, js, length, world_name,
    source_uri, cancellable, callback, user_data)

// libgtk-3
gtk_init(ref argc, ref argv)
gtk_widget_set_size_request(widget, width, height)
gtk_widget_show(widget) / gtk_widget_hide(widget) / gtk_widget_grab_focus(widget)
gtk_widget_get_window(widget)   // returns GdkWindow*
gdk_x11_window_get_xid(window)  // X11 path only
// Wayland: gdk_wayland_window_get_wl_surface(window), wl_subsurface_* via libwayland-client
```

**Key risk:** GTK requires `gtk_init()` before any GTK function, on the main thread.
The SDL event loop already runs on the main thread. `gtk_init()` must be called
after `SDL_Init()` (SDL on X11 opens the display first) and before `SdlEventLoop.Run()`.
On Linux, `NativeWebView.Create()` calls `gtk_init()` (idempotent after first call).
The SDL3 window must already exist before `AttachToWindow()`.

### 3. macOS — `CocoaWebView` (WKWebView via P/Invoke)

**Runtime dependency:** `WebKit.framework` (bundled with macOS).

**How it works:**

1. Get the NSWindow from SDL via `GetNativeWindowHandle()`
   (`SDL.Props.WindowCocoaWindowPointer`).

2. Create a WKWebView and add it as a subview of the SDL window's content view:
   ```csharp
   // WKWebViewConfiguration* config = [WKWebViewConfiguration new];
   // WKWebView* webView = [[WKWebView alloc] initWithFrame:frame configuration:config];
   // [nsWindow.contentView addSubview:webView];
   ```

3. **Input routing:** the NSView hierarchy handles this natively — WKWebView
   receives mouse/key events on its frame like any NSView. No manual forwarding.

4. **JS ↔ .NET:** `[webView evaluateJavaScript:js completionHandler:...]` for
   C# → JS; `WKScriptMessageHandler` via
   `[userContentController addScriptMessageHandler:name:]` for JS → C#.

5. **P/Invoke:** prefer `System.Runtime.InteropServices.ObjectiveC` (.NET 8+) over
   raw `[DllImport("libobjc.dylib")] objc_msgSend` — the variadic calling convention
   differs between ARM64 and x86-64 macOS and the built-in interop handles it.

---

## Integration Points with Existing Code

### SdlVulkanWindow — native handle exposure (lives in CORE; no webview dep)

`SdlVulkanWindow.Handle` is the SDL window pointer. The webview backends need the
**platform-native** handle. Add a `GetNativeWindowHandle()` helper to core — it is a
generic capability (not webview-specific) and pulls no webview dependency. The
SDL3-CS property keys are **confirmed** to exist as constants under `SDL.Props`
(resolves old Open Question #1):

```csharp
public nint GetNativeWindowHandle()
{
    var props = SDL_GetWindowProperties(Handle);
    if (OperatingSystem.IsWindows())
        return SDL_GetPointerProperty(props, SDL.Props.WindowWin32HWNDPointer, 0);
    if (OperatingSystem.IsMacOS())
        return SDL_GetPointerProperty(props, SDL.Props.WindowCocoaWindowPointer, 0);
    if (OperatingSystem.IsLinux())
        // Wayland first, X11 fallback (per the Linux decision):
        return SDL_GetPointerProperty(props, SDL.Props.WindowWaylandSurfacePointer, 0) is var s and not 0
            ? s : SDL_GetPointerProperty(props, SDL.Props.WindowX11WindowNumber, 0);
    return 0;
}
```

Confirmed `SDL.Props.*` keys available in the binding:
`WindowWin32HWNDPointer`, `WindowWin32HDCPointer`, `WindowWin32InstancePointer`,
`WindowX11DisplayPointer`, `WindowX11ScreenNumber`, `WindowX11WindowNumber`,
`WindowWaylandDisplayPointer`, `WindowWaylandSurfacePointer`, `WindowWaylandEGLWindowPointer`,
`WindowCocoaWindowPointer`, `WindowCocoaMetalViewTagNumber`.

The Linux/Wayland backend additionally needs the **display** pointers
(`WindowWaylandDisplayPointer` / `WindowX11DisplayPointer`); the WebView assembly
reads those directly via `SDL_GetWindowProperties` rather than overloading the core
single-handle helper.

### SdlEventLoop — resize forwarding

When `SdlWindowView.OnResize` fires (swapchain resized), the webview must resize its
child window/view to match. The `SdlWindowView` owning the webview calls
`SetBounds()` in its `OnResize` callback.

### SdlEventLoop — focus management

When the webview is active, SDL no longer receives keyboard input — WebView2 /
WKWebView consumes it; clicking outside refocuses the SDL window and SDL events
resume. No special handling on Windows/macOS. On Linux (X11) the reparented widget
may need explicit focus forwarding.

### VkRenderer / VulkanContext — no changes needed

The webview is a **native child surface on top** of the swapchain. It does not touch
the Vulkan render pass, pipelines, or descriptors — the window system compositor
stacks it above the Vulkan swapchain surface.

---

## Implementation Phases

### Phase 1: Project scaffold + Interface + Windows backend (2–3 days)

1. Create **`SdlVulkan.Renderer.WebView.Native`** (native-assets package): vendor
   `WebView2Loader.dll` for win-x64/win-arm64/win-x86 under `runtimes/<rid>/native/`,
   pack with a `lib/net10.0/_._` placeholder. No managed code.
2. Create **`SdlVulkan.Renderer.WebView`** multi-targeting `net10.0;net10.0-windows`:
   - `INativeWebView` interface
   - `NativeWebView` factory (static `Create()`)
   - `NativeWebViewHandle` helper (SDL window → native handle / display)
   - `Win32WebView` (net10.0-windows only) — wraps `ICoreWebView2Controller`
   - `GtkWebView`, `CocoaWebView` — skeletons in this phase
   - `WebView2Aot` + `SdlVulkan.Renderer.WebView.Native` refs gated to `net10.0-windows`
3. Add `WebView2Aot` 1.4.1 to `Directory.Packages.props`; add both projects to the `.sln`.
4. Add `SdlVulkanWindow.GetNativeWindowHandle()` to core.
5. Manual test: SDL window + Win32WebView, navigate to a URL, verify render + input.

**Deliverable:** WebView2 inside an SDL3 Vulkan window on Windows ARM64 + x64,
built as Native AOT.

### Phase 2: macOS backend (1–2 days)

1. Implement `CocoaWebView` using `System.Runtime.InteropServices.ObjectiveC`.
2. Test on macOS (ARM64 + x86-64) with an SDL3 Vulkan window.
3. Verify WKWebView input, JS interop, memory lifecycle.

**Risk:** needs a macOS build/test machine.

### Phase 3: Linux backend (3–4 days) — Wayland-preferred

1. Implement `GtkWebView` P/Invoke to `libwebkit2gtk-4.1` + `libgtk-3`.
2. **X11 reparenting path first** (proves the abstraction end-to-end), then the
   **Wayland `wl_subsurface` path** (the hard part). Driver detected at runtime — no
   forced `SDL_VIDEO_DRIVER`.
3. Test on Wayland (Fedora / Ubuntu) and X11 (and XWayland) sessions.
4. Verify input forwarding, JS interop, `gtk_init()` lifecycle.

**Note:** if Wayland-native embedding proves too costly for v1, ship X11 + XWayland
and track Wayland-native as a follow-up — but the goal is native Wayland.

### Phase 4: Input routing polish + tests (1–2 days)

1. Keyboard focus grab/release correct per platform.
2. Edge cases: webview during fullscreen toggle, resize-to-zero (minimize),
   multi-window with per-window webviews.
3. `OnMessageReceived` (JS → C#) on all platforms.
4. Tests for factory dispatch + lifecycle.
5. `[rdiag]` diagnostics for webview load timing/errors.

---

## Design Decisions & Rationale

### Why child surface, not offscreen rendering?

| Approach | Pros | Cons |
|----------|------|------|
| **Child surface** (this plan) | Simpler; native input handling; HW-accelerated compositing by the window system; zero Vulkan integration | Webview always on top of Vulkan content; no post-processing on web content |
| **Offscreen → Vulkan texture** | Full compositing control; effects/transforms on web content | Requires CEF (~150 MB); texture-sharing across API boundaries; complex frame pacing |

A child surface is the pragmatic v1. Offscreen-to-texture can be added later as an
alternative backend (`CefOffscreenWebView`) behind the same `INativeWebView`.

### Why per-platform P/Invoke, not webview.h?

`webview.h` creates its *own* window and event loop. We already have an SDL3 event
loop managing Vulkan swapchains and multiple windows. Direct platform APIs give us
full control over window parenting, event routing, and add no extra dependency
(WebKitGTK and WKWebView are already on the system).

### Why WebView2Aot on Windows, not raw WebView2 COM?

WebView2Aot provides AOT-safe generated COM wrappers (no runtime COM interop /
trimming issues), handles `WebView2Loader.dll` discovery, handles `IDispatch`-based
JS ↔ .NET bridging, and has zero WinForms/WPF dependency. Saves ~5000 lines of COM
interop. MIT-licensed; actively maintained.

---

## File Layout

```
src/
  SdlVulkan.Renderer/
    SdlVulkanWindow.cs                 // + GetNativeWindowHandle() helper
    ...                                // (otherwise unchanged)

  SdlVulkan.Renderer.WebView/
    SdlVulkan.Renderer.WebView.csproj  // multi-target net10.0;net10.0-windows
    INativeWebView.cs                  // interface + NativeWebView factory
    NativeWebViewHandle.cs             // SDL window → native handle/display
    Win32WebView.cs                    // net10.0-windows only: WebView2 via WebView2Aot
    GtkWebView.cs                      // Linux: WebKitGTK via P/Invoke (Wayland-first)
    CocoaWebView.cs                    // macOS: WKWebView via ObjectiveC interop

  SdlVulkan.Renderer.WebView.Native/
    SdlVulkan.Renderer.WebView.Native.csproj   // native-assets only
    runtimes/win-x64/native/WebView2Loader.dll
    runtimes/win-arm64/native/WebView2Loader.dll
    runtimes/win-x86/native/WebView2Loader.dll
```

---

## Open Questions

1. ~~**SDL3-CS property key names.**~~ **RESOLVED** — exposed as `SDL.Props.*`
   constants (see Integration Points). No magic strings needed.

2. **Linux: X11 vs Wayland?** **DECIDED** — native (don't force a driver),
   per-driver embedding: Wayland `wl_subsurface` preferred, X11 `XReparentWindow`
   fallback. Wayland-native embedding may land as a follow-up if too costly for v1.

3. **macOS: ObjectiveC interop vs binding library?** Start with
   `System.Runtime.InteropServices.ObjectiveC` (.NET 8+). Re-evaluate only if
   inadequate.

4. ~~**WebView2Aot license.**~~ **RESOLVED** — MIT, compatible.

5. ~~**Package reference conditional / single cross-platform NuGet?**~~ **RESOLVED**
   — `SdlVulkan.Renderer.WebView` multi-targets `net10.0;net10.0-windows`; the
   `net10.0-windows` TFM adds `Win32WebView` + the Windows-only package refs. One
   cross-platform NuGet, buildable on any OS.

6. **WebView2Aot exact API surface (1.4.1).** Verify the env/controller creation
   handler type names and `ComObject<>` wrappers against the restored 1.4.1 package
   when implementing the Phase 1 body (the snippets above use sample names).
