# SdlVulkan.Renderer

SDL3 + Vortice.Vulkan rendering library built on [DIR.Lib](https://github.com/SharpAstro/DIR.Lib) primitives.

## Types

- **`SdlVulkanWindow`** — SDL3 window with Vulkan instance and surface lifecycle. Creates maximized, resizable windows with Vulkan surface.
- **`VulkanContext`** — Vulkan device, swapchain, command buffers, sync management. `MaxFramesInFlight = 2` with per-frame vertex buffers.
- **`VkRenderer`** — `Renderer<VulkanContext>` implementation with FillRectangle, DrawRectangle, FillEllipse, DrawText. Exposes `FontAtlasDirty` for callers to trigger redraws after glyph rasterization.
- **`VkPipelineSet`** — GLSL 450 shader compilation and Vulkan pipeline creation (flat, textured, ellipse)
- **`VkFontAtlas`** — Dynamic glyph atlas with FreeType2 rasterization and Vulkan texture upload. Supports grow (512→2048), deferred eviction, and `skipUnflushed` to prevent sampling stale GPU texture data.

## Font Atlas Lifecycle

Per frame:
1. `BeginFrame()` — handles deferred eviction if atlas was full last frame
2. `Flush(cmd)` — uploads dirty staging region to GPU via `vkCmdCopyBufferToImage`
3. `DrawText(...)` → `GetGlyph(...)` — cache hit returns UV coords; miss rasterizes into staging
4. `GetGlyph(..., skipUnflushed: true)` — in draw loops, returns zero-width for glyphs not yet uploaded

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

## Dependencies

- [DIR.Lib](https://www.nuget.org/packages/DIR.Lib) — Rendering primitives + FreeType glyph rasterization
- [SDL3-CS](https://www.nuget.org/packages/SDL3-CS) — SDL3 bindings
- [Vortice.Vulkan](https://www.nuget.org/packages/Vortice.Vulkan) — Vulkan bindings
- [Vortice.ShaderCompiler](https://www.nuget.org/packages/Vortice.ShaderCompiler) — GLSL to SPIR-V

## License

MIT
