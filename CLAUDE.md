# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
dotnet build                          # Debug build
dotnet build -c Release               # Release build
dotnet pack -c Release                # Build + produce .nupkg
```

No test project exists. No linter is configured.

## Project Overview

A .NET 10 library (`SdlVulkan.Renderer`) providing a 2D rendering API on top of SDL3 + Vulkan. It targets AOT compatibility and uses `unsafe` code extensively for raw Vulkan interop via Vortice.Vulkan bindings.

Published as a NuGet package. CI/CD (`.github/workflows/dotnet.yml`) builds on push/PR to main and publishes to nuget.org on main push.

## Package Management

Central package versioning via `src/SdlVulkan.Renderer/Directory.Packages.props`. All package versions are pinned there — update versions in that file, not in the `.csproj`.

## DIR.Lib Dependency

DIR.Lib provides the rendering primitives, font rasterization, and widget interfaces that this library builds on. It is published via NuGet but its source lives in a sibling directory (`../DIR.Lib`). When fixing bugs or adding features, always prefer pushing changes upstream to DIR.Lib rather than working around its limitations in this repo.

The `.csproj` uses a conditional ProjectReference: when `../DIR.Lib/` exists locally it builds in-tree (no NuGet needed); CI falls back to the NuGet `PackageReference` automatically. Same pattern DIR.Lib uses for its `../Fonts.Lib` dependency. When pushing new API upstream:

1. Commit & push **Fonts.Lib** (if changed) → CI publishes to NuGet
2. Update `SharpAstro.Fonts` version in DIR.Lib's `src/Directory.Packages.props`, commit & push → CI publishes DIR.Lib to NuGet
3. Update `DIR.Lib` version in `src/SdlVulkan.Renderer/Directory.Packages.props`, commit & push

## Architecture

**Rendering pipeline flow:**
`SdlVulkanWindow` (SDL3 window + Vulkan instance/surface) → `VulkanContext` (device, swapchain, command buffers, per-frame sync with `MaxFramesInFlight = 2`) → `VkRenderer` (2D draw API: rectangles, ellipses, text, textures) → `VkPipelineSet` (10 pipelines compiled from GLSL 450 at runtime)

**Key design patterns:**
- **Push-constant-only uniforms** — no UBOs; all per-draw data (projection matrix, color, extra params) goes through an 84-byte push constant block
- **Single descriptor set layout** — one combined-image-sampler layout shared by all pipelines; font atlas gets a fixed set, each `VkTexture` allocates its own (pool max 512)
- **Per-frame vertex ring buffer** — two host-visible/coherent buffers (one per in-flight frame), written linearly and reset each `BeginFrame`
- **Deferred texture upload** — `VkTexture.CreateDeferred` + `RecordUpload` records GPU uploads into the frame command buffer before `BeginRenderPass`, avoiding `vkQueueWaitIdle` stalls
- **Font atlas lifecycle** — `VkFontAtlas` manages a growable glyph atlas (up to 4096x4096) with dirty-region staging upload; eviction is deferred one frame to prevent stale UV sampling; `skipUnflushed` guards draw loops from sampling unuploaded glyphs
- **Idle-suppressing event loop** — `SdlEventLoop` uses `WaitEventTimeout` when idle, throttles mouse-motion redraws to ~30 fps

**Key files:**
- `VkRenderer.cs` — high-level draw API, extends `Renderer<VulkanContext>` from DIR.Lib
- `VulkanContext.cs` — Vulkan device/swapchain/sync lifecycle
- `VkFontAtlas.cs` — glyph rasterization cache + GPU texture management
- `VkSdfFontAtlas.cs` — SDF glyph atlas using R8_Unorm single-channel textures for resolution-independent text
- `VkPipelineSet.cs` — GLSL→SPIR-V compilation + pipeline creation (Flat, Textured, Ellipse, Page, Stroke, SDF, blend variants)
- `VkTexture.cs` — per-image Vulkan texture with blocking and deferred upload modes
- `SdlEventLoop.cs` — event-driven render loop with resize handling
- `VkMenuWidget.cs` — self-contained menu UI widget implementing `IWidget`
- `SdlInputMapping.cs` — SDL3 scancode/keymod → DIR.Lib `InputKey`/`InputModifier` mapping
