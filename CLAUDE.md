# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
dotnet build                                # Debug build
dotnet build -c Release                     # Release build
dotnet pack -c Release                      # Build + produce .nupkg
dotnet test src/SdlVulkan.Renderer.Tests    # Run the offscreen-Vulkan regression tests
```

The test project (`SdlVulkan.Renderer.Tests`, xunit.v3 + Shouldly) renders through a headless offscreen Vulkan context, so it needs a Vulkan ICD at runtime; with none available the tests `Assert.Skip` rather than fail. No linter is configured.

## Project Overview

The solution is centered on `SdlVulkan.Renderer`, a .NET 10 library providing a 2D rendering API on top of SDL3 + Vulkan. It targets AOT compatibility and uses `unsafe` code extensively for raw Vulkan interop via Vortice.Vulkan bindings. Alongside it:

- `SdlVulkan.Renderer.WebView` / `SdlVulkan.Renderer.WebView.Native` — optional native-webview-in-window packages (WebView2 on Windows, WebKitGTK on Linux). Shipped separately so core consumers pull no webview dependency. See README "Native WebView".
- `SdlVulkan.Renderer.Inspector` — a debug-inspector tool/client (pairs with `DebugInspector` in the core lib).
- `tools/WebViewSmoke` — headless self-test exercised by CI.

The core library, `WebView`, and `WebView.Native` are published as NuGet packages (`GeneratePackageOnBuild`). CI/CD (`.github/workflows/dotnet.yml`) builds on push/PR to main, runs the offscreen test matrix (`ubuntu-latest` + `ubuntu-24.04-arm` with Mesa lavapipe) and a non-gating Linux WebView smoke job under Xvfb, and publishes to nuget.org on main push.

## Package Management

Central package versioning via `src/SdlVulkan.Renderer/Directory.Packages.props`. All package versions are pinned there — update versions in that file, not in the `.csproj`.

## Versioning

The package version is `Major.Minor.RunNumber` where `RunNumber` is the CI build number. Two places must stay in sync when bumping:
- `VersionPrefix` in `src/SdlVulkan.Renderer/SdlVulkan.Renderer.csproj` — used for local builds (`Major.Minor.0`)
- `VERSION_PREFIX` in `.github/workflows/dotnet.yml` — used for CI builds (`Major.Minor.${{ github.run_number }}`)

## DIR.Lib Dependency

DIR.Lib provides the rendering primitives, font rasterization, and widget interfaces that this library builds on. It is published via NuGet but its source lives in a sibling directory (`../DIR.Lib`). When fixing bugs or adding features, always prefer pushing changes upstream to DIR.Lib rather than working around its limitations in this repo.

The `.csproj` uses a conditional ProjectReference: when `../DIR.Lib/` exists locally it builds in-tree (no NuGet needed); CI falls back to the NuGet `PackageReference` automatically. Same pattern DIR.Lib uses for its `../Fonts.Lib` dependency. When pushing new API upstream:

1. Commit & push **Fonts.Lib** (if changed) → CI publishes to NuGet
2. Update `SharpAstro.Fonts` version in DIR.Lib's `src/Directory.Packages.props`, commit & push → CI publishes DIR.Lib to NuGet
3. Update `DIR.Lib` version in `src/SdlVulkan.Renderer/Directory.Packages.props`, commit & push

## Architecture

**Rendering pipeline flow:**
`SdlVulkanApp` (process-wide SDL lifecycle + shared `VkInstance` + shared `VulkanDevice` for a multi-window app) → `SdlVulkanWindow` (per-window SDL3 window + Vulkan surface) → `VulkanContext` (per-window swapchain, command buffers, per-frame sync with `MaxFramesInFlight = 2`, vertex ring; references a `VulkanDevice`) → `VkRenderer` (2D draw API: rectangles, ellipses, text, textures) → `VkPipelineSet` (10 pipelines compiled from GLSL 450 at runtime)

**Device vs. context split (6.0+):** device-level state (logical device, queue, command pool, render pass, descriptor pool/layout, pipeline layout, MSAA) lives in `VulkanDevice`; per-window state (swapchain, framebuffers, sync, vertex ring, command buffers) lives in `VulkanContext`. `VulkanContext` forwards the device-level members (`ctx.RenderPass`, `ctx.PipelineLayout`, etc.) so existing consumers keep working. Three construction paths:
- `SdlVulkanApp.CreateWindow` + `VulkanContext.CreateForSharedDevice` — multi-window; one `VulkanDevice` shared across windows (GPU resources stay valid in all of them, so a document tab can move between windows without re-uploading geometry).
- `VulkanContext.Create(instance, surface, …)` — single-window; the context creates and owns its own device.
- `VulkanContext.CreateOffscreen(instance, …)` — headless render-to-`VkImage`; no surface/swapchain/SDL window. Used by tests, thumbnail/raster workers, and CI without a display.

A context tears down the device only when it created it; shared-device windows leave teardown to the device owner (the `SdlVulkanApp`).

**Key design patterns:**
- **Push-constant-only uniforms** — no UBOs; all per-draw data (projection matrix, color, extra params) goes through an 84-byte push constant block
- **Single descriptor set layout** — one combined-image-sampler layout owned by `VulkanDevice` and shared by all pipelines; the font atlas gets a fixed set, each `VkTexture` allocates its own (descriptor pool max 512)
- **Per-frame vertex ring buffer** — two host-visible/coherent buffers (one per in-flight frame), written linearly and reset each `BeginFrame`
- **Deferred texture upload** — `VkTexture.CreateDeferred` + `RecordUpload` records GPU uploads into the frame command buffer before `BeginRenderPass`, avoiding `vkQueueWaitIdle` stalls
- **Font atlas lifecycle** — `VkFontAtlas` manages a growable bitmap glyph atlas (512→4096) with dirty-region staging upload; eviction is deferred one frame to prevent stale UV sampling; `skipUnflushed` guards draw loops from sampling unuploaded glyphs
- **Multi-page SDF atlas** — `VkSdfFontAtlas` is a list of fixed-size page textures (default 2048², R8_Unorm); a full page appends a new page instead of reallocating (no `vkDeviceWaitIdle` + realloc + re-upload stall), with per-page LRU eviction. Optional `SdfGlyphDiskCache` persists rasterized SDF glyphs across runs (bounded per-frame load drain)
- **Idle-suppressing event loop** — `SdlEventLoop` uses `WaitEventTimeout` when idle, throttles mouse-motion redraws to ~30 fps; supports multi-window
- **Live-device thumbnail capture** — `VkRenderer.BeginThumbnailCapture`/`EndThumbnailCapture`/`TryGetThumbnailCapture` re-issue already-tessellated geometry into an offscreen target at thumbnail scale with non-blocking readback (`VulkanContext.ThumbnailCapture`)

**Side-car (custom) pipeline pattern:**
Consumer projects (e.g., TianWen) can create their own Vulkan pipelines that render within
the same render pass. Two proven examples: `VkFitsImagePipeline` (image stretch + WCS grid)
and `VkSkyMapPipeline` (3D star/constellation rendering with stereographic projection).

To create a side-car pipeline:
1. Create your own `VkDescriptorSetLayout` + `VkPipelineLayout` (with your UBO/push constants)
2. Create `VkPipeline` using `ctx.RenderPass` and `ctx.MsaaSamples` (must match)
3. Compile GLSL 450 → SPIR-V at runtime using `Vortice.ShaderCompiler.Compiler`
4. Record draw commands via `renderer.CurrentCommandBuffer` between `BeginFrame`/`EndFrame`
5. Use `ctx.WriteVertices()` for per-frame geometry or `ctx.CreatePersistentVertexBuffer()`
   for static geometry. Instancing is supported — just call `vkCmdDraw(vertexCount, instanceCount, ...)`

The 84-byte push constant block is only a constraint if you use `ctx.PipelineLayout`.
Side-car pipelines with their own `VkPipelineLayout` can define any push constant layout.

**Key files:**
- `SdlVulkanApp.cs` — process-wide SDL + shared `VkInstance`/`VulkanDevice` for multi-window apps
- `SdlVulkanWindow.cs` — per-window SDL3 window + Vulkan surface
- `VulkanDevice.cs` — shared device-level state: queue, command pool, render pass, descriptor pool/layout, pipeline layout, MSAA
- `VkRenderer.cs` — high-level draw API, extends `Renderer<VulkanContext>` from DIR.Lib
- `VulkanContext.cs` — per-window swapchain/sync/vertex-ring lifecycle (references a `VulkanDevice`); partials: `VulkanContext.Offscreen.cs` (headless render-to-image), `VulkanContext.SwapchainReadback.cs`, `VulkanContext.ThumbnailCapture.cs`
- `VkFontAtlas.cs` — bitmap glyph rasterization cache + GPU texture management
- `VkSdfFontAtlas.cs` — multi-page SDF glyph atlas (R8_Unorm pages) for resolution-independent text
- `SdfGlyphDiskCache.cs` — opt-in on-disk cache of rasterized SDF glyphs
- `VkPipelineSet.cs` — GLSL→SPIR-V compilation + pipeline creation (Flat, Textured, Ellipse, Page, Stroke, SDF, blend variants)
- `VkTexture.cs` — per-image Vulkan texture with blocking and deferred upload modes
- `SdlEventLoop.cs` — event-driven (multi-window) render loop with resize handling
- `SdlInputMapping.cs` — SDL3 scancode/keymod → DIR.Lib `InputKey`/`InputModifier` mapping
- `DebugInspector.cs` / `DebugInspectorOptions.cs` — in-process debug inspector (client lives in `SdlVulkan.Renderer.Inspector`)
