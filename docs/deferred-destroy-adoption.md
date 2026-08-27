# Deferred destruction: adopting it in a consumer

SdlVulkan.Renderer 7.28 adds `VulkanContext.DeferDestroy` (forwarded as `VkRenderer.DeferDestroy`).
This note is for a consumer that creates its own Vulkan objects on the renderer's device (images,
image views, buffers, descriptor sets) and needs to release them while frames are being produced.
If you only ever draw through `VkTexture` and the renderer's own draw calls, adoption is automatic:
`VkTexture.Dispose` already defers.

## The hazard it removes

A frame is `BeginFrame` -> your hooks and draws -> `EndFrame` (submit). Everything you bind into the
frame's command buffer stays referenced until that buffer has executed and its fence signals, which
is `MaxFramesInFlight` frames later. Two things follow, and only the first was ever covered:

1. A frame submitted EARLIER may still be executing. The drains (`TryWaitAllFramesIdle`,
   `TryWaitPriorFramesIdle`) wait for those, at the cost of a render-thread stall per call.
2. The frame being RECORDED may already have bound the object. No wait can retire that frame; it has
   not been submitted. If you destroy the object now, the frame reaches the GPU referencing freed
   memory. The validation layer reports it as
   `vkCmdBindDescriptorSets(): was called in VkCommandBuffer ... which is now in an invalid state ...
   VkImageView 0x... was destroyed`; without the layer it is a GPU fault, `nvlddmkm 153` on NVIDIA,
   then a `LiveKernelEvent 141` watchdog and your process is gone with no managed exception.

Whether case 2 applies depends on call ORDER across hooks: whether anything earlier in the same frame
(a pre-render-pass hook, another widget, a cached-layer pass) bound the object. That is not a property
of the destroy site, so reasoning at the destroy site cannot make it safe. The TianWen FITS viewer
had a destroy that was correct about fences and wrong about this, and crashed the Store build twice
in a day when the user stepped through files of different sizes.

## What to do

Replace every `vkDestroyImageView` / `vkDestroyImage` / `vkFreeMemory` / `vkDestroyBuffer` /
`FreeDescriptorSet` of an object a frame may have bound with one call:

```csharp
_ctx.DeferDestroy(view: _view, image: _image, memory: _memory);      // any subset, nulls ignored
_ctx.DeferDestroy(descriptorSet: set);                               // shared-pool sets
_ctx.DeferDestroy(() => api.vkDestroySampler(sampler));              // anything else
```

Then delete the drain you called before it. The context stamps the entry with the current frame
ordinal and destroys it right after the fence wait that proves that frame, and every frame in flight
with it, has retired. Recovery and teardown flush the queue after their own drains, so nothing leaks
across a device reset or `Dispose`. `PendingDeferredDestroys` tells you how many are waiting; a test
can assert it reaches zero after `MaxFramesInFlight + 1` frames.

Render thread only, like every frame call. Null handles are ignored, so pass whatever you hold.

## The other half: descriptor sets are per frame in flight

Deferring the destroy is not enough if you then `vkUpdateDescriptorSets` the set that pointed at the
old object. A set bound by a pending frame may not be written (without `UPDATE_AFTER_BIND`); the drain
used to make that write legal, and you have just removed the drain. So a set that changes while
frames are in flight needs one copy per frame in flight:

- allocate `VulkanContext.MaxFramesInFlight` sets instead of one;
- keep a change stamp on the resources (`_viewsStamp++` whenever a view is replaced) and a stamp per
  set slot;
- when recording a draw, take `slot = ctx.CurrentFrame`, and if `slotStamp[slot] != _viewsStamp`,
  rewrite that slot's set first (its previous frame has retired: `BeginFrame` waited that slot's
  fence before handing you the command buffer), then bind it.

This is what `VkFitsImagePipeline` in TianWen does for its channel-sampler sets. Sets whose contents
never change after creation (a fixed-size histogram texture) need no copies.

## Verify it

Run the app with `SDLVK_VALIDATION=1 SDLVK_SYNC_VALIDATION=1` (the Khronos layer must be installed:
the Vulkan SDK registers it), drive the path that replaces resources, and read the inspector's
`validation_report`: `active: true` with zero messages is the bar. `SdlVulkan.Renderer.Tests`
carries `DeferredDestroyTests`, which disposes a texture in the frame that drew it under the
validation layer and asserts silence; copy its shape for your own resource class if it is not a
`VkTexture`.

## What is deliberately NOT here

Uniform buffers written by the host each frame while earlier frames still read them are the same
class of hazard on the host side, and the validation layer cannot see host writes. A persistently
mapped UBO that changes per frame wants one region per frame in flight as well. That is a consumer
pattern rather than a renderer facility, and it is not part of 7.28.
