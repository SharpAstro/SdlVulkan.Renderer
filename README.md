# SdlVulkan.Renderer

SDL3 + Vortice.Vulkan rendering library built on [DIR.Lib](https://github.com/SharpAstro/DIR.Lib) primitives.

## Types

- **`SdlVulkanWindow`** — SDL3 window with Vulkan instance and surface lifecycle. Creates maximized, resizable windows with Vulkan surface.
- **`SdlFileDialogs`** — DIR.Lib's `IFileDialogs` over the platform's own open, save and folder dialogs (`new SdlFileDialogs(window)`). Never blocks the window; the task completes on the event loop's thread, which `SdlEventLoop` arranges itself and a host with its own loop arranges with `SdlFileDialogs.TryDeliver(evt)`.
- **`VulkanContext`** — Vulkan device, command buffers, sync management. `MaxFramesInFlight = 2` with per-frame vertex buffers. Two construction modes:
  - `VulkanContext.Create(instance, surface, w, h, ...)` — on-screen path with a swapchain tied to an `SdlVulkanWindow`.
  - `VulkanContext.CreateOffscreen(instance, w, h, ...)` — headless path rendering to a standalone `VkImage`; no surface, no swapchain, no SDL window. See **Headless / offscreen rendering** below.
  - `VulkanContext.CreateTransparent(window, w, h, ...)`: on-screen path for a window whose pixels blend with the desktop. See **Transparent windows** below.
- **`VkRenderer`** — `Renderer<VulkanContext>` implementation with FillRectangle, DrawRectangle, FillEllipse, DrawText, plus batched glyph and persistent-vertex-buffer draw APIs. Exposes `FontAtlasDirty` so callers can trigger redraws after glyph rasterization. Has `BeginFrame` / `BeginOffscreenFrame` variants that match the two `VulkanContext` modes.
- **`VkPipelineSet`** — Vulkan pipeline creation from pre-baked SPIR-V (flat, textured, ellipse, page, stroke, SDF, round-rect, blend variants, mesh, Slug). Shaders are authored as GLSL 450 and baked to committed SPIR-V by `tools/BakeShaders`, re-run by hand after editing one — see **Dependencies** below.
- **`VkFontAtlas`** — Dynamic bitmap glyph atlas with ManagedFontRasterizer (from DIR.Lib) rasterization and Vulkan texture upload. Supports grow (512→4096), deferred eviction, and `skipUnflushed` to prevent sampling stale GPU texture data.
- **`VkSdfFontAtlas`** — Signed-distance-field glyph atlas side-car for resolution-independent text. `SdfRasterSize = 64` (overridable per atlas), `fwidth`-driven AA in the fragment shader auto-tunes to ±0.5 screen pixels at any zoom. Four-channel R8G8B8A8Unorm MTSDF texture — RGB carry pseudo-distance, which the shader medians to keep corners sharp, and A the true distance. Keyed on `(font, size, gid, glyph name)`: glyph identity rather than code point, so subset fonts that reuse code points don't collide.

## Font Atlas Lifecycle

Per frame:
1. `BeginFrame()` / `BeginOffscreenFrame()` — handles deferred eviction, runs `OnPreFlush` (pre-warm callback), calls `Flush(cmd)` on both atlases, runs `OnPreRenderPass` (texture uploads), then `BeginRenderPass`.
2. `Flush(cmd)` — uploads dirty staging region to GPU via `vkCmdCopyBufferToImage`.
3. `DrawText(...)` → `GetGlyph(...)` — cache hit returns UV coords; miss rasterizes into staging.
4. `GetGlyph(..., skipUnflushed: true)` — in draw loops, returns zero-width for glyphs not yet uploaded. Pair with `PreWarmGlyph` in `OnPreFlush` if drawing a glyph that wasn't shown last frame (first-frame glyph flicker). For SDF text, `PreWarmSdfGlyph` is the equivalent; `PreWarmSdfGlyphBatch` warms many glyphs in one call with parallel rasterization — preferred when a page introduces tens-to-hundreds of unique glyphs.

**Thread safety**: `vkDeviceWaitIdle()` before reusing the shared upload buffer (prevents race with `MaxFramesInFlight = 2`).

**Diagnostic logging** (`Console.Error`): `[FontAtlas]` / `[VkRenderer]` prefixed lines for Flush, Grow, EvictAll, cache miss, Resize. Capture with `2>stderr.log`.

## Usage

```csharp
using SdlVulkan.Renderer;

using var window = SdlVulkanWindow.Create("My App", 800, 600);
window.GetSizeInPixels(out var w, out var h);

var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)w, (uint)h);
var renderer = new VkRenderer(ctx, (uint)w, (uint)h);

while (running)
{
    if (!renderer.BeginFrame(bgColor)) { renderer.Resize(w, h); continue; }
    renderer.FillRectangle(rect, color);
    renderer.DrawText("Hello", fontPath, 14f, white, layout);
    renderer.EndFrame();
    if (renderer.FontAtlasDirty) needsRedraw = true;
}
```

The click that activates an unfocused window is delivered to the app as an ordinary click (`SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH`, set before `SDL_Init`), so one click focuses the window and presses the button. An app whose canvas does something costly or irreversible on a bare click passes `clickThroughOnFocus: false` to `SdlVulkanApp.Create`, `SdlVulkanWindow.Create` or `SdlVulkanWindow.CreateTransparent`.

By default the app lets the display sleep (SDL's own default would hold it on while a window is open). Pass
`keepDisplayAwake: true` to `SdlVulkanWindow.Create`/`CreateTransparent` or `SdlVulkanApp.Create`, or call
`window.KeepDisplayAwake(true/false)` at runtime (process-wide), for a live view or focusing screen.

**Tell the OS who the app is.** Every creator (`SdlVulkanApp.Create`, `SdlVulkanWindow.Create`, `CreateTransparent`) takes an optional `SdlAppMetadata` (name, version, reverse-DNS identifier such as `com.sharpastro.tianwen`, optional creator and url) and hands it to `SDL_SetAppMetadata` before `SDL_Init`. The taskbar groups the app's windows by it, Wayland and X11 take it as `app_id` / `WM_CLASS`, and the macOS menu and a power-inhibit reason show it. Pass nothing and the entry assembly's name and version are used (under NativeAOT there may be no entry assembly, so pass `appMetadata` explicitly there): `SdlVulkanApp.Create(appMetadata: new("TianWen", "1.4.2", "com.sharpastro.tianwen"))`.

## Headless / offscreen rendering

`VulkanContext.CreateOffscreen` builds a context that renders to a single `VkImage` instead of a swapchain. No `VkSurfaceKHR`, no SDL window, no `VK_KHR_swapchain` device extension requested — useful for tests, thumbnail / raster workers, CI without a display server, and server-side rendering.

```csharp
using SdlVulkan.Renderer;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

// Minimal instance — no windowing extensions needed.
vkInitialize().CheckResult();
VkInstanceCreateInfo ici = new();
vkCreateInstance(&ici, null, out var instance).CheckResult();

const uint W = 1920, H = 1080;
using var ctx = VulkanContext.CreateOffscreen(instance, W, H);
using var renderer = new VkRenderer(ctx, W, H);

renderer.BeginOffscreenFrame(new DIR.Lib.RGBAColor32(255, 255, 255, 255));
renderer.FillRectangle(new RectInt(new(100, 100), new(400, 300)), red);
renderer.DrawText("Hello", fontPath, 14f, black, layout);
renderer.EndOffscreenFrame();
ctx.WaitOffscreenFrameComplete();

byte[] rgba = ctx.ReadbackOffscreenRgba(); // top-down, 4 bytes per pixel
// Pipe rgba into PNG encoder / image library of choice.
```

The offscreen path reuses all existing pipelines, MSAA slots, sync objects, and vertex ring buffers. Only the swapchain acquire/present is replaced by `vkCmdCopyImageToBuffer` into a host-visible staging buffer.

### Runtime requirements (native)

At runtime you need the Vulkan loader plus an ICD (driver). Nothing else: no X11 / Wayland, no SDL native, no DirectX.

- **Windows**: `vulkan-1.dll` (shipped with any modern GPU driver; also installable via the Vulkan SDK).
- **Linux**: `libvulkan.so.1` + an ICD. Options in increasing order of portability:
  - **GPU driver** (Mesa `radv` / `intel_anv` / NVIDIA proprietary) — fastest.
  - **Mesa `lavapipe` / `llvmpipe`** — software rasterizer. 5–20× slower but fully headless. Apt: `mesa-vulkan-drivers`.
  - **Google SwiftShader** — alternative software ICD.
- **macOS / iOS**: MoltenVK (Vulkan on Metal). Untested for offscreen here but no reason it shouldn't work.

SDL3-CS remains a package reference because the on-screen path uses it. Its P/Invokes are lazy — `libSDL3.so` / `SDL3.dll` is never loaded if `SdlVulkanWindow` is not instantiated, so the offscreen path has no SDL runtime dependency.

### Android

Android is a first-class target: the package multi-targets `net10.0;net10.0-android`, the host is
`Android/SdlVulkanActivity.cs` (`abstract class SdlVulkanActivity : Org.Libsdl.App.SDLActivity`), and
the Java bridge plus per-ABI `libSDL3.so` come from `SDL3-CS.Android`. Two properties of that native
package are load-bearing for **every** Android consumer, so they live here rather than in any one app:

**Keep the Java and native halves on the same version.** SDL checks them against each other at
startup, so a mismatch is not a warning — the app dies at launch. 3.4.12.x shipped exactly that: a
Java bridge built for 3.4.10 alongside a 3.4.12 native (`expected 3.4.10, got 3.4.12`), which is why
this package sat on 3.4.10.5 for a year. **3.4.16 is consistent** and is what both halves pin now, in
`src/SdlVulkan.Renderer/Directory.Packages.props`, TFM-conditional so desktop stays on the 3.5.0
preview.

**16 KB page alignment — fixed in 3.4.16, and a hard Google Play requirement.** Play refuses an
upload whose 64-bit native libraries are laid out for 4 KB pages, because an Android 15+ device may
use 16 KB ones. Up to 3.4.10.5 the shipped `libSDL3.so` was 4 KB aligned and raised `warning XA0141`
in every consumer build; in a real app bundle it was the *only* misaligned library among ninety,
because everything the .NET Android SDK emits is already aligned. 3.4.16 aligns the `arm64-v8a` and
`x86_64` natives. (`armeabi-v7a` and `x86` still are not, which does not matter: 16 KB pages are a
64-bit concern and a Release build publishes neither ABI.) See
[Android's page-sizes guide](https://developer.android.com/guide/practices/page-sizes).

**Checking a bump without a device.** Both properties above are readable straight out of the
packages, which is worth doing first because only one of them can fail the build:

- **Version coherence** — `javap -p -constants org/libsdl/app/SDLActivity.class`, taken from the
  `.aar` inside `SDL3-CS.Android`, prints `SDL_MAJOR/MINOR/MICRO_VERSION`; the version string inside
  `runtimes/android-arm64/native/libSDL3.so` gives the native side. 3.4.16 reads `3.4.16` against
  `SDL-3.4.16-release-3.4.16`.
- **Page size** — the `p_align` of the `PT_LOAD` segments in each `runtimes/android-*/native/libSDL3.so`
  must be at least 16384 for the 64-bit ABIs.

Neither replaces launching it. The version check fails at startup, on a device, and nowhere else.

Consumers also need `dotnet workload install android`; the workload supplies the .NET bits but not
the Android SDK, which `dotnet build -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=true`
will provision. The tooling needs a JDK it can parse — **JDK 17**; JDK 25 is too new and throws inside
`GetJdkRevision`.

### CI setup

On a fresh Ubuntu runner (GitHub Actions `ubuntu-latest`, Azure Pipelines, GitLab):

```yaml
- run: sudo apt-get update && sudo apt-get install -y libvulkan1 mesa-vulkan-drivers vulkan-tools
- run: dotnet test
```

`vulkan-tools` is optional but gives you `vulkaninfo --summary` for sanity-checking which ICD the runner loaded. For deterministic behaviour across runners, pin to lavapipe:

```yaml
- run: echo "VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.x86_64.json" >> $GITHUB_ENV
```

Containers: add the same two packages to your image. The offscreen path doesn't require a `tty`, display variable, or privileged mode.

## Transparent windows

A window whose pixels blend with what is behind it, such as a desktop companion or an overlay. Transparency is a
per-window choice: ordinary windows are unchanged.

```csharp
using var window = SdlVulkanWindow.CreateTransparent("Buddy", 240, 240);   // borderless, always on top
using var ctx = VulkanContext.CreateTransparent(window, 240, 240);
using var renderer = new VkRenderer(ctx, 240, 240);
window.SetShape(silhouette);   // an RgbaImage: clicks outside its non-zero alpha reach the window underneath

renderer.BeginFrame(new RGBAColor32(0, 0, 0, 0));   // clear to transparent, then draw as usual
// ...
renderer.EndFrame();
```

With `SdlVulkanApp`, open it with `app.CreateTransparentWindow(...)` and build its context with
`VulkanContext.CreateTransparentForSharedDevice(app.DeviceFor(window), window, w, h)`.

The window's frames reach the screen with their alpha by the first of these the machine can do, and
`ctx.AlphaPresentation` says which it got. The app draws the same way whichever it is.

| | Way | Where | Cost |
|---|---|---|---|
| 1 | `Swapchain`, on the native device | Wayland, macOS, Android drivers usually | none |
| 2 | `Swapchain`, on a second device | Windows with Mesa's Dozen installed | a second device, which shares no resources |
| 3 | `LayeredWindow`, on the native device | Windows without Dozen, or kept off it | a readback every frame |
| 4 | `None`: an opaque swapchain, cut by `SetShape` | anywhere else | no alpha, hard edges only |

- **Alpha comes from the swapchain's `PRE_MULTIPLIED` composite alpha,** and the ordinary pipelines already leave
  premultiplied pixels over a transparent clear. Wayland, macOS and Android drivers usually offer it.
- **On Windows the native drivers do not.** NVIDIA, Intel and Qualcomm Adreno offer no premultiplied alpha for any
  kind of window (`OPAQUE`, plus `INHERIT` on Intel). Mesa's Dozen (Vulkan over Direct3D 12) does, and composites through DirectComposition, so a
  transparent window that needs it gets a second device: Dozen on the same GPU, matched by adapter LUID. Every other
  window stays on the native device. Dozen comes with Microsoft's "OpenCL, OpenGL, and Vulkan Compatibility Pack"
  (`winget install 9NQPSL29BFFF --source msstore`).
- **Without Dozen, a layered window.** The native device draws into an image of the context's own instead of a
  swapchain's; `EndFrame` waits for the GPU, copies the frame out and hands it to the window with
  `UpdateLayeredWindow`, which blends it per pixel. Clicks pass through wherever alpha is zero, so the window needs no
  shape. The copy grows with the window's size: a 240 by 240 companion took 0.3 ms a frame from `BeginFrame` to
  `EndFrame` on an Adreno X1-85, against 0.09 ms for Dozen's swapchain. To take this way with Dozen installed, and
  keep the window's resources on the native device, build its context on `app.Device` rather than
  `app.DeviceFor(window)`, or pass `nativeDeviceOnly: true` standalone.
- **Check `ctx.AlphaPresentation`** (or `ctx.PresentsAlpha`). `None` on a transparent window means no device's
  swapchain offered alpha and there are no layered windows to fall back on, so its swapchain is `OPAQUE`. Some
  Windows drivers blend an opaque swapchain's alpha anyway on an SDL transparent window, which Vulkan does not report
  (seen with NVIDIA under Mailbox presentation and with Intel under Fifo, in a Remote Desktop session; the Adreno, at
  its console, blends it under neither), so treat that as a bonus, not a guarantee. `SetShape` still cuts the window
  to a hard-edged silhouette.
- **The shape is one bit per pixel** (a window region on Windows). It decides both where clicks go and where anything
  is drawn, so make it cover every pixel with any alpha, soft edges included. Rebuilding it costs about 3 ms at
  256 by 256, so set it when the silhouette changes, not every frame.
- **A companion with hard edges needs no alpha at all.** On an opaque swapchain the shape still cuts the window out
  and lets clicks through outside it, with no readback and no second device: give it an ordinary context,
  `VulkanContext.CreateForSharedDevice(app.Device, window.Surface, ...)` or `VulkanContext.Create` standalone. Its
  antialiased edge blends with the clear colour, so clear to the colour at the sprite's edge, or draw an outline.
- **A second device shares no GPU resources** with the first: textures and meshes made on `app.Device` are not valid
  in a transparent window on Dozen. A layered window on `app.Device` shares them.

`tools/TransparencyProbe` measures a machine: `caps` lists what each device's surface offers, `present all` puts a
known premultiplied picture over a checkerboard for every device, present mode and window kind and scores what the
screen shows, and `buddy` runs this API end to end and times its frames (`buddy app native` and
`buddy standalone native` keep the window on the native device, a layered window on Windows; `buddy app opaque`
gives it an opaque swapchain). A capture is the compositor's picture, not necessarily the panel's: add `--look` to
hold each picture on screen and answer by eye, which the tool prints beside the capture's verdict.

## Slug text

Text drawn from the glyph's **outline**, not from an atlas: each glyph is encoded once on the CPU (DIR.Lib's `SlugGlyphEncoder`, Eric Lengyel's Slug algorithm) into a blob of curves and bands, and the fragment shader computes the coverage at every pixel from it. Nothing is rasterized at a size, so there is no atlas to fill, no field to magnify, and nothing depends on the pass's sample count: the edge is analytic, as smooth on `SingleSampleMainPass` as on a multisampled pass. It sits beside MTSDF (`VkSdfFontAtlas`), which stays the default text path.

**Pick Slug over MTSDF when** the size of the text varies widely (zoomable pages, a PDF or CAD view, text on a rotated or horizontally stretched axis), when the glyph set is large (CJK: an atlas page fills and recycles, the Slug store is one buffer that grows), or when corners and thin strokes must stay exact at any magnification. **Keep MTSDF when** the text is small, fixed-size UI: its fragment cost is lower (the probe below measures it).

```csharp
// Batch: one colour and one size, ONE draw however many glyphs.
renderer.BeginSlugGlyphBatch(color, fontSize);
renderer.AddBatchedSlugGlyphAtBaseline(fontPath, new Rune('A'), -1, penX, baselineY);   // or ...ByGid(fontPath, gid, null, ...)
renderer.EndGlyphBatch();

// Persistent: a page laid out once and kept; glyphs of ANY mix of sizes, one draw per colour.
var kind = renderer.WriteSlugGlyphInstance(fontPath, gid, null, penX, baselineY, fontSize, rotation: 0f, xScale: 1f,
    instances.AsSpan(i * VkRenderer.SlugInstanceFloats, VkRenderer.SlugInstanceFloats));   // Quad, Blank (a space) or Pending
var (buffer, memory) = renderer.CreatePersistentVertexBuffer(instances);
renderer.DrawPersistentSlugGlyphs(buffer, 0, glyphCount, color, originX, originY, zoom);   // each frame, at any origin and zoom
```

A glyph the store has not encoded yet is **never waited for**: it is queued, skipped this frame (`Pending`, and `FontAtlasDirty` asks for another frame), and drawn once its blob has been uploaded; write the instance again then. `renderer.SlugStoreStats` reports what the store holds, how often it has grown and whether anything is pending; `renderer.SlugDrawCalls` counts the draws. Pipelines exist for every pass shape (with and without depth, multisampled and single-sample).

**Measure it on your machine and your page: `tools/SlugProbe`.** A headless run (no window; exit 2 with no Vulkan device) that draws one A4-like page of reading-size text with a CJK block as persistent MTSDF and as persistent Slug, and prints draws and GPU ms per frame, the worst number of curves a pixel walks on the densest CJK glyph, one dense CJK glyph alone at 300 px (first draw and after), and the longest frame while a burst of new glyphs misses the store and makes it grow:

```
dotnet run --project tools/SlugProbe -c Release
```

Options `--frames`, `--size`, `--msaa`, `--burst` and `--cjk-font` (see `tools/SlugProbe/Program.cs`). The CJK block takes the distinct glyphs its face has and the report says how many; the committed Noto Sans JP subset has only three, so its block repeats them, and `--cjk-font PATH` with a full CJK face makes it a block of different glyphs, which is the load a store sees on a real CJK page. The numbers of one machine are in [CHANGELOG.md](CHANGELOG.md), "7.83".

## HDR

HDR output is an **opt-in per context**, and an app that does not opt in pays nothing for it: its swapchain, render passes, pipelines and frame code are the ones it always had, with no extra pass, target, conversion, shader branch or per-frame allocation (the SDR frame bench below measures that).

```csharp
using var window = SdlVulkanWindow.Create("Viewer", 1280, 800);
window.GetSizeInPixels(out var w, out var h);
var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)w, (uint)h, hdr: true);   // the opt-in
var renderer = new VkRenderer(ctx, (uint)w, (uint)h);

// A float texture: four floats a pixel (R, G, B, A), row 0 at the top, scene-linear light where 1.0 is 203 cd/m2.
using var image = VkTexture.CreateFromRgbaFloat(ctx, rgba, imageWidth, imageHeight);

// Every frame (an HDR frame is repainted in full, so draw every HDR image every frame):
renderer.DrawHdrImage(image, x, y, width, height, HdrPrimaries.Bt2020, sourcePeakNits: 1000f);
```

`hdr` is accepted by `VulkanContext.Create`, `CreateForSharedDevice` and `CreateOffscreen`. `SdlVulkanWindow`/`SdlVulkanApp` enable `VK_EXT_swapchain_colorspace` on the instance when the loader offers it (without it a driver lists `SRGB_NONLINEAR` for every format, whatever the panel can do).

**scRGB only.** The HDR swapchain is `R16G16B16A16_SFLOAT` in `EXTENDED_SRGB_LINEAR_EXT` (scRGB: linear light, 1.0 is 80 cd/m2, negative components carry colours outside BT.709), the Windows-native path. HDR10 (PQ) is not produced. `ctx.HdrRequested` says it was asked for, `ctx.HdrActive` says it was GOT.

**What happens on SDR.** When the surface offers no scRGB (an SDR display, an SDR-only driver, HDR off in the OS), `HdrActive` is false and the context is exactly an SDR one: nothing HDR exists. `DrawHdrImage` then still works, tone-mapped for the SDR target (below), so one code path draws on both kinds of display. Transparent, layered and Dozen-device windows never take scRGB (their alpha is carried another way) and keep the SDR swapchain.

**The composite, and why it costs the SDR path nothing.** On an HDR window the frame is rendered as it always was, into a swapchain-sized B8G8R8A8 SDR *layer* (so every pipeline, blend state, MSAA resolve, cached layer, thumbnail and readback is bit-identical to the SDR path, and the device's passes stay 8-bit, so one shared device serves HDR and SDR windows). `EndFrame` then records ONE composite pass on the float swapchain image: the queued HDR images first, then the SDR layer over them (premultiplied, sRGB-decoded to linear and scaled by the display's SDR white, so the UI keeps the brightness the user set for SDR content). Only an HDR window has the layer, the pass and the composite pipelines; an SDR context allocates none of it, and records no extra command. An HDR frame is painted in full every time (no damage repaint).

**`DrawHdrImage` units and the tone curve.** The texture's RGB is scene-linear light in units of `referenceWhiteNits` (default 203 cd/m2, ITU-R BT.2408: where a diffuse white page sits), in the primaries you name (`HdrPrimaries.Bt709` or `Bt2020`); alpha is straight. On an HDR window the image is queued for the composite and drawn as scRGB: scaled by 203/80, converted to BT.709 primaries (a BT.2020 primary comes out with a negative component, which the panel's wider gamut can show), and clamped to the display's peak (`ctx.HdrPeakScRgb`). Its rectangle is punched out of the SDR layer, so UI drawn after it lies over it. In an 8-bit target (an SDR window, a cached layer, a thumbnail) the image is tone-mapped instead, per channel: identity up to 0.75 of reference white, then an extended Reinhard roll-off that reaches exactly white at `sourcePeakNits`, then the sRGB encode (`HdrColor.ToneMap`; the shader `hdrimage.frag` is a transcription of the tested CPU maths in `HdrColor`). The CPU maths, the units (`HdrColor.ReferenceWhiteNits`, `ScRgbUnitNits`) and the primaries matrices are public, so an app can convert its own data the same way.

**Display facts.** `SdlVulkanWindow.HdrDisplay` is what SDL says about the display the window is on: HDR on (both the window's and the display's property, since on Windows the window's follow the HDR desktop), SDR white in scRGB units and the headroom above it. They are read once when the window is added to the `SdlEventLoop` and again on SDL's display-changed and HDR-state-changed events, never per frame, and handed to the context (`ctx.ApplyHdrDisplay`), which keeps `SdrWhiteScRgb` and `HdrHeadroom` current. When the display changes and the new surface offers scRGB where the swapchain has none (or no longer does), the swapchain is rebuilt through the resize path and adopts or drops scRGB. `HdrDisplayChanged` on the window is the event an app can listen to.

**See it, and measure it.** `tools/HdrProbe` states whether this machine can present HDR (`dotnet run`), and `-- --show` puts it on the panel: scRGB patches drawn with raw Vulkan, then, when that window is closed, the library's own ramp and BT.2020 primaries through `DrawHdrImage`. Judge both on the panel, not in a screenshot (Windows captures to SDR). `tools/SdrFrameBench` is the headless proof that an SDR app is not slower: a fixed UI scene (rectangles, rounded rectangles, ellipses, strokes, text, images) on an offscreen context, printing CPU, GPU (`VulkanContext.GpuTiming`) and wall ms per frame, mean and 95th percentile; exit 2 with no Vulkan device. It references the library by the `RendererProject` build property, so the same bench builds against another source tree (`git archive` a base into a folder and pass that tree's `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj`), which is how a before and after are taken in one sitting.

```
dotnet run --project tools/SdrFrameBench -c Release -- --frames 300
```

## Native WebView (optional)

Host a native browser view *inside* an `SdlVulkanWindow`. The window system composites it
over the Vulkan swapchain as a child surface — it does **not** go through the Vulkan render
pass. Shipped as separate packages so core renderer consumers pull no webview dependency:

| Package | Purpose |
| --- | --- |
| [`SdlVulkan.Renderer.WebView`](https://www.nuget.org/packages/SdlVulkan.Renderer.WebView) | The `INativeWebView` abstraction + platform backends. Multi-targets `net10.0` (interface, factory + the Linux/WebKitGTK backend) and `net10.0-windows` (adds the Windows/WebView2 backend + its native dependency). |
| [`SdlVulkan.Renderer.WebView.Native`](https://www.nuget.org/packages/SdlVulkan.Renderer.WebView.Native) | `WebView2Loader.dll` native assets per Windows RID (x64 / arm64 / x86). Pulled in transitively on Windows — no need to reference it directly. The loader is Microsoft's: an app that ships it ships the package's `THIRD-PARTY-NOTICES.txt` too. |

```
dotnet add package SdlVulkan.Renderer.WebView
```

Backends:
- **Windows** — `Win32WebView`, WebView2 (Edge/Chromium) via the [WebView2Aot](https://www.nuget.org/packages/WebView2Aot) Native-AOT bindings. Needs the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) installed: that runtime is a system component and is **not** redistributed here; only the loader DLL ships (in the `.Native` package).
- **Linux** — `GtkWebView`, WebKitGTK 4.1 embedded via X11 (`XReparentWindow` into SDL's window; GTK runs its own loop on a dedicated thread). Requires the SDL window on the X11 driver — run with `SDL_VIDEODRIVER=x11` (works under Wayland sessions via XWayland) — and the WebKitGTK 4.1 + GTK3 runtime (`apt install libwebkit2gtk-4.1-0` on Debian/Ubuntu; present on most desktops).
- **macOS** — `WKWebView`; backend is present but not yet implemented.

```csharp
using SdlVulkan.Renderer;
using SdlVulkan.Renderer.WebView;
using DIR.Lib;

using var window = SdlVulkanWindow.Create("App", 1280, 800);
using var web = NativeWebView.Create();            // platform backend via the factory

web.NavigationCompleted += url => Console.WriteLine($"loaded {url}");
web.ConsoleMessage += (level, text) => Console.WriteLine($"[{level}] {text}");
web.PageError += err => Console.WriteLine($"JS error: {err}");

web.AttachToWindow(window);                         // parents the webview into the window's HWND
web.Navigate("https://example.com");
window.GetSizeInPixels(out var w, out var h);
web.SetBounds(new RectInt(new PointInt(w, h), new PointInt(0, 0)));   // window pixel coords

string href = await web.ExecuteScriptAsync("location.href");   // JSON-encoded result
```

For two-way native↔web interaction, `MessageReceived` surfaces the page's
`window.chrome.webview.postMessage(...)` calls (as raw JSON) and `PostMessage(json)` sends the
other way (the page receives it on `chrome.webview`'s `message` event) — build whatever
request/response protocol you want on top. The Linux backend injects a small `window.chrome.webview`
shim at document-start (mapping to WebKit's `messageHandlers`), so the **same page JS API works on
both backends**.

### Fit-to-content sizing

`SetBounds` is host→browser (you position the webview). For the reverse direction — letting the
page's content drive the webview's size — wrap it in a `WebViewContentSizer`. It injects a small
`ResizeObserver` reporter after each navigation, surfaces the page's content size (in **device
pixels**, ready for `SetBounds`) as `ContentSizeChanged`, and can drive the bounds for you:

```csharp
using var web = NativeWebView.Create();
using var sizer = new WebViewContentSizer(web);     // construct before navigating

// Grow/shrink the webview's height to fit the page; width stays fixed at `w`, top-left at (0,0).
sizer.EnableAutoSize(origin: new PointInt(0, 0), fixedExtent: new PointInt(w, 0), axis: AutoSizeAxis.Height);
sizer.ContentSizeChanged += size => Console.WriteLine($"content is {size.X}x{size.Y} device px");

web.AttachToWindow(window);
web.NavigateToString("<h1>I size myself</h1>");
```

`AutoSizeAxis.Height` is the default and the safe choice: a fixed width never reflows the page, so it
can't oscillate. `Width`/`Both` track the other axis but are prone to reflow feedback. The `__sdlLayout`
message key is **reserved** for this protocol — layout envelopes are consumed by the sizer and never
reach *its* `MessageReceived`, so subscribe to `sizer.MessageReceived` to get only your app's messages.
No `INativeWebView` change is involved; the sizer is built entirely on the existing message bridge.

Beyond navigation, both backends surface diagnostics: `Trace` (redirect/load chain), `ConsoleMessage`
(`console.*`), and `PageError` (uncaught JS exceptions, with stack). On Windows these come from the
WebView2 DevTools Protocol; on Linux from in-page hooks forwarded over dedicated script-message
channels.

Backend notes:
- **Windows** — WebView2's async creation completes on the Win32 message pump, which SDL's event
  loop drives, so it composes with the standard render/event loop. Requires an STA thread. Events
  are raised on the UI thread.
- **Linux** — GTK runs its own main loop on a dedicated thread; public methods are safe to call from
  the SDL event-loop thread (they marshal across via `g_idle_add`). Events (`MessageReceived`,
  `TitleChanged`, …) are raised on the **GTK thread**, so handlers must be thread-safe or marshal back
  themselves.

## Debug inspector: screenshot and resize

The `#if DEBUG` `DebugInspector` (client: `SdlVulkan.Renderer.Inspector`, which also has a `selftest` mode) answers
these window verbs:

- `screenshot` un-minimises a minimised window itself, waits for the first presented frame and captures, and the
  reply says it did so. `reminimize: true` puts the window back minimised afterwards. If no frame is presented
  within a short budget it fails at once with "the window presented no frame: is the display off?".
- `resize` takes `width` and `height` in window pixels, un-maximises if needed, sets the client size clamped to the
  display work area, and answers once the next frame at that size has been laid out.
- `screenshot` with optional `width` and `height` resizes, captures at that size and puts the window back as it was
  (maximised, its floating size, or minimised), so a layout can be captured at real window sizes. The size is
  clamped to the work area like `resize`, so the reply's width and height are what was actually captured.

## Debug twin package (`SdlVulkan.Renderer.Debug`)

The in-process `DebugInspector` is `#if DEBUG`, and a published `SdlVulkan.Renderer` is built Release, so a
Debug build of a consumer that restored the package has no inspector. `SdlVulkan.Renderer.Debug` is the same
library packed from a Debug build at the same version; it depends on `DIR.Lib.Debug` rather than `DIR.Lib`.
Switch by configuration:

```xml
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="SdlVulkan.Renderer.Debug" Version="7.76.*" />
</ItemGroup>
<ItemGroup Condition="'$(Configuration)' != 'Debug'">
  <PackageReference Include="SdlVulkan.Renderer" Version="7.76.*" />
</ItemGroup>
```

Restore after switching configuration (Visual Studio does not do it for you). Never reference both packages.

## Dependencies

- [DIR.Lib](https://www.nuget.org/packages/DIR.Lib) — Rendering primitives + pure-managed glyph rasterization (no native dependency)
- [SDL3-CS](https://www.nuget.org/packages/SDL3-CS) — SDL3 bindings
- [SDL3-CS.Android](https://www.nuget.org/packages/SDL3-CS.Android) — android TFM only: SDL Java bridge + per-ABI `libSDL3.so` (see [Android](#android) for the version pin)
- [Vortice.Vulkan](https://www.nuget.org/packages/Vortice.Vulkan) — Vulkan bindings

Not a runtime dependency: [Vortice.ShaderCompiler](https://www.nuget.org/packages/Vortice.ShaderCompiler) (GLSL to SPIR-V) is used only by `tools/BakeShaders`, which is run by hand and its output committed — no build here invokes it. Shaders ship pre-baked as embedded SPIR-V, which keeps the shaderc native — it has no Android RID — out of the package entirely. A build only *checks* the bake is current, via warning SVR0001.

## License

MIT

## Rationale: Why SDL3 + Vortice.Vulkan

This library exists because the TianWen project needed a path to HDR display output
(HDR10, scRGB, wide color gamut) that its previous Silk.NET + OpenGL stack could not
deliver. The investigation below documents the alternatives considered and why
SDL3 (via edwardgushchin/SDL3-CS) + Vortice.Vulkan won. This is decision rationale,
not documentation of current features — check the types list above for what the
library actually does today.

### Why OpenGL was a dead end for HDR

- GPU vendors (NVIDIA, AMD) block 10-bit and floating-point pixel formats for OpenGL
  in windowed mode.
- The Windows HDR compositor (DWM) requires a DXGI swapchain, which only DirectX can
  drive natively.
- GLFW has no HDR support — [glfw issue #890](https://github.com/glfw/glfw/issues/890)
  open since 2016, never implemented.
- GLFW 3.4 (Feb 2024) shipped without it; a proposed `GLFW_FLOAT_PIXEL_TYPE` patch was
  never merged.
- Silk.NET's `WindowHintBool` ends at `SrgbCapable` / `DoubleBuffer` — no float pixel
  type or HDR color space.

### Why Vulkan

Vulkan supports HDR output via `VK_EXT_swapchain_colorspace` + HDR10 surface formats.

| Platform | OpenGL | Vulkan | HDR possible? |
|----------|--------|--------|---------------|
| Windows  | Native | Native | Yes (Vulkan HDR swapchain) |
| Linux    | Native | Native | Yes (if compositor supports) |
| macOS    | Deprecated (frozen at 4.1) | MoltenVK | No (Metal HDR needs separate path) |
| Android  | OpenGL ES | Native | Yes (Android 10+) |
| iOS      | OpenGL ES (deprecated) | MoltenVK | No (same as macOS) |
| Web/WASM | WebGL  | **No**  | No |

**Shader migration effort: low.** GLSL shaders compile to SPIR-V with minimal mechanical
changes (`#version 330 core` to `#version 450`, uniforms packed into UBOs, explicit
`layout(binding=N)`, build-time compile via `glslc` / `glslangValidator` or runtime
via `Vortice.ShaderCompiler`). Shader math (MTF stretch, Hermite soft-knee, WCS
deprojection, histogram) stays identical.

**API migration effort: high.** The real work was replacing ~2000 lines of OpenGL API
calls with swapchain setup, descriptor sets, pipeline objects, command buffers, and
synchronization.

### Silk.NET status at the time of the decision

- v2.23.0 (Jan 2026) — stable, quarterly maintenance releases.
- 3.0: `develop/3.0` branch exists, tracking issue [#209](https://github.com/dotnet/Silk.NET/issues/209)
  open since June 2020 (5.5+ years). Complete rewrite of bindings generation, no release
  date, lead developer (Perksey) less active, WebGPU bindings planned.
- Silk.NET's prior usage in TianWen was well-contained: 4 source files, 3 NuGet packages,
  AOT with trimmer warning suppressions.
- Verdict: not dead, but 3.0 had been in development for years; 2.x worked fine for OpenGL
  but was a dead end for Vulkan/HDR on any near-term horizon.

### Known Silk.NET-side blockers

- **macOS regression**: Silk.NET 2.21+ cannot create GLFW Vulkan windows on macOS
  ([#2440](https://github.com/dotnet/Silk.NET/issues/2440)); 2.20 worked.
- **MoltenVK not fully conformant**: translates Vulkan to Metal, supports Vulkan 1.4 but
  some features missing; HDR swapchain extensions may not be implemented.
- **Web target lost**: Vulkan has no browser support (WebGPU would be the path forward).

### Alternatives evaluated (March 2026)

#### Veldrid — avoid (dead project)

- Last commit: March 2024. Latest NuGet: v4.9.0 (Feb 2023). 159 open issues.
- Clean abstraction (Vulkan, D3D11, Metal, OpenGL) but author (mellinoe) has moved on.
- Targets .NET 6 / netstandard2.0, not .NET 10. No AOT testing. No HDR.

#### Avalonia + GPU interop — consider only if full UI rewrite desired

- 30K+ stars, extremely active. .NET 10 supported.
- Has `GpuInterop` sample with Vulkan demo via `CompositionDrawingSurface`.
- Gives a proper UI framework (menus, panels, dialogs) — could replace hand-built
  text/panel rendering.
- But: GPU interop is low-level (manage your own Vulkan context inside a compositor
  callback). HDR depends on the SkiaSharp compositor pipeline (no HDR). Very high
  migration effort, only worth it if replacing the hand-built UI.

#### SDL3 (.NET bindings)

- SDL3 itself: 15K stars, actively developed, battle-tested.
- Three competing .NET bindings: **ppy/SDL3-CS** (osu! team, most production-tested),
  **edwardgushchin/SDL3-CS**, **flibitijibibo/SDL3-CS**.
- SDL3 has native Vulkan surface creation + `SDL_GPU` abstraction (Vulkan/D3D12/Metal
  with automatic shader cross-compilation).
- HDR output support at the windowing level.
- **SDL3 + keep OpenGL**: replaces only GLFW windowing/input. Medium effort. But HDR
  is still blocked because SDL3's OpenGL renderer hardcodes `SDL_COLORSPACE_SRGB`.
- **SDL3 + SDL_GPU**: higher-level Vulkan-like API with shader translation. Medium-high
  effort.

#### Evergine Vulkan.NET — best raw Vulkan bindings, no ecosystem

- 284 stars. Source-generated from Vulkan headers (always up-to-date).
- Targets .NET 8+. Full HDR access via raw swapchain formats.
- Raw bindings only — no windowing, no VMA, no shader compiler. Very high migration
  effort; 5-10x more code than OpenGL for the same result.

#### Vortice.Vulkan — best raw Vulkan ecosystem (chosen)

- 371 stars (Vulkan), 1.1K stars (Windows/D3D). Last commit: Feb 2026. Only 2 open issues.
- Explicitly targets net9.0 + net10.0. Pure managed C# bindings (`delegate* unmanaged`
  function pointers, no P/Invoke). `IsAotCompatible = true`.
- Bundles VMA (Vulkan Memory Allocator), SPIRV-Cross, and shaderc as companion packages.
- Caveat: single maintainer (bus factor of 1).

#### WebGPU via wgpu-native — future option, not ready

- wgpu-native: 1.2K stars. Translates to Vulkan/D3D12/Metal.
- .NET bindings immature (Evergine WebGPU.NET Nov 2025, WebGPUSharp 14 stars).
- Shader language is WGSL (GLSL would need porting). HDR not yet in WebGPU spec.
- Revisit when .NET bindings mature.

### Why Vortice.Vulkan + edwardgushchin/SDL3-CS

edwardgushchin/SDL3-CS uses `LibraryImport` (source-generated, AOT-safe) — preferred
over ppy/SDL3-CS which uses old `DllImport`. `SDL3-CS.Native` NuGet ships desktop
natives; Android works but needs manual lib bundling.

| Platform | Vulkan | SDL3 native | AOT | HDR |
|----------|--------|-------------|-----|-----|
| Windows x64 | Native | NuGet | Yes | Yes (Vulkan HDR swapchain) |
| Windows ARM64 | Native | NuGet | Yes | Yes from Qualcomm Adreno driver 31.0.170.0 (scRGB: `R16G16B16A16_SFLOAT` + `EXTENDED_SRGB_LINEAR_EXT`); the OEM-channel 31.0.137.0 offered no `VK_EXT_swapchain_colorspace` at all. Both probed 2026-09-15 with `tools/HdrProbe` on a Surface Pro 11 |
| Linux x64 | Native (Mesa/NVIDIA) | NuGet | Yes | Possible (Wayland + Vulkan) |
| Linux ARM64 | Native (Mesa) | NuGet | Yes | Limited |
| macOS x64 | MoltenVK | NuGet | Yes | MoltenVK limitations |
| macOS ARM64 | MoltenVK | NuGet | Yes | MoltenVK limitations |
| Android | Native | Manual bundling | Partial | Yes |
| iOS | MoltenVK (must bundle) | Not shipped | Yes | Limited |

SDL3 HDR support: `SDL.window.HDR_enabled`, `SDL.window.SDR_white_level`,
`SDL.window.HDR_headroom` display properties, plus PQ (ST 2084) and HLG transfer
characteristics. Combined with Vulkan `VK_COLOR_SPACE_HDR10_ST2084_EXT` swapchain,
full HDR output is achievable.

Whether it is achievable on a GIVEN machine is a fact about that machine's Vulkan driver, and
`tools/HdrProbe` states it (`dotnet run` in that folder): the loader's instance extensions,
SDL3's HDR properties per display and per window, and every surface format and colour space each
physical device offers on a surface created on each display. Exit code 0 when some display can take
an HDR swapchain, 2 when none can. `dotnet run -- --show` then puts it on the panel: an scRGB
swapchain on the first capable display with patches at 0.25 to 2.0 times SDR white and at the
panel's peak, the primaries at both, and a ramp, drawn with clear rectangles so nothing but the
colour space sits between the numbers and the screen. Judge it on the panel: a screenshot goes
through Windows' SDR capture path and clips everything above 1.0 to 255 (measured 2026-09-15, the
patches from 1.0 up all read 255 while the OLED showed each brighter than the last). Closing that
window (Escape) opens a second one drawn through the library, for the same eyes: a float ramp and
the BT.2020 primaries through `DrawHdrImage` on a context created with `hdr: true`, see "HDR"
above. The library creates SRGB_NONLINEAR swapchains unless a context opts in to scRGB.

SDL3 Vulkan surface creation: `SDL.VulkanLoadLibrary()` auto-finds MoltenVK on macOS;
`SDL.VulkanCreateSurface()` returns a `VkSurfaceKHR` that pairs directly with
Vortice.Vulkan.

### Could we have stayed on Silk.NET by fixing it upstream?

#### macOS Vulkan regression (#2440) — small PR, uncertain merge timeline

GLFW 3.4 changed Vulkan detection on macOS; `glfwVulkanSupported()` can't find the
Vulkan loader even though Silk.NET ships it (`Silk.NET.Vulkan.Loader.Native`). GLFW 3.4
added `glfwInitVulkanLoader()` which could solve this.

Possible fixes: call `glfwInitVulkanLoader()` with a custom `vkGetInstanceProcAddr`
before `glfwInit()`; set `VK_ICD_FILENAMES` at the bundled MoltenVK ICD; ensure the
Vulkan loader is on `DYLD_LIBRARY_PATH`.

Status: no PRs submitted, zero maintainer engagement on the issue. Silk.NET 2.x is
in maintenance mode (14-month gap between 2.22 and 2.23), team focused on 3.0. Trivial
PRs merge in 0-11 days; no evidence of substantive external feature PRs merging
recently.

#### HDR - not feasible within Silk.NET's GLFW-based windowing

- GLFW has no API for HDR pixel formats, transfer functions, or color spaces.
- GLFW's own HDR issue ([#890](https://github.com/glfw/glfw/issues/890)) open since 2016.
- Silk.NET's Vulkan bindings already cover all HDR swapchain extensions — the blocker
  is purely windowing.
- Would require replacing GLFW with SDL3 as windowing backend (huge change) or
  platform-specific code.

| Path | macOS fix | HDR | Effort | Risk |
|------|-----------|-----|--------|------|
| Fix Silk.NET upstream | Small PR, may wait months | **Blocked by GLFW** | Low for macOS, impossible for HDR | PR rot |
| Vortice.Vulkan + SDL3-CS | SDL3 auto-detects MoltenVK | Full HDR built into SDL3 | High (rewrite renderer) | Two active projects |

### Comparison matrix

| Option | Maintenance | Vulkan | HDR | AOT | Migration | Shaders kept? |
|--------|------------|--------|-----|-----|-----------|---------------|
| Silk.NET 2.x (stay) | Moderate | Via 3.0 someday | No | Yes | None | Yes |
| Silk.NET 2.x + macOS PR | Moderate | Yes (with fix) | No | Yes | None | Yes |
| SDL3 + OpenGL | Excellent | Surface only | **No** | Yes | Medium | **Yes** |
| SDL3 + SDL_GPU | Excellent | Under the hood | Possible | Yes | Medium-high | Rewrite to SDL_GPU |
| **Vortice.Vulkan + SDL3** (chosen) | Good | Full | **Yes** | **Yes** | Very high | GLSL to SPIR-V |
| Evergine Vulkan.NET + SDL3 | Excellent | Full | **Yes** | **Yes** | Very high | GLSL to SPIR-V |
| Avalonia + Vulkan interop | Excellent | Yes (interop) | No | Improving | Very high | Rewrite |
| WebGPU/wgpu | Weak (.NET) | Under the hood | Not yet | Possible | High | GLSL to WGSL |

SDL3 + OpenGL HDR was corrected to **No** during evaluation: SDL3's OpenGL renderer
hardcodes `SDL_COLORSPACE_SRGB` as the only accepted output. No float pixel formats,
no scRGB, no HDR10 via OpenGL on any platform.

### What to watch

- **SDL3_GPU maturity** — could simplify Vulkan over time.
- **WebGPU .NET bindings** — if they mature, a future browser target becomes possible.
- **Silk.NET 3.0** — if it ever ships with working Vulkan + a non-GLFW windowing path,
  the comparison may change.
- **`SDL3-CS.Android` releases** — 3.4.16 cleared *both* Android constraints that had held the pin
  at 3.4.10.5, so the thing to watch now is that a future bump does not reintroduce either. Run the
  two package checks in the Android section above before taking one, and still launch it: the
  version check fails at startup, not at build time.

### Hidden views

A host that hides its window for hours (a tray app started minimised) need not keep the browser's full memory
meanwhile. Two calls on `INativeWebView`, both for a view you have hidden with `SetVisible(false)`:

```csharp
web.SetVisible(false);
web.SetMemoryUsageTarget(low: true);        // trim memory; safe even while media plays
if (await web.TrySuspendAsync())            // only for a paused page: stops its script and timers
    Console.WriteLine("suspended");
...
web.SetMemoryUsageTarget(low: false);       // shown again
web.SetVisible(true);                       // WebView2 resumes a suspended view when it is shown
```

- **Windows (WebView2):** `SetMemoryUsageTarget` is `CoreWebView2.MemoryUsageTargetLevel` (`Low` / `Normal`). It can be
  called before `AttachToWindow`: the level is held and applied when the browser is up; a runtime too old to have it
  is reported on `Trace`. `TrySuspendAsync` is `TrySuspend` and returns `false` for a visible view, one not attached
  yet, or when the browser refuses (a page playing media, for one). `Resume` wakes it while it stays hidden; showing the
  view or running script in it does that on its own.
- **Linux (WebKitGTK):** no runtime equivalent (its memory-pressure settings are fixed when the web context is
  created), so `SetMemoryUsageTarget` and `Resume` do nothing and `TrySuspendAsync` returns `false`.
