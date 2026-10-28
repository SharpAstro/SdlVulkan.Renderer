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
- `tools/HdrProbe` — run by hand: can this machine present HDR through SDL3 + Vulkan, per display? Exit 0 yes, 2 no; `-- --show` puts scRGB patches on the capable panel, and only the panel can judge them (a screenshot clips at 1.0) (README, "Why Vulkan").
- `tools/TransparencyProbe` - run by hand, Windows only: does a transparent window actually show the desktop through, per device, present mode and window kind? `caps` (what Vulkan promises), `present all` (what the screen shows, scored against a checkerboard backdrop), `buddy app|standalone [native|opaque]` (the library's API end to end, timed; `native` keeps the window on the native device, a layered window on Windows; `opaque` gives it an ordinary context, the hard-edged companion). The display must be on and visible: a minimized Remote Desktop client captures black, reported as exit 3. `--look` holds each picture and asks the person at the panel, since nothing promises the capture shows what the panel does (on the Surface, asked picture by picture, they agreed: native Adreno opaque, Dozen blending) (README, "Transparent windows").

The core library, `WebView`, and `WebView.Native` are published as NuGet packages (`GeneratePackageOnBuild`). CI/CD (`.github/workflows/dotnet.yml`) builds on push/PR to main, runs the offscreen test matrix (`ubuntu-latest` + `ubuntu-24.04-arm` with Mesa lavapipe) a non-gating Linux WebView smoke job under Xvfb, and a non-gating `windows-latest` job (the same tests on mesa-dist-win's lavapipe, then the WebView2 smoke, where a SKIP fails), and publishes to nuget.org on main push.

## Package Management

Central package versioning via `src/SdlVulkan.Renderer/Directory.Packages.props`. All package versions are pinned there — update versions in that file, not in the `.csproj`.

## Versioning

The package version is `Major.Minor.RunNumber`, where `RunNumber` is the CI build number.

**A release bump is ONE line:** `<VersionMajorMinor>` in `src/Directory.Build.props`. Everything else
derives from it and must not be hand-edited — `VersionPrefix` (guarded on empty so CI's `-p:Version`
wins), `AssemblyVersion` as `$(VersionMajorMinor).0.0`, and CI's `VERSION_PREFIX`, which the build job
RESOLVES by reading the property back (`dotnet msbuild src/Directory.Build.props
-getProperty:VersionMajorMinor`) rather than restating it. So CI cannot stamp a version the packages
disagree with, and **no csproj declares a `VersionPrefix` of its own** — one that did would silently
win. (This doc used to describe a two-place scheme, csproj + workflow, that the repo has not used for
some time; the csproj property it named no longer exists.)

The matching release note goes in [CHANGELOG.md](CHANGELOG.md) at the repo root, in the same commit —
newest first, one `## Major.Minor` section each. (The notes used to live in a comment block in the
workflow's `env:`; nothing ever read them there, and they had grown to 329 of that file's 478 lines.)

## DIR.Lib Dependency

DIR.Lib provides the rendering primitives, font rasterization, and widget interfaces that this library builds on. It is published via NuGet but its source lives in a sibling directory (`../DIR.Lib`). When fixing bugs or adding features, always prefer pushing changes upstream to DIR.Lib rather than working around its limitations in this repo.

The `.csproj` uses a conditional ProjectReference: when `../DIR.Lib/` exists locally it builds in-tree (no NuGet needed); CI falls back to the NuGet `PackageReference` automatically. Same pattern DIR.Lib uses for its `../Fonts.Lib` dependency. When pushing new API upstream:

1. Commit & push **Fonts.Lib** (if changed) → CI publishes to NuGet
2. Update `SharpAstro.Fonts` version in DIR.Lib's `src/Directory.Packages.props`, commit & push → CI publishes DIR.Lib to NuGet
3. Update `DIR.Lib` version in `src/SdlVulkan.Renderer/Directory.Packages.props`, commit & push

## Architecture

**Rendering pipeline flow:**
`SdlVulkanApp` (process-wide SDL lifecycle + shared `VkInstance` + shared `VulkanDevice` for a multi-window app) → `SdlVulkanWindow` (per-window SDL3 window + Vulkan surface) → `VulkanContext` (per-window swapchain, command buffers, per-frame sync with `MaxFramesInFlight = 2`, vertex ring; references a `VulkanDevice`) → `VkRenderer` (2D draw API: rectangles, ellipses, text, textures, plus depth-tested meshes) → `VkPipelineSet` (12 pipelines built from pre-baked SPIR-V)

**Device vs. context split (6.0+):** device-level state (logical device, queue, command pool, render pass, descriptor pool/layout, pipeline layout, MSAA, depth format) lives in `VulkanDevice`; per-window state (swapchain, framebuffers, sync, vertex ring, command buffers) lives in `VulkanContext`. `VulkanContext` forwards the device-level members (`ctx.RenderPass`, `ctx.PipelineLayout`, etc.) so existing consumers keep working. Three construction paths:
- `SdlVulkanApp.CreateWindow` + `VulkanContext.CreateForSharedDevice` — multi-window; one `VulkanDevice` shared across windows (GPU resources stay valid in all of them, so a document tab can move between windows without re-uploading geometry).
- `VulkanContext.Create(instance, surface, …)` — single-window; the context creates and owns its own device.
- `VulkanContext.CreateOffscreen(instance, …)` — headless render-to-`VkImage`; no surface/swapchain/SDL window. Used by tests, thumbnail/raster workers, and CI without a display.

A context tears down the device only when it created it; shared-device windows leave teardown to the device owner (the `SdlVulkanApp`).

**Key design patterns:**
- **Push-constant-only uniforms** — no UBOs; all per-draw data (projection matrix, color, extra params) goes through an 84-byte push constant block
- **Single descriptor set layout** — one combined-image-sampler layout owned by `VulkanDevice` and shared by all pipelines; the font atlas gets a fixed set, each `VkTexture` allocates its own. `DescriptorSetsPerPool = 512` is the capacity of one pool, not a ceiling: a pool cannot be resized, so `AllocateDescriptorSet` chains a new one when the current fills, and `FreeDescriptorSet` recycles into a free-list rather than handing the set back to the driver (the handle stays valid for in-flight command buffers, and nothing has to track which pool issued it)
- **One shared sampler** — every `VkTexture` binds `VulkanDevice.LinearClampSampler`. Samplers carry no per-image state, so a sampler per texture bought nothing and ran image-dense scenes into `maxSamplerAllocationCount` (commonly 4096)
- **Per-frame vertex ring buffer** — two host-visible/coherent buffers (one per in-flight frame), written linearly and reset each `BeginFrame`
- **Deferred texture upload** — `VkTexture.CreateDeferred` + `RecordUpload` records GPU uploads into the frame command buffer before `BeginRenderPass`, avoiding `vkQueueWaitIdle` stalls
- **Font atlas lifecycle** — `VkFontAtlas` manages a growable bitmap glyph atlas (512→4096) with dirty-region staging upload; eviction is deferred one frame to prevent stale UV sampling; `skipUnflushed` guards draw loops from sampling unuploaded glyphs
- **Multi-page MTSDF atlas** — `VkSdfFontAtlas` is a list of fixed-size page textures (default 2048², R8G8B8A8Unorm: RGB carry pseudo-distance, which the shader medians to keep corners sharp, and A the true distance); a full page appends a new page instead of reallocating (no `vkDeviceWaitIdle` + realloc + re-upload stall), with per-page LRU eviction. DIR.Lib's optional `SdfGlyphDiskCache` persists rasterized SDF glyphs across runs (bounded per-frame load drain)
- **Idle-suppressing event loop** — `SdlEventLoop` uses `WaitEventTimeout` when idle, throttles mouse-motion redraws to ~30 fps; supports multi-window
- **Live-device thumbnail capture** — `VkRenderer.BeginThumbnailCapture`/`EndThumbnailCapture`/`TryGetThumbnailCapture` re-issue already-tessellated geometry into an offscreen target at thumbnail scale with non-blocking readback (`VulkanContext.ThumbnailCapture`)
- **One render-pass shape, in two variants (with a depth attachment and without), and depth allocated per target only once a mesh draws on it (7.75, #146)** — `VulkanDevice.CreateCompatibleRenderPass` builds every pass (swapchain, damage load pass, single-sample pass, offscreen, cached layer, thumbnail) as colour (0), [depth (1),] resolve (MSAA only) with the shared dependency pair, so the pre-baked pipelines bind into all passes *of the same variant*; a pass with the depth attachment and one without are not render-pass compatible, so each variant has a pass (`RenderPass` / `RenderPassNoDepth`, `SingleSampleRenderPass` / `SingleSampleRenderPassNoDepth`) and a `VkPipelineSet` (`VkPipelineSet.Create(..., withDepth)`; the depth-less set has no `Mesh`). The depth attachment is for `VkRenderer.BeginMeshRegion`/`DrawMesh`/`EndMeshRegion`: a mesh is drawn inline in whatever pass is open, antialiased by the pass's MSAA and clipped by the caller's clip, and its triangles sort through depth; the 2D pipelines carry a depth-stencil state that tests nothing, so painter's order still rules among them and a fill after a mesh covers it. Depth is cleared at pass start and again over each region (so two models never test against each other), never stored, never sampled — `TransientAttachment`, and placed in lazily allocated memory where the device has any (`VulkanDevice.FindTransientMemoryType`, which the multisample colour images use too), so a tiler never backs it; a desktop GPU takes device-local, and there it costs 16 bytes a pixel under 4x MSAA, which is why **no target has a depth image until it draws a mesh**: the swapchain framebuffers (with the damage load pass and the single-sample pass paired with them), the offscreen target, each cached-layer slot and the thumbnail capture all start depth-less. A target gets depth per target, up front with `VulkanContext.RequireMeshDepth(MeshDepthTargets)` / `VkRenderer.RequireMeshDepth` (`Main`, `CachedLayer`, `Thumbnail`, flags; offscreen also `CreateOffscreen(meshDepth: true)`), or lazily: a `BeginMeshRegion` on a depth-less pass returns false, draws nothing, marks that target and sets `VkRenderer.TakeMeshDepthRedraw` (which `SdlEventLoop` honours by redrawing), and the next `BeginFrame`/`BeginOffscreenFrame` (main target: drains the device, allocates the one shared depth image, rebuilds the framebuffers and load passes), `BeginCachedLayerPass` (per slot, old framebuffer through `DeferDestroy`) or `BeginThumbnailCapturePass` gives it depth. The shape then stays across resize and swapchain recreation, and `VkRenderer` keeps a pipeline set per shape and picks the one matching the pass it records (`CurrentPassHasDepth`). `VulkanContext.Depth.cs` owns the per-target depth state, the `MeshDepthTargets` flags and the framebuffer/clear-value helpers
- **Transparent windows are a per-window opt-in, and take the first way the machine has: a swapchain with alpha, on the native device or else on Dozen; a layered window; an opaque swapchain** - `SdlVulkanWindow.CreateTransparent` / `SdlVulkanApp.CreateTransparentWindow` set `SDL_WINDOW_TRANSPARENT`; `VulkanContext.CreateTransparent` / `CreateTransparentForSharedDevice` make the swapchain ask for `PRE_MULTIPLIED` composite alpha (`ChooseCompositeAlpha`), which the ordinary blend state already fills correctly over an alpha-0 clear (`PremultipliedOutputTests`). Windows' native drivers offer no premultiplied alpha, so there the window gets a second device: the one on the same GPU (adapter LUID) whose surface offers it, in practice Mesa's Dozen from Microsoft's compatibility pack (`VulkanDevice.Transparency.cs`; `SdlVulkanApp.DeviceFor` makes it once and shares it among transparent windows). The shared device is used whenever it offers premultiplied alpha itself, and opening a transparent window never changes which device is shared. No such device, or a context deliberately on the native one (`app.Device` rather than `DeviceFor`, `nativeDeviceOnly` standalone): on Windows a layered window (`VulkanContext.Layered.cs`, `LayeredWindow.cs`), whose context draws into an image of its own that stands in the swapchain's arrays (`_swapchainImages`, one image, no acquire, no present, no semaphores), so the whole swapchain frame machine (fence ledger, dropped frames, damage, recovery, resize as out-of-date) runs unchanged; `SubmitFrame` records the copy into a persistent host-cached buffer, waits the frame's fence (bounded, Timeout thrown like `BeginFrame`'s) and hands the BGRA to `UpdateLayeredWindow`, synchronously because the loop draws only on change. `ILayeredTarget` is the seam the tests use (`LayeredWindowFrameTests`, on the offscreen device, whose passes end in `ColorAttachmentOptimal`: `VulkanDevice.ColorFinalLayout` is what the load passes and the copy start from). `ChooseAlphaPresentation` is the order. Elsewhere, no such device: the swapchain is opaque (`ctx.AlphaPresentation` `None`), though NVIDIA under Mailbox and Intel under Fifo were seen to blend its alpha anyway on an SDL transparent window (off-spec, measured in an RDP session; the Adreno at its console blends it under neither; `tools/TransparencyProbe` measures it), and `SetShape` (a 1-bit `SDL_SetWindowShape` region) still cuts it out. A hard-edged companion needs only that, with no readback and no Dozen: an ordinary context on the transparent window (`CreateForSharedDevice` on `app.Device`, or `VulkanContext.Create` standalone)

**Side-car (custom) pipeline pattern:**
Consumer projects (e.g., TianWen) can create their own Vulkan pipelines that render within
the same render pass. Two proven examples: `VkFitsImagePipeline` (image stretch + WCS grid)
and `VkSkyMapPipeline` (3D star/constellation rendering with stereographic projection).

To create a side-car pipeline:
1. Create your own `VkDescriptorSetLayout` + `VkPipelineLayout` (with your UBO/push constants)
2. Create `VkPipeline` using `ctx.RenderPass` **or** `ctx.RenderPassNoDepth` and `ctx.MsaaSamples` (must match). **State which pass shape the side-car targets**: a pass has a depth attachment only once its target has drawn a mesh (every target starts without), and a pipeline is render-pass compatible with one shape only. A side-car that draws wherever the host does builds one pipeline per shape and binds the one matching `renderer.CurrentPassHasDepth` (true = `ctx.RenderPass`, false = `ctx.RenderPassNoDepth`; the single-sample pass has `SingleSampleRenderPass` / `SingleSampleRenderPassNoDepth`); one that is only ever drawn on a target it asked depth of (`RequireMeshDepth`) can build for `ctx.RenderPass` alone. Against the depth shape supply a `VkPipelineDepthStencilStateCreateInfo` (`ctx.DepthFormat`) — a pipeline created against such a pass must state one, and against the depth-less shape it is ignored; `depthTestEnable = false` for ordinary 2D drawing, or test against it (cleared to 1.0, so `Less`) if the side-car wants depth — which needs the depth shape. `VkMeshPipeline` is the in-tree example of the latter
3. Bring your own SPIR-V — this package bakes its shaders at build time and no longer references `Vortice.ShaderCompiler`, so a side-car wanting runtime GLSL 450 → SPIR-V has to take that package itself
4. Record draw commands via `renderer.CurrentCommandBuffer` between `BeginFrame`/`EndFrame`
5. Use `ctx.WriteVertices()` for per-frame geometry or `ctx.CreatePersistentVertexBuffer()`
   for static geometry. Instancing is supported — just call `vkCmdDraw(vertexCount, instanceCount, ...)`

The 84-byte push constant block is only a constraint if you use `ctx.PipelineLayout`.
Side-car pipelines with their own `VkPipelineLayout` can define any push constant layout.

**Key files:**
- `SdlVulkanApp.cs` — process-wide SDL + shared `VkInstance`/`VulkanDevice` for multi-window apps
- `SdlVulkanWindow.cs` — per-window SDL3 window + Vulkan surface
- `VulkanDevice.cs` — shared device-level state: queue, command pool, render pass (`CreateCompatibleRenderPass`, the one shape every pass takes, with and without depth), the descriptor pool chain + layout, the shared sampler, pipeline layout, MSAA, depth format; partial `VulkanDevice.Transparency.cs` (the device pick for a transparent window)
- `VkRenderer.cs` — high-level draw API, extends `Renderer<VulkanContext>` from DIR.Lib
- `VkMeshPipeline.cs` — the depth-testing lit-mesh pipeline behind `VkRenderer.DrawMesh`; its own 96-byte push-constant layout, held by `VkPipelineSet`
- `VulkanContext.cs` — per-window swapchain/sync/vertex-ring lifecycle (references a `VulkanDevice`); partials: `VulkanContext.Offscreen.cs` (headless render-to-image), `VulkanContext.SwapchainReadback.cs`, `VulkanContext.ThumbnailCapture.cs`, `VulkanContext.CachedLayer.cs`, `VulkanContext.Damage.cs` (repaint only what changed), `VulkanContext.Depth.cs` (per-target depth state, allocated only once a mesh draws on the target, + the framebuffer and clear-value helpers), `VulkanContext.DeferredDestroy.cs`, `VulkanContext.Layered.cs` (a transparent window as a layered window: the readback in place of the present; `LayeredWindow.cs` is the Win32 side)
- `VkFontAtlas.cs` — bitmap glyph rasterization cache + GPU texture management
- `VkSdfFontAtlas.cs` — multi-page MTSDF glyph atlas (R8G8B8A8Unorm pages) for resolution-independent text. This is the thin Vulkan backend of DIR.Lib's `SdfFontAtlas`; the glyph disk cache behind it lives there too, not here
- `VkPipelineSet.cs` — pipeline creation from the pre-baked SPIR-V embedded in the assembly (Flat, Textured, Ellipse, Page, Stroke, SDF, RoundRect, blend variants, Mesh). Shaders are authored as GLSL 450 in `Shaders/*.vert|*.frag` and baked to `Shaders/spirv/*.spv` by `tools/BakeShaders`; re-run it after editing one and commit the `.spv`
- `VkTexture.cs` — per-image Vulkan texture with blocking and deferred upload modes
- `SdlEventLoop.cs` — event-driven (multi-window) render loop with resize handling
- `SdlInputMapping.cs` — SDL3 scancode/keymod → DIR.Lib `InputKey`/`InputModifier` mapping
- `DebugInspector.cs` / `DebugInspectorOptions.cs` — in-process debug inspector (client lives in `SdlVulkan.Renderer.Inspector`)
