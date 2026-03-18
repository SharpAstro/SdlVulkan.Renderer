# SdlVulkan.Renderer

SDL3 + Vortice.Vulkan rendering library built on [DIR.Lib](https://github.com/SharpAstro/DIR.Lib) primitives.

## Types

- **`SdlVulkanWindow`** — SDL3 window with Vulkan instance and surface lifecycle
- **`VulkanContext`** — Vulkan device, swapchain, command buffers, and sync management
- **`VkRenderer`** — `Renderer<VulkanContext>` implementation with FillRectangle, DrawRectangle, FillEllipse, DrawText
- **`VkPipelineSet`** — GLSL 450 shader compilation and Vulkan pipeline creation (flat, textured, ellipse)
- **`VkFontAtlas`** — Dynamic glyph atlas with FreeType2 rasterization and Vulkan texture upload

## Usage

```csharp
using SdlVulkan.Renderer;

using var window = SdlVulkanWindow.Create("My App", 800, 600);
window.GetSizeInPixels(out var w, out var h);

var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)w, (uint)h);
var renderer = new VkRenderer(ctx, (uint)w, (uint)h);

// Use renderer.FillRectangle, DrawText, etc.
```

## Dependencies

- [DIR.Lib](https://www.nuget.org/packages/DIR.Lib) — Rendering primitives + FreeType glyph rasterization
- [SDL3-CS](https://www.nuget.org/packages/SDL3-CS) — SDL3 bindings
- [Vortice.Vulkan](https://www.nuget.org/packages/Vortice.Vulkan) — Vulkan bindings
- [Vortice.ShaderCompiler](https://www.nuget.org/packages/Vortice.ShaderCompiler) — GLSL to SPIR-V

## License

MIT
