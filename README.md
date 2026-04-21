# SdlVulkan.Renderer

SDL3 + Vortice.Vulkan rendering library built on [DIR.Lib](https://github.com/SharpAstro/DIR.Lib) primitives.

## Types

- **`SdlVulkanWindow`** — SDL3 window with Vulkan instance and surface lifecycle. Creates maximized, resizable windows with Vulkan surface.
- **`VulkanContext`** — Vulkan device, command buffers, sync management. `MaxFramesInFlight = 2` with per-frame vertex buffers. Two construction modes:
  - `VulkanContext.Create(instance, surface, w, h, ...)` — on-screen path with a swapchain tied to an `SdlVulkanWindow`.
  - `VulkanContext.CreateOffscreen(instance, w, h, ...)` — headless path rendering to a standalone `VkImage`; no surface, no swapchain, no SDL window. See **Headless / offscreen rendering** below.
- **`VkRenderer`** — `Renderer<VulkanContext>` implementation with FillRectangle, DrawRectangle, FillEllipse, DrawText, plus batched glyph and persistent-vertex-buffer draw APIs. Exposes `FontAtlasDirty` so callers can trigger redraws after glyph rasterization. Has `BeginFrame` / `BeginOffscreenFrame` variants that match the two `VulkanContext` modes.
- **`VkPipelineSet`** — GLSL 450 shader compilation and Vulkan pipeline creation (flat, textured, ellipse, stroke, SDF, blend variants).
- **`VkFontAtlas`** — Dynamic bitmap glyph atlas with ManagedFontRasterizer (from DIR.Lib) rasterization and Vulkan texture upload. Supports grow (512→4096), deferred eviction, and `skipUnflushed` to prevent sampling stale GPU texture data.
- **`VkSdfFontAtlas`** — Signed-distance-field glyph atlas side-car for resolution-independent text. `SdfRasterSize = 128`, `fwidth`-driven AA in the fragment shader auto-tunes to ±0.5 screen pixels at any zoom. Single-channel R8_Unorm texture, keyed on `(font, size, character, charCode)` so CID subset fonts don't collide.

## Font Atlas Lifecycle

Per frame:
1. `BeginFrame()` / `BeginOffscreenFrame()` — handles deferred eviction, runs `OnPreFlush` (pre-warm callback), calls `Flush(cmd)` on both atlases, runs `OnPreRenderPass` (texture uploads), then `BeginRenderPass`.
2. `Flush(cmd)` — uploads dirty staging region to GPU via `vkCmdCopyBufferToImage`.
3. `DrawText(...)` → `GetGlyph(...)` — cache hit returns UV coords; miss rasterizes into staging.
4. `GetGlyph(..., skipUnflushed: true)` — in draw loops, returns zero-width for glyphs not yet uploaded. Pair with `PreWarmGlyph` in `OnPreFlush` if drawing a glyph that wasn't shown last frame (first-frame glyph flicker).

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

## Dependencies

- [DIR.Lib](https://www.nuget.org/packages/DIR.Lib) — Rendering primitives + FreeType glyph rasterization
- [SDL3-CS](https://www.nuget.org/packages/SDL3-CS) — SDL3 bindings
- [Vortice.Vulkan](https://www.nuget.org/packages/Vortice.Vulkan) — Vulkan bindings
- [Vortice.ShaderCompiler](https://www.nuget.org/packages/Vortice.ShaderCompiler) — GLSL to SPIR-V

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
| Windows ARM64 | Native | NuGet | Yes | Yes |
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
