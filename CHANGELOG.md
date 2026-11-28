# Changelog

Release notes for SdlVulkan.Renderer, one entry per `Major.Minor`, newest first.

The version NUMBER is not here: it lives in `src/Directory.Build.props` (`VersionMajorMinor`), and the
build job reads that property back rather than restating it, so a package can never declare a version
this file disagrees with. Bump it there and add the entry here, in the same commit.


## 7.122

**Memory counters, fewest swapchain images and Trim (#218).** `VkRenderer.MemoryStats` reports atlas pages and bytes, the Slug
store, the vertex ring and the swapchain image count; `VulkanContext.PreferFewestSwapchainImages` asks for the surface's
minimum image count instead of one more. `VkRenderer.Trim()` releases atlas pages, the Slug store, the vertex ring and the
swapchain between frames and the next frame begin rebuilds them, drawing the same pixels. `SdlEventLoop.TrimWhenHidden`
(off by default, desktop only) trims a view on WindowHidden / WindowMinimized, draws nothing for it, and Shown / Restored
rebuilds it on the next frame. See the README, "Memory".

## 7.121

**Incremental present.** Each present names the rectangle of the image that changed since the last present
(`VK_KHR_incremental_present`, enabled by default on a software rasteriser that offers it, `SDLVK_INCREMENTAL_PRESENT=1` asking for it on any other device; `VulkanDevice.IncrementalPresentAvailable`), so a
presentation engine can move that and keep the rest. A frame owed to a clock's tick names the clock's rectangle.

What it names is everything declared since the last present that was made, not the frame's own region: a swapchain image
holds the frame of two or three presents ago, and a frame that never reached a present leaves its damage to the next.
So a third record, of one slot, takes the same `AddFrameDamage` and `MarkFullFrameDamage` calls as the per-image ones,
and only a present takes it (`VulkanContext.PresentRegion.cs`). The present is whole, naming nothing, before a
swapchain's first present, after a frame declared full or declared nothing, on an HDR frame, and on a swapchain whose
transform is not the identity. `SDLVK_INCREMENTAL_PRESENT=0` refuses it everywhere. A present that fails with anything but success or suboptimal declares the next present whole. `SDLVK_FRAME_LOG`'s line for each frame says
what its present named (`present 2663,0 201x32`, or `present whole`), and `VulkanContext.LastPresentRegion` holds it.

Why: without rectangles, Mesa's X11 present for a software rasteriser with no MIT-SHM sends the whole image down the X
socket with `XPutImage` on every present, a clock's tick included (Mesa 25.2, `wsi_common_x11.c`). With MIT-SHM it hands
the X server a shared pixmap and the update region; a Wayland compositor is handed the rectangle as surface damage.
Measured on lavapipe in WSL (TianWen's idle Sky Map, 120 s each, MIT-SHM present): the app, Xwayland and WSLg's weston
together took 5.66 % of a core with it and 5.78 % without, which is no difference that can be told from noise there: that
X server and compositor already move only what changed. It is unmeasured on a native X server without a compositor (a
Raspberry Pi on X11), which is where it is expected to matter. Under the Khronos layer with synchronisation validation,
presents naming a rectangle and frames repainting a clock alone: no messages.

## 7.120

**The saved pipeline cache carries a checksum.** 7.117 checked a saved cache's header (vendor, device, cache UUID)
before handing it to the driver, which says that this device made the data but not that the data is what was written:
a body damaged behind an intact header (a disk error, a sync tool) went to the driver, which the spec requires to cope
and which is not trusted to. The file is now a magic number, a format version and a SHA-256 of the cache data, then the
data (`PipelineCacheFile`); on load the checksum is checked first, then the header, and either failing starts an empty
cache and is logged with which it was. A file written by 7.117 or 7.118 has no magic number, so the first launch on
7.120 rebuilds it once. Measured with one body byte flipped: refused, rebuilt (106 ms for the first set), then 8 ms
from the next launch. (7.121 is the incremental present, #216.)

## 7.118

**The pipeline cache is saved as pipeline sets are built, not only on dispose.** 7.117 wrote it when the device was
disposed, so a run that did not end that way (killed, crashed, ended from outside) kept nothing: the PDF viewer's
start-up timing, which ends every run with a kill, measured no cache at all. Each pipeline set now asks for a
background save once it is built (`VulkanDevice.NotePipelinesBuilt`, internal): on a pool thread, skipped when a save
is already under way, written only when the cache has grown. The caller never waits (the main set is built on the
render thread before a window's first frame). Dispose still saves last and waits out a running save before it destroys
the cache. Measured, launches that kill themselves without disposing: the first set 86 ms with 356 KB saved before the
kill, then 6 and 7 ms.

## 7.117

**A pipeline cache per device, saved where the app says.** The shaders ship as SPIR-V, but compiling SPIR-V to the
GPU's own code is the driver's work, and every pipeline was built without a `VkPipelineCache`, so it was done again for
every set: on the Adreno X1-85 the first set in a fresh process took 82 to 99 ms, and each one after it in the same
process still 71 to 88 ms. Now `VulkanDevice.PipelineCache` is made with the device and every pipeline (the sets, the
mesh pipeline, the HDR composite and image pipelines, the YCbCr compute pipeline) is built through it. Set
`VulkanDevice.PipelineCacheDirectory` before the first device and the cache is loaded from there and saved on dispose
(or on `SavePipelineCache()`), as `pipelines-<vendor>-<device>-<driver>-<uuid>.bin`, written to a temporary file and
renamed. Saved data is checked against the device's header (vendor, device, cache UUID) before the driver sees it, and
a file that does not match is not used. Measured, a launch as an app sees it: the first set 69 ms and 356 KB saved,
every launch after that 4 ms, and a set rebuilt in the same process 1 ms. Created without
`VK_PIPELINE_CACHE_CREATE_EXTERNALLY_SYNCHRONIZED_BIT`, so sets may still be built on pool threads at once. With no
directory the cache lives as long as the device. `VkMeshPipeline.Create` gains an overload taking the cache.

## 7.116

**`BetweenFramesCollector` learns its threshold**, and **DIR.Lib 11.103** (was 11.102).

The runtime's gen-0 budget is not one number: TianWen's runtime collected after about 6 MB, the PDF viewer's after 3.2
to 4.1 MB on the same machine and runtime, so 7.115's fixed 4 MB never fired there (0 of 13 collections between
frames; the runtime always got there first). Each collection the runtime starts itself now says how much it let the
process allocate first (shared out over every collection since the last look, since an idle app presents no frames),
and the threshold is three quarters of a running average of that, between `MinimumThresholdBytes` (1 MB, which is also
the least an interval must be to count, so an induced collection teaches nothing) and the constructor's threshold, now
a ceiling. `ThresholdBytes` reports the one in use. Measured in the PDF viewer, 25 s phases of pan and zoom alternating
off and on in one window: off, 19 gen-0 collections, all started by the runtime; on, 22, of which 20 between frames.

DIR.Lib 11.103 stops a trimmed run (a tab title) allocating every frame, keeps `PaintLayout`'s depth stacks between
paints and adds `IPixelWidget.CollectRegisteredTextInputs`.

## 7.115

**`BetweenFramesCollector`, moved here from TianWen, and `SdlEventLoop.BetweenFrames` to run it.** A generation-0
collection starts at the allocation that uses up the runtime's budget, and a GUI's render thread does most of its
allocating while it paints, so the pause lands inside a paint (TianWen: 511 of 1,489 paints over 8 ms in one traced
session). Set `loop.BetweenFrames = new BetweenFramesCollector()` and the loop asks it after every iteration that
presented a frame, right after `OnPostFrame`: once 4 MB (two thirds of the runtime's ~6 MB gen-0 budget) has been
allocated since the last collection it collects generation 0 there, between frames; when the runtime collected on its
own in between it starts its count again and does nothing. Null (the default) changes nothing. Additive only.

## 7.114

**DIR.Lib 11.102** (was 11.101), which adds `KeptLayout<TKey>`: a widget's arranged tree kept from one frame to the
next while everything its build reads stands, still painted every frame so its regions and hover stay live. A consumer
keeping its chrome with it (the PDF viewer's hint bar and tool palette, TianWen's panels) builds against this release.
Nothing in the renderer's own source changes. Additive only.

## 7.113

**Dependencies to their latest releases.** DIR.Lib 11.101 (was 11.99: 11.100 keeps a `TabBar` strip's tree between
frames and lets `FontFallbackResolver` measure a span without allocating, 11.101 takes Svg 3.42), SharpAstro.AppShell
1.5 (was 1.4: `AppIdentity.Claim` does nothing inside an app package, and `AppIdentity.IsPackaged` asks) and, for the
inspector, SharpAstro.Png 3.42 (was 3.41, the Codecs release that adds `JpegDecoder.DecodeGrayTo`). Nothing in the
renderer's own source changes. A consumer that pins AppShell itself moves with it. Additive only.

## 7.112

**BC1 textures: a texture at four bits a pixel, an eighth of RGBA.** `VkTexture.CreateDeferred` takes
`VkFormat.Bc1RgbUnormBlock`: BC1 blocks, 8 bytes for every 4×4 pixels, a partial block at an edge whose size is not
a multiple of 4. The staging buffer and the upload are the blocks, so a 3164×4096 page scan is 6.2 MB on the GPU and
in the copy where its RGBA is 49.4 MB. The device enables the `textureCompressionBC` feature wherever it is offered
(no feature was enabled before, and none other is now), and `VulkanDevice.SupportsBc1Textures` says whether the
format can be sampled with linear filtering; every desktop GPU and lavapipe can, many mobile GPUs cannot, and
`CreateDeferred` refuses BC1 on such a device rather than create an image it cannot use. Encoding is the caller's.
`VkTexture.ImageBytes` is a texture's pixels in its own format (four bytes a pixel for RGBA, half for BC1), for a
memory budget that would otherwise count every texture as RGBA.
`Bc1TextureRenderTests` draws a texture of solid-colour blocks and reads each block's colour back. Additive.

## 7.111

**Many textured quads off one texture in one draw.** `VkRenderer.DrawTexturedQuadRegions(textureSet, quads)` takes a
span of `TexturedQuad` (four corners and the UV rectangle they map, meaning what they mean for
`DrawTexturedQuadRegion`), writes every quad's vertices into the ring together, and issues one bind and one draw. A
page of many small images packed into one atlas texture paid a descriptor bind and a draw for each image. In the PDF
viewer, a sheet of 1,365 raster strips went from 1,365 draws to 4 and its redraw from 4.7 to 1.7 ms of GPU (Adreno
X1-85). Each quad paints after the one before it, as separate calls would, and the pixels are those of the quads drawn
one call at a time (`TexturedQuadRegionsRenderTests`). One quad goes through `DrawTexturedQuadRegion`; an empty span
draws nothing.

## 7.110

Render-thread work traced in TianWen's GUI (2026-11-25). Additive: three public members, the rest internal.

**The redraw check is asked once a refresh, not on every wake.** `SdlWindowView.CheckNeedsRedraw` is asked right before
each frame drawn of the window and otherwise at most about once a display refresh (`RedrawCheckPacing`). A burst of
input woke the loop thousands of times a second and asked the check on each: 3,700 times a second for 55 frames while
dragging a sky map. A host that used the check as a once-per-pass hook now gets it about once a refresh. **BEHAVIOUR
CHANGE** for such a host.

**An idle host can say when it next needs asking.** `SdlWindowView.IdleCheckInterval` (default zero: one refresh, as
before), set from inside the check, lets the loop leave an idle window unasked until then, an event, or
`SdlEventLoop.Wake()`, which any thread calls when something changed outside the loop; any number of wakes before the
loop gets to them are one. The loop bounds the interval at a second, and keeps the refresh's pace for a second after
input. Sixty asks a second of an idle window were more of an idle GUI's render thread than its frames.

**What a pending frame is owed to.** `SdlWindowView.RedrawPendingForCheckAndPointerMovesAlone` (and the same on
`SdlEventLoop`): the frame owed is owed to nothing but the host's own check answers and pointer moves a handler took,
so a host that keeps what its check asked a frame for may narrow that frame.

**The render thread asks for full speed on Windows** (`RenderThreadQos`): `SetThreadInformation` with
`ThreadPowerThrottling` execution-speed throttling off, so a hybrid processor does not run a background window's frames
on an efficiency core at half speed. A no-op elsewhere and where Windows refuses it.

**Text outside a partial repaint is not shaped** (`TextCulling`): a text that cannot reach the region a damage-limited
pass paints is dropped before shaping, on a generous bound (two ems a glyph, an em of padding).

**`SDLVK_FRAME_LOG` has an input line** (`InputTrace`): once a second while input arrives, what the loop dispatched by
source (mouse, touch-as-mouse, pen-as-mouse, finger, wheel, key) and how long the events had waited in SDL's queue.

- `VkRenderer`'s line boxes and `FillRectangles` add their vertex floats one by one instead of through a temporary array.

## 7.109

**An iOS app gets SDL3 and MoltenVK from the package (sebgod/chess#74).** `buildTransitive/SdlVulkan.Renderer.targets`
adds `SDL3.xcframework` (SDL 3.4.16) and the dynamic `MoltenVK.xcframework` (MoltenVK 1.4.2) as `NativeReference` items
to any `net10.0-ios` project that references the package. The first build fetches the pinned releases
(`buildTransitive/fetch-ios-frameworks.sh`, macOS only, as an iOS build is), checks their SHA-256 against GitHub's
published digests, and keeps them in `obj/`; a framework is renamed into place only once complete, so an interrupted
fetch is not taken for a finished one. `SdlVulkanIosFrameworksDir` moves the folder, `SdlVulkanBundleIosFrameworks=false`
opts out. The versions, digests and the reason SDL 3.4.16 is safe under the 3.5 preview bindings move here from
sebgod/chess, which carried them until now. Nothing changes for a project of any other target framework. The Debug
twin carries the same file as `SdlVulkan.Renderer.Debug.targets`, and CI now fails a pack that lacks either.

**The mobile hosts' log has a useful default (sebgod/chess#75).** `SdlVulkanIosApp` writes `SdlEventLoop.DiagnosticLog`
to `Documents/<LogFileName>` as well as stderr: the title in lower case plus `.log` by default ("Chess" writes
`chess.log`), replaced on each launch, its first line the bundle's version, the device model and the OS version (read
through CoreFoundation and `sysctl`, as the assembly has no UIKit). It starts before the bring-up, so a failure there
reaches the file. `SdlVulkanActivity.LogTag` (default "SdlVulkan", as before) is the logcat tag. An app that sets
`DiagnosticLog` before the host starts keeps its own, as before. **BEHAVIOUR CHANGE** for an iOS app that set nothing:
a file now appears in its Documents folder; override `LogFileName` to return null for stderr alone.

## 7.108

**Dependencies to their latest releases.** DIR.Lib 11.99 (was 11.84; the fifteen releases between are DIR.Lib's own,
see its changelog), SharpAstro.AppShell 1.4 (was 1.2: 1.3 adds `CrashTrace`, 1.4 `DesktopAccent` and
`DesktopAppearance`, all opt-in) and, for the inspector, SharpAstro.Png 3.41 (was 3.14). Nothing in the renderer's own
source changes. A consumer that pins AppShell itself (the PDF viewer does, to the same float as this file) moves with it.
Additive only.

## 7.107

**Y'CbCr planes to an HDR texture on the GPU.** `VkTexture.CreateDeferredFromYCbCr(ctx, YCbCrPlanes, reduce)` takes a
decoder's planes (luma, chroma, subsampling, `Kr`/`Kb`, range, and a code-to-linear table) and converts them in a compute
shader (`Shaders/ycbcrhdr.comp`): chroma repeated, the H.273 matrix in code units, rounding, the transfer by table, an
optional whole-factor reduction averaged in linear light, and the brightest channel, which `VkTexture.MeasuredPeak` reports
once the frame that ran it has retired. The result is the R16G16B16A16Sfloat texture `DrawHdrImage` takes. The renderer
knows no codec: SharpAstro.Heif 3.41's `HeifDecoder.DecodeYCbCr` hands over exactly this. The dispatch is recorded where a
deferred upload's copy is and requeued the same way after a dropped frame; `CreateFromYCbCr` is the one-shot form. The
pipeline, layouts and descriptor pool belong to the device (`VulkanDevice.YCbCrConverter`), made on first use; a graphics
queue without compute is refused with `NotSupportedException`. Tested against a double-precision CPU reference in every
H.265 layout, and under the validation layer with synchronization validation. BakeShaders and the stale-bake check take
`*.comp`. Additive only.

**`SlugGlyphStoreValidationTests`: the dropped-frame test no longer races its encoders.** It requested its glyphs in a clean
frame, and when the encoder threads finished before that frame drained, the clean frame grew the store and the dropped
frame had nothing to grow; it failed every full run once more test classes ran ahead of it. The glyphs are now requested
outside any frame.

## 7.106

**The event loop owns the caret blink (#200).** `SdlWindowView.Ui` (and `SdlEventLoop.Ui` for a single window) takes the
window's `WindowUiSettings`, set once by the host. Before each frame the loop sets `CaretPhase` from
`CaretBlink.PhaseAt` on its clock, so `OnRender` paints in it. While a text field has the keyboard (`Ui.Focus.Current`
is set) the loop asks for a frame at each 530 ms phase change (the wait is the time to that change but never longer than the loop's base 16 ms, so it wakes at that rate while a field is focused and draws only at a change); a window with no
focused field, or no `Ui`, asks for nothing, so an idle window draws no frame for the caret. `SdlEventLoop.TimeProvider`
(init, default `TimeProvider.System`) is the clock every caret decision reads; frame pacing and backoff stay on the
Stopwatch. The rule is the pure `CaretBlinkPacing`, tested on a fake clock. DIR.Lib 11.84 (`WindowUiSettings.CaretPhase`).
Additive only. Hosts (tianwen's per-tab `CaretPhase` and `PaintedCaretPhase` check, pilot#1192) can drop their own caret
glue once they set `Ui`.

## 7.105

**One rule for when a glyph atlas may touch the GPU, written down and checked under validation for all three (#193).**
The atlases kept breaking the same way: a draw-path call (a miss, reached from the draw methods between `BeginFrame`
and `EndFrame`) destroyed, recreated or rewrote a GPU object that the current or an in-flight frame still used. CLAUDE.md
now states the rule ("When an atlas may touch the GPU"): such a call only records the need; the change happens at the
next `BeginFrame`, in the frame's pre-pass upload with its barriers, or through `DeferDestroy`, never by a fence wait
and never to the frame being recorded; an atlas owns its descriptor sets and never writes the device's shared one; work
recorded in a frame stays provisional until the frame reaches the queue.

- **Bitmap atlas (`VkFontAtlas`, colour and emoji glyphs):** a grow now allocates a new image, view and descriptor set
  of its own and defers the old ones, where it used to rewrite the shared `ctx.DescriptorSet`. Covered by
  `BitmapAtlasGrowValidationTests`, including eviction and a dropped frame.
- **MTSDF atlas (`VkSdfFontAtlas`, base and large tier):** evict-all handed each page to a bounded wait and then
  destroyed it, which can skip or time out under a frame that still samples the page; each page's GPU half now goes
  through `DeferDestroy`. Page append and the per-page LRU recycle were already safe. `SdfAtlasValidationTests` floods
  append, recycle, evict-all and a dropped frame with zero validation errors asserted.
- **Slug glyph store (`VkSlugGlyphStore`):** audited and already obeys the rule (growth by copy into a new buffer and a
  new set inside `RecordUploads`, old objects through `DeferDestroy`, a dropped frame's growths and batches recorded
  again, staging recycled after the frame retires), so no change to the store. `SlugGlyphStoreValidationTests` grows
  it from 512 words under batch and persistent draws with earlier glyphs in the same frames, and, in Debug, drops a
  frame that recorded a growth, asserting zero validation errors and lit pixels after. Per operation, where the GPU
  change happens and whether a draw-path call reaches it:
  - *Miss:* `TryGet` only queues the encode on a worker and the caller skips the glyph; no GPU call. Draw path: yes, and
    that is all it does.
  - *Upload drain:* finished encodes are written to staging with one copy and a transfer-to-shader barrier in
    `RecordUploads`, before `BeginRenderPass`. Draw path: no.
  - *Growth:* a full buffer doubles by copy into a new buffer in `RecordUploads`; the old buffer and memory go through
    `DeferDestroy`. Draw path: no.
  - *New descriptor set:* made together with the grown buffer in `RecordUploads`, the old set through `DeferDestroy`;
    the store never writes the device's shared set. Draw path: no.
  - *Dropped-frame re-record:* `OnFrameDropped` treats the frame's drains and growths as provisional and records them
    again in the next frame (the buffers a growth copied from are held until then). Draw path: no.
  - *Staging recycle:* staging is reused only after the frame that recorded its copy has retired. Draw path: no.

## 7.104

**`ShutdownDrain` lives here (#140), and a wrong-thread dispose names itself.** `ShutdownDrain.PumpUntilComplete(loop,
task)` moves from tianwen: after `SdlEventLoop.Run` returns it pumps the loop while background work drains, with a
timeout, on `TimeProvider`. tianwen and pilot drop their copies in their own repos. `SdlVulkanWindow.Dispose` and
`VulkanContext.Dispose` called off the thread that owns them (the window's creator; the context's frame thread, now
recorded in Release too) throw `InvalidOperationException` in Debug and log one warning in Release, naming both threads
and pointing at `ShutdownDrain`. Offscreen and abandoned contexts are exempt.

## 7.103

**The bitmap glyph atlas no longer grows under a recording frame** (tianwen#621). `VkFontAtlas`, which holds the colour
and emoji glyphs, grew by destroying its image and view and rewriting the context's descriptor set, and it did that
on the spot when a glyph did not fit. A miss reaches the atlas from the draw calls, so with a colour glyph drawn earlier
in the same frame the grow rewrote a descriptor set the recording command buffer had bound, and destroyed a view a
recorded draw samples. The pool has no update-after-bind, so either one invalidates the command buffer: on this
machine's GPU the frame's submit failed with `VK_ERROR_INITIALIZATION_FAILED`. The grow now waits for the next
`BeginFrame`, before anything is recorded, the way eviction already did; the glyph that did not fit is skipped for
that one frame (it still moves the pen) and drawn from the grown atlas on the next. `IsDirty` reports a pending grow,
so an idle host still draws that frame. `BitmapAtlasGrowValidationTests` floods the atlas under a recording frame on a
validated context and asserts no validation error and a grown atlas; it failed on the old code.

## 7.102

**The event loop dispatches SDL's window mouse-leave (#173).** `SdlEventLoop` now turns `WindowMouseLeave` into DIR.Lib's
`InputEvent.PointerLeft` on the view's `OnPointerInput`, so an app is told the pointer left its window (a hover
clears, a tooltip drops). It arms a redraw only when the handler returns true. The legacy per-event callbacks get
nothing new, and the inspector's synthetic pointer never sends or flushes a leave.

A leave with a button held is withheld. The view tracks the buttons held on the real SDL path; a leave with none
held is sent at once, a leave with one held is sent after the release that clears the last button (after that
`MouseUp`), and a `WindowMouseEnter` first drops it. So a drag that runs off the window keeps its moves and release,
as DIR.Lib's `PointerLeft` remark requires.

Why the rule holds whatever SDL does (SDL release-3.4.0, read as source): `SDL_UpdateMouseCapture` captures the mouse
while a button is down (`SDL_mouse.c` 1443, hint `SDL_HINT_MOUSE_AUTO_CAPTURE`, default on), and the Windows
`WM_MOUSELEAVE` is ignored while captured (`SDL_windowsevents.c` 1512), so there the leave arrives only after the
release. X11's `LeaveNotify` (`SDL_x11events.c` 1495) skips `NotifyGrab`/`NotifyUngrab` but otherwise calls
`SDL_SetMouseFocus(NULL)` (`SDL_mouse.c` 571, sends the leave at 593) with no capture check, so a leave can arrive
mid-drag there, or anywhere auto capture is off.

## 7.100

**Built against DIR.Lib 11.81 (was 11.79).** No renderer code change; a lockstep release with Console.Lib and
WebGl.Renderer. Both crossed minors are additive:

- **11.81: F1 on a menu shows its keys.** `MenuModel.ShowingHelp` turns the card into a list of the keys that menu
  answers to (`HelpLines`: arrows, Enter, the row digits, Tab only when there is a Back or close control, then the
  host's own `HostHelp`). F1 or Esc closes it; every other key is swallowed while it is open. `MenuLayout` declares F1
  on the column and Esc on the title, so a host routing through `InputRouter` gets it with no change. A host that takes
  Esc or Backspace before the router should skip that while `ShowingHelp` is true, or Esc goes back instead of closing
  the list.
- 11.80: SVG icons and images with clips, masks, markers, images, patterns and text.

## 7.99

**Built against DIR.Lib 11.79 (was 11.73).** No renderer code change; a lockstep release so a consumer holding this and
Console.Lib or WebGl.Renderer restores one DIR.Lib by intent. Every crossed minor is additive (DIR.Lib's newest breaking
release is 11.25). What they bring that this package's users will see:

- **11.79: a menu panel's Back and close controls are reachable by keyboard.** Tab moves to them and Shift+Tab back;
  arrows stay on the rows; Enter on a focused control runs it. `PixelMenuWidget` passes the key's modifiers through, so a
  host routing keys through `InputRouter` (or calling `HandleInput`) needs no change. The focused control is lit in the
  selected row's colours and the row selection hides while it is.
- 11.74: the menu panel's Back and close controls themselves. 11.78: SVG icons (`IconBaker`, `VectorIcon`).
  11.75 and 11.77: prototypes. 11.76: the inspector's publish-only trim fix.

## 7.98

**A software rasteriser's cost is bounded, and a full repaint is drawn once (#181).** Two changes for a device that
rasterises on the processor (`VulkanDevice.IsSoftwareRasterizer`: llvmpipe in a VM, a board with no GPU driver). A GPU
takes neither: its frames stay paced by the display, on the swapchain path it had.

- **Frames are paced by what they cost** (`FrameCostPacing`). After a frame the window rests until the process has used no
  more than `SdlEventLoop.FrameProcessorShare` of the machine's processors since that frame began (half by default; zero
  for no limit), and never longer than `SdlEventLoop.MaxFrameHold` (100 ms) after it ended. The second bound keeps the
  limit from becoming a stall, since processor time is the whole process's.
- **The frame is kept in one image** (`VulkanContext.RetainsFrame`). Damage is per swapchain image, so one full frame left
  every other image owing a full repaint on its turn. The frame is now drawn into one image of the context's own and each
  swapchain image is brought up to date from it with a copy: a rectangle, or all of it the first time and after a full
  frame. For an opaque SDR swapchain whose images may be a copy's destination. `SDLVK_RETAINED_FRAME=1` asks for it on any
  device and `=0` refuses it.

Measured with TianWen's GUI on llvmpipe (WSL 2, Mesa 25.2, 4 processors, 1200x800), share of one core:

| | Before | After |
| --- | --- | --- |
| Panning the sky atlas | 271 % at 25 fps, longest gap 118 ms | 197 % at 22 fps, longest gap 52 ms |
| Full frames in 20 idle seconds, any tab | 5 | 2 |
| Hover over the atlas | 35 % | 17 % |
| The atlas idle (a full frame each 10 s) | 12.5 % | 4.6 % |

Run under the Khronos validation layer with synchronisation validation on llvmpipe, and on Mesa's dzn with the retained
frame forced: no message. dzn is what found that the copy must obey the queue's transfer granularity, as the atlases'
uploads do.


## 7.97

**A published app has no debug inspector unless it is published with `-p:DirInspectorSupport=true` (#182).**
`DebugInspector.Attach` follows DIR.Lib's inspector feature switch (`DebugInspectorCore.IsSupported`, DIR.Lib 11.73, now the
pin); there is no second property in this repo. With the switch off (the default for a trimmed or NativeAOT publish)
`Attach` returns the inert inspector, DIR.Lib prints its one stderr line when `DIR_INSPECTOR=1`, and nothing that roots the
verbs is reachable, so ILC drops them. Build, run and test are unchanged. The inspector client and the MCP sidecar now name
the publish property as a likely cause when they find no instances.

Measured with `tools/InspectorAotProbe` (win-x64 NativeAOT, no SDL or window, `IlcGenerateMapFile`), no IL2xxx/IL3xxx warning:

| Publish | exe bytes |
| --- | --- |
| no `Attach` call (baseline) | 1,166,336 |
| same renderer types (`SdlEventLoop`, `SdlWindowView`), no `Attach` call | 1,199,616 |
| `Attach`, default (switch off) | 1,202,176 |
| `Attach`, `-p:DirInspectorSupport=true` | 2,865,664 |

The first measurement of the switch-off row was 1,509,888, 343,552 bytes over the baseline. 33,280 of that is the renderer's own
types (the third row, `SdlWindowView` and what it pulls in), not the inspector. The other 310,272 was `Attach` itself: its
shared overload took the switch as a `bool` parameter, which ILC does not propagate, so the active half stayed rooted, and
with it the start-time `DateTimeOffset.UtcNow.ToString("o")` (DateTime and culture formatting, calendars, `TimeZoneInfo`,
`Enum` formatting: about 170 KB of method code and the metadata behind it, found by diffing the two map files). `Attach` now
branches on `DebugInspectorCore.IsSupported` itself, into a supported and an unsupported half, and the start time is formatted
on the active path only. The switch-off row is now 2,560 bytes over the third row.

The ilc rsp carries `DIR.Lib.Inspector.IsSupported=false` by default and `=true` with the property. The default map file
mentions `DebugInspector` 23 times against 589 with the property, and has no verb methods (no `Screenshot`); what remains is
the `Attach` wrapper, `DebugInspector`, `DebugInspectorOptions` and a closure type, plus DIR.Lib's `StartIfOptedIn` and
`WarnTrimmed`. Run with `DIR_INSPECTOR=1 DIR_INSPECTOR_TOKEN=x`, the default exe prints `[inspector] DIR_INSPECTOR=1 but the
inspector was trimmed out of this build: publish with -p:DirInspectorSupport=true` and reports `IsActive=False Port=0`; the
`=true` exe reports `IsActive=True` on a port.

## 7.96

**The debug inspector is in Release, behind a run-time opt-in (#179).** `DebugInspector` and `DebugInspectorOptions` are no
longer `#if DEBUG`, nor are what they need: the frame average behind `frameStats` (two doubles of arithmetic a frame; the
`frame.slow` log line stays DEBUG), `DebugPrimaryView`, and the swapchain readback behind `screenshot` (every hook returns at
once unless a capture was requested, so there is no readback or allocation with the inspector off). `GpuFaultInjection` and the
`gpuFault` verb stay DEBUG.

- `Attach` now starts the core through DIR.Lib's `DebugInspectorCore.StartIfOptedIn`: `DIR_INSPECTOR=1` plus `DIR_INSPECTOR_TOKEN`
  (every request must carry the token, else `unauthorized`). With the variable unset, or set with no token, it returns an inert
  inspector (`IsActive` false): no socket, no registry file and no `OnLoopIteration` hook, so frame pacing is untouched.
  Call it unconditionally, not under `#if DEBUG`. A Debug build stays always-on when `DIR_INSPECTOR` is absent.
- DIR.Lib pin: 11.70.* (the minor that carries SharpAstro/DIR.Lib#242), for both `DIR.Lib` and `DIR.Lib.Debug`.
- The `Inspector*Tests` classes are no longer `#if DEBUG`, so CI's Release run covers them, with new tests for the unset,
  no-token and opted-in cases.
- New package `SdlVulkan.Renderer.Inspector.Client`: a typed async `InspectorClient` (connect by pid and token, verbs for
  describe_ui, describe_layout, screenshot, click, drag, move, press_key, type_text, resize, restore, frame_stats,
  validation_report, render_liveness and signals) and `InspectorException` carrying the server's message. The registry
  reader, discovery and socket client moved into it from the MCP sidecar, which is now built on it; every request carries
  the token.
- Inspector traps fixed: a `screenshot` on a window minimized after the request now fails within the present budget saying the
  window is minimized, and the operation disarms its capture request on completion, timeout and dispose (it is `IDisposable`),
  so the next screenshot is not refused as already in progress. Instance 0 with several apps throws `InspectorException`
  listing app and pid and asking for one (`InspectorClient.Resolve`, shared by the client and the sidecar). The sidecar's
  tools pass the real error text through one call-tool filter instead of the SDK's "An error occurred invoking".
- The Debug twin package stays, for `GpuFaultInjection` and the diagnostics; it is no longer the only way to get the inspector.

## 7.95

**Glyph atlases start transparent.** A Vulkan image's contents are undefined until written, and both glyph atlases
(`VkFontAtlas`, and every `VkSdfFontAtlas` page) uploaded only the rectangles they had drawn, so each texel no glyph had
covered yet held whatever the driver handed over. A glyph is sampled with linear filtering, which blends its edge texels
with the ones just outside it. NVIDIA and Android drivers return zeroed memory, so it never showed; Metal (MoltenVK, on
the iPad) does not, and small glyphs (chess's captured-piece icons, a menu's chess-piece title) grew a one-pixel
lavender line along their bottom and right edges. The first upload into a fresh atlas image now clears it to
transparent black (`VulkanHelpers.ClearToTransparent`, with the transfer barrier that orders the clear before the upload), once per image: an atlas's creation and each new SDF page. A
grown bitmap atlas was already re-uploaded whole. Every later flush is unchanged.

## 7.94

**On iOS, a tap lands where it was made, and the app turns with the iPad.** The first iPad run that drew (chess, 7.93)
had two faults, both iOS-only.

- **Pointer coordinates are pixels everywhere.** SDL's mouse events are in window coordinates, which `SdlEventLoop`
  passed through as they were; everything a consumer hit-tests against is in pixels. The two only differ where the
  window has `HighPixelDensity`, which this library sets on iOS alone, where window coordinates are points, half the
  pixels on a Retina iPad: every tap landed at half its position (the app's log showed 17 taps arrive and hit nothing).
  Mouse down, up and motion (and so the wheel's position) are now multiplied by the window's `PixelDensity`, which is
  1 everywhere else, so nothing changes off iOS. Finger events were already pixels, and the inspector's synthesized
  input does not pass through the loop's SDL path.
- **The iOS window is `Resizable`.** Without it, or `SDL_HINT_ORIENTATIONS`, SDL allows only the orientations matching
  the window's initial shape, so the portrait default design size pinned the app to portrait and a landscape iPad
  drew it sideways. iOS now applies the orientations the app's Info.plist lists.

Also corrects 7.93's account of 7.92: it threw `DllNotFoundException: SDL3`, not an ahead-of-time compilation abort.

Built against DIR.Lib 11.61 (was 11.57; 11.58 to 11.61 are additive, and this library uses none of `FloatingPalette`,
whose `IsFading` changed meaning in 11.59), so chess, already on 11.59, no longer lifts the renderer past what its CI ran.

## 7.93

**`SdlVulkanIosApp` reaches SDL's callback the way that works on a device.** 7.92 never reached SDL on the iPad: it
loaded SDL with `NativeLibrary.Load("SDL3", typeof(SDL3.SDL).Assembly, null)`, which on iOS does NOT consult the
assembly's `DllImportResolver` (only a real `[DllImport]` does), so it threw `DllNotFoundException: SDL3`. (This entry
first blamed Mono's ahead-of-time compiler; the app's crash log, read later, showed the exception, corrected in 7.94.)
7.93 goes back to the mechanism 7.90 used, which
DID reach the callback on the iPad (an ordinary `[DllImport]` of `SDL_RunApp` taking a delegate, kept alive in a static
field, its target marked `[MonoPInvokeCallback]`), and changes only what 7.90 got wrong: the delegate is the renderer's
own `RawMainFunc(int argc, nint argv)`, not SDL3-CS's `MainFunc` with its unmarshallable `string[] argv`. The import is
this assembly's, so `AppleVulkan.ResolveBundledSdl` now registers its "SDL3" resolver on this assembly too (it has no
other `[DllImport]`). No API change.

## 7.92

**`SdlVulkanIosApp` actually starts on a device.** The first iPad run (chess, iPadOS 26.0.1) aborted 0.17 s after
launch with `MarshalDirectiveException: [MarshalAs] attribute required to marshal arrays to managed code` at
`SDL.RunApp`: SDL3-CS's `MainFunc` callback declares `string[] argv` with no size, and Mono, the iOS runtime, cannot
build a native-to-managed wrapper for an unsized array. The exception surfaced before a line of the bring-up ran, past
`SdlMain`'s own `try`. `Run()` now looks `SDL_RunApp` up on the library SDL3-CS resolves (through its resolver, so the
bundled `SDL3.framework`, or an app's own resolver) and calls it through a function pointer, back into an
`[UnmanagedCallersOnly]` `SdlMain(int argc, nint argv)`: no array, nothing to marshal. The local
`MonoPInvokeCallbackAttribute` is gone with the delegate it served. No API change. Follows 7.91 (#174).

## 7.91

**A frame owed to a pointer move stays one until it is drawn.** `RedrawPendingForPointerMoveAlone` (7.89) lets a host
repaint only what a move changed. The loop lost that mark whenever the host's `CheckNeedsRedraw` answered yes: it
re-armed the frame through the setter that clears it, and since a frame is usually held a few milliseconds for pacing, a
1000 Hz mouse had the check asked again before the frame was drawn. The second check found a frame owed to "more than a
move" by nothing but its own earlier yes, and the host drew the whole window. The check's yes now leaves the mark of a
frame that was already owed to a move alone; a frame the check alone asked for is owed to more than a move, as before.

**The inspector's `move` arms a frame the way a real pointer does**: for a pointer move when a handler took it, a plain
redraw otherwise. It used to ask for a plain redraw either way, so an injected hover could never produce the narrowed
frame a real one does and a host's partial repaint could not be measured unattended.


## 7.90

**Groundwork for iOS, and Vulkan through MoltenVK on every Apple platform.** `SdlVulkanIosApp` is the iOS host, the
counterpart of `SdlVulkanActivity` with the same members: a `net10.0-ios` head calls `Run()` from `Main` instead of
`UIApplication.Main`, SDL owns the UIKit application (`SDL_RunApp`), and the bring-up both mobile hosts now share
(`MobileHost`: a fullscreen window, a renderer built once the surface settles, the loop) runs inside it. It is in the
plain `net10.0` assembly, so the package gains no target and its CI no workload. iOS has no Vulkan loader, so the head
bundles `SDL3.xcframework` and `MoltenVK.xcframework` as dynamic frameworks, and the library loads SDL and Vulkan from
the bundle by absolute path, SDL and Vortice from the same MoltenVK. A fullscreen window on iOS asks for the screen's
pixel density. NOT yet run on a device: the first iOS head (chess) is what proves it.

On macOS and iOS the instance asks for `VK_KHR_portability_enumeration` (a loader in front of MoltenVK lists it only
then), and on any platform a device advertising `VK_KHR_portability_subset` gets it enabled, as the spec requires; no
Windows, Linux or Android driver advertises it, so nothing changes there. The device extensions for the on-screen and
offscreen devices are now one list (`VulkanDevice.DeviceExtensions`).

Tests: the hint and app-metadata tests share one collection (`SdlProcessStateCollection`). `SDL_Quit` erases SDL's
global properties, so the screensaver test's init and quit, running in parallel, made
`AppMetadataTests.ApplyingTheFallbackNamesTheEntryAssembly` read the version back as null about once in a few runs.

## 7.89

**An app can ask whether a frame is already owed.** `SdlWindowView.RedrawPending` (and `SdlEventLoop.RedrawPending` for the
primary window) is true when the loop has armed a frame for a cause of its own: an event a handler took, an expose, a
resize, a restore, an earlier request whose frame was paced or held back. Read from `CheckNeedsRedraw`, it lets an app
that narrows a frame's damage for one cause (a status bar's clock) repaint in full whenever anything else also asked,
which it could not tell before: the flag was internal.

**...and whether it is owed to pointer moves alone.** `RedrawPendingForPointerMoveAlone` is true when nothing but pointer
moves a handler took armed the frame. An app whose move handler knows exactly what the move changed (a chart's crosshair,
a readout) may then repaint that alone. Every other way of arming a frame clears it, so a new one is safe by default.


## 7.88

**Meshes can carry a specular highlight.** `VkRenderer.DrawMesh` gains an overload taking a highlight colour and a
Blinn-Phong exponent, added on top of the two-sided diffuse shade, so a dark part facing the light still catches it.
The highlight assumes a light that travels with the eye (a headlamp), where the half vector is the light direction and
n.h is the lambert term. The mesh pipeline's push constants grow from 96 to 112 bytes (`vec4 specular` after
`lightDir`); the existing overload pushes no highlight and draws exactly as before.

**A mesh can be drawn from a buffer the caller keeps.** `DrawMesh(VkBuffer, byteOffset, vertexCount, ...)` draws a range of a buffer made with `CreatePersistentVertexBuffer`, so a model drawn on every frame of an orbit is uploaded once rather than copied through the frame's vertex ring each time (a 630,000-triangle CAD model was 45 MB a frame). The span overloads are unchanged.


## 7.87

**The `DIR.Lib` pin moves from `11.48.*` to `11.57.*`.** No source change here: the package now asks for DIR.Lib 11.57 at
the least, so a consumer gets the releases since 11.48 with it, the last of them 11.57's table rule (a divider follows the
pointer: a fixed column and a capped star trade width) and 11.56's I-beam over selectable text. A consumer that pins
DIR.Lib itself below 11.57 gets a downgrade error (NU1605) until it moves its pin.


## 7.86

**WebView: a memory target and suspend for hidden views (#168).**

- `INativeWebView` gains `SetMemoryUsageTarget(bool low)`, `Task<bool> TrySuspendAsync()` and `Resume()`, for a
  host that keeps its window hidden for hours with a page loaded (a hidden YouTube player held about 720 MB).
- WebView2 (`Win32WebView`): `SetMemoryUsageTarget` is `CoreWebView2.MemoryUsageTargetLevel` through
  `ICoreWebView2_19`; the requested level is held before the browser is up and applied when it is, and a runtime
  without the interface or an error HRESULT goes to `Trace`, never a throw. `TrySuspendAsync` is `ICoreWebView2_3`'s
  `TrySuspend`: `false` at once when the view is visible or not attached, `false` when the browser refuses or
  errors (traced), `true` once suspended. `Resume` calls `Resume`; WebView2 also resumes on its own when the view
  is shown or script runs in it.
- WebKitGTK has no runtime equivalent (its memory-pressure settings are fixed at context creation): the target and
  `Resume` do nothing, `TrySuspendAsync` returns `false`. `CocoaWebView` is still not implemented and follows its
  other members.
- `tools/WebViewSmoke` gains `assert-suspend` (WebView2 only, SKIPs elsewhere): Low target set, a visible view's
  suspend is `false`, hidden it is `true`, then Resume, shown again, and a script still answers. The Windows CI job
  runs it with the other WebView2 self-tests.


## 7.85

**Packaging fix: the `SdlVulkan.Renderer` package depends on `DIR.Lib` again, not `DIR.Lib.Debug`.**

- 7.83 and 7.84 shipped `SdlVulkan.Renderer` (the Release package) with a dependency on `DIR.Lib.Debug`. The
  csproj picks `DIR.Lib` or `DIR.Lib.Debug` by `Configuration` (since 7.83, so a Debug build without the DIR.Lib
  sibling gets the inspector types), and that choice is made at restore: CI's `dotnet restore` named no
  configuration, so it was a Debug restore, and the `build -c Release --no-restore` after it packed from that
  assets file. The library itself was the Release build; only its dependency was wrong. A consumer that also
  references `DIR.Lib` got both packages, which carry the same assembly.
- CI now restores with `-p:Configuration=$BUILD_CONF` (build and test jobs).
- The csproj's new `RequireReleaseRestoreForPack` target fails a non-Debug, non-twin pack whose assets file
  holds `DIR.Lib.Debug`, with the restore command to run instead, so this cannot ship silently again (also
  for a local `dotnet build -c Release --no-restore` after a plain restore).
- `SdlVulkan.Renderer.Debug` is unchanged: it depends on `DIR.Lib.Debug`, as intended.


## 7.84

**HDR images: an opt-in scRGB swapchain, float textures and an HDR image draw, with no cost to the SDR path (#165).**

- **Opt-in, per context.** `VulkanContext.Create`, `CreateForSharedDevice` and `CreateOffscreen` take `hdr` (default
  false). `SdlVulkanWindow` and `SdlVulkanApp` now enable `VK_EXT_swapchain_colorspace` on the instance when the loader
  offers it. When the surface offers `R16G16B16A16_SFLOAT` in `EXTENDED_SRGB_LINEAR_EXT` (scRGB; HDR10 is not produced)
  the context presents it (`HdrActive`); otherwise it is exactly today's SDR context and nothing HDR exists.
  Transparent, layered and Dozen-device windows never take scRGB.
- **How it composes, and why SDR pays nothing.** On an HDR window the frame still renders into a swapchain-sized
  B8G8R8A8 SDR layer, so every pipeline, blend, MSAA resolve, cached layer, thumbnail and readback is bit-identical to
  the SDR path and the device's passes stay 8-bit (one shared device serves HDR and SDR windows). `EndFrame` records one
  composite pass on the float image: queued HDR images, then the layer (premultiplied, sRGB decoded, scaled by
  `SdrWhiteScRgb`). An SDR context allocates no layer, pass or composite pipeline (`HdrCompositeTests` asserts it). An HDR
  frame is repainted in full and must draw every HDR image every frame.
- **Float textures and `DrawHdrImage`.** `VkTexture.CreateFromRgbaFloat` / `CreateDeferredFromRgbaFloat` make an
  `R16G16B16A16_SFLOAT` texture from scene-linear floats (1.0 is 203 cd/m2, BT.2408). `VkRenderer.DrawHdrImage(texture, x,
  y, w, h, primaries, sourcePeakNits, referenceWhiteNits)` draws it: as scRGB through the composite on an HDR window
  (scaled by 203/80, primaries to BT.709, clamped to the display's peak), and tone-mapped on any 8-bit target (identity
  to 0.75 of white, then an extended Reinhard roll-off to white at the source peak, then the sRGB encode). Its pipeline is
  built on the first HDR draw, not before. `HdrColor` (the CPU maths, which `hdrimage.frag` transcribes and
  `HdrImageRenderTests` holds it to), `HdrPrimaries` (BT.709, BT.2020) and `HdrSurfaceFormat` are public.
- **Display facts.** `SdlVulkanWindow.HdrDisplay` / `HdrDisplayChanged` carry SDL's answer for the display the window is
  on (HDR on, SDR white, headroom). The event loop reads them when a window is added and on SDL's display-changed and
  HDR-state-changed events, never per frame, and hands them to `VulkanContext.ApplyHdrDisplay`, which keeps
  `SdrWhiteScRgb` / `HdrHeadroom` current and rebuilds the swapchain when a move to another display makes scRGB
  available or not. A context that did not opt in only records them.
- **Proof.** `tools/HdrProbe --show` gains a second phase: after the raw-Vulkan scRGB patches, a window drawn through the
  library, a float ramp from 0 to the panel's peak beside an SDR white swatch and the BT.2020 primaries at SDR white and at
  the peak beside the SDR primaries, through `DrawHdrImage` on an `hdr: true` context (a panel judges it; a screenshot
  clips). `tools/SdrFrameBench` is a headless SDR UI scene timed offscreen (CPU, GPU and wall ms, mean and p95; exit 2 with
  no Vulkan device) that builds against another source tree through the `RendererProject` property.
- **SDR before and after, one sitting, Adreno X1-85, 1280x800, MSAA x4, 300 frames a run, four runs of each alternating
  (base = the flight's merge-base 40b39c9 exported with `git archive`; the machine is shared, so runs are noisy):**

  | run | base CPU mean / p95 | head CPU mean / p95 | base GPU mean / p95 | head GPU mean / p95 |
  |---|---|---|---|---|
  | 1 | 0.531 / 0.710 | 0.625 / 1.355 | 0.658 / 0.673 | 0.664 / 0.663 |
  | 2 | 2.501 / 5.429 | 1.476 / 4.334 | 0.874 / 0.910 | 0.865 / 0.905 |
  | 3 | 0.944 / 2.720 | 0.574 / 0.999 | 0.966 / 1.122 | 0.940 / 1.111 |
  | 4 | 0.537 / 0.723 | 0.518 / 0.736 | 0.825 / 0.872 | 0.895 / 1.100 |

  GPU mean over the four runs: base 0.831 ms, head 0.841 ms; the machine's own drift between runs (0.66 to 0.97 ms on
  either side) is larger than that. CPU: the two quiet pairs (runs 1 and 4) read 0.531 / 0.537 base and 0.625 / 0.518 head,
  and the head's best (0.518) is under the base's (0.531). No SDR regression beyond noise.

## 7.83

**Slug text beside MTSDF: a glyph's outline drawn by its own coverage on the GPU, and a probe that A/Bs the two on one page (#162).**

- **What it is.** Eric Lengyel's Slug algorithm: each glyph is encoded once on the CPU (DIR.Lib's `SlugGlyphEncoder`,
  from DIR.Lib 11.48, which this release moves to from 11.40) into a blob of curves and bands, and the fragment shader
  computes the outline's coverage at every pixel from it. There is no atlas, no field to magnify and nothing that depends
  on the pass's sample count, so the edge is exact at any size and as smooth on `SingleSampleMainPass` as on a 4x
  multisampled pass. MTSDF stays; Slug is the choice where size varies widely, or the glyph set is large (CJK).
- **The store.** `VkSlugGlyphStore` (internal, one per renderer, created on first use): ONE growable device-local storage
  buffer of the encoded glyphs, keyed by glyph identity as `VkSdfFontAtlas` keys its cells. A miss never blocks: the
  glyph is queued for encoding on worker threads and skipped this frame (`FontAtlasDirty` asks for another). Blobs reach the
  buffer through the deferred upload path, in the frame's command buffer before the render pass, a bounded number of words a
  frame. A full buffer doubles by copy into a new buffer, with a new descriptor set, and the old one goes through
  `DeferDestroy`: no `vkDeviceWaitIdle`, no stall. It has its own descriptor-set and pipeline layout
  (`VulkanDevice.SlugDescriptorSetLayout` / `SlugPipelineLayout`), so the shared layout and the 84-byte push block are
  untouched.
- **Shaders and pipelines.** `slug.vert`, `slug.frag` and `sluginst.vert` (which shares `slug.frag`), baked to SPIR-V as
  the others are. `VkPipelineSet.SlugPipeline` and `SlugInstancedPipeline` exist on every pass shape: with and without the
  depth attachment, multisampled and the single-sample pass. `slug.frag` takes a pixel's footprint in em space as the
  length of `(dFdx, dFdy)` of each em coordinate, not `fwidth` as the Slug reference shader does: it is exact for any
  rotation (`fwidth` overstates a rotated pixel's span by up to the square root of 2 at 45 degrees), so rotated text
  differs a little from another Slug implementation's.
- **API.** Batch: `BeginSlugGlyphBatch(color, fontSize)`, `AddBatchedSlugGlyphAtBaseline` / `AddBatchedSlugGlyphAtBaselineByGid`
  (rotation, horizontal scale, ink-left), then `EndGlyphBatch`: one draw however many glyphs. Persistent (a page laid
  out once and kept): `WriteSlugGlyphInstance` writes one `SlugInstanceFloats` instance per glyph and
  `DrawPersistentSlugGlyphs(buffer, byteOffset, count, color, originX, originY, scale)` draws a page's glyphs of any size
  in ONE draw per colour, where `DrawPersistentSdfGlyphs` takes one per colour and atlas page. New public:
  `SlugDrawCalls` (was internal) and `SlugStoreStats` (glyphs, words used and capacity, growth count, failures,
  pending), so a consumer can see what the store is doing.
- **Tests.** The shader's coverage is held to a CPU port of itself (`SlugCoverageOracle`) on DejaVu and on three dense CJK
  glyphs (a committed Noto Sans JP subset, OFL), at 12 and 200 px, in both pass shapes, no pixel more than 8/255 off;
  the instanced path against the oracle through a rotation and a horizontal stretch and against the batch path; the
  store's encode, upload, growth and dropped-frame handling; and the probe's page (below), all under the validation layer
  when the host has it.
- **`tools/SlugProbe`**, run by hand (own csproj, not in the solution, not packed): draws one A4-like page, 794 x 1123 px
  of 15 px DejaVu Sans text with a heading and a CJK block (3183 glyph quads, 3 colours), as persistent MTSDF and as
  persistent Slug on a headless offscreen context, and prints draws and GPU ms per frame (timestamps), the worst
  number of curves a pixel's two rays walk on the densest CJK glyph (counted from the blob as the shader walks its bands
  and sorted lists), one dense CJK glyph alone at 300 px (first draw and after), and the longest frame while a burst of
  1200 new glyphs misses the store and makes it grow. Exit 0, or 2 with no Vulkan device. `SlugProbeSmokeTests` runs the
  same page code for a few frames on the offscreen fixture. #162's bars, measured on a Qualcomm Adreno X1-85 (Windows on
  Arm, Vulkan 1.4.295, 300 frames each, MSAA x4; the tiler's frame totals are reliable, its section times are not, so
  only totals are reported):

  | | MTSDF | Slug |
  |---|---|---|
  | draws a frame, whole page | 3 | 3 (one a colour) |
  | GPU ms a frame, median / p95 | 0.245 / 0.246 | 0.824 / 1.384 |
  | dense CJK glyph (U+9B31, 186 curves) alone at 300 px, GPU ms first draw / after | 0.053 / 0.032 | 0.133 / 0.111 |

  Worst per-pixel curve count on that glyph: 37 at 15 px and 38 at 300 px (the horizontal and vertical ray together;
  23 and 22 for the worse single ray; the longest band list is 41 of the glyph's 186 curves, so the bands cut a pixel's
  walk to a fifth of the glyph). A burst of 1200 new glyphs took 26 frames to land and grew the store four times (65 536
  to 1 048 576 words); the longest frame was 2.5 ms wall, 0.93 ms GPU, against a 0.92 ms steady median wall frame: a growth
  is a copy in the frame, not a stall. At MSAA x1 the page is 0.208 ms (MTSDF) and 0.649 ms (Slug) and the burst's
  longest frame 8.9 ms wall (one noisy frame; the same run's GPU longest is 1.7 ms). So on this GPU Slug costs about 3x
  MTSDF's fragment time at reading size; in return it needs no atlas page, so a page of any sizes and any glyph
  count is one draw a colour. The MTSDF page here is three draws only because its glyphs fit one atlas page.

## 7.82

**Inspector: screenshot un-minimises the window itself, and a resize verb captures layouts at real window sizes.**

- `screenshot` on a minimised window restores it, waits for the first presented frame and captures, reporting that
  it un-minimised; `reminimize: true` puts it back. With no present inside a short budget it fails at once instead
  of wedging.
- New `resize` verb (host command and MCP tool): `width`/`height` in window pixels, un-maximises if needed, clamps
  to the display work area and answers when a frame at that size has been laid out.
- `screenshot` takes optional `width`/`height`: it resizes, captures and puts the window back as it was.
- The inspector `selftest` parses `ping` with `GetRawText` (the reply is an object) and now takes a screenshot at a
  size, checking the size and that the window is as it was.
- pilot and tianwen should bump their `SdlVulkan.Renderer.Debug` pin to 7.82 after release.

## 7.81

**The OS is told who the app is: `SDL_SetAppMetadata` runs before `SDL_Init`.**

- Nothing set it, so every app built on this library was "SDL Application" to the system: the taskbar could not group
  its windows under its own name, Wayland and X11 windows carried no usable `app_id` / `WM_CLASS`, the macOS menu and
  a power-inhibit reason had no app to name.
- `SdlVulkanApp.Create`, `SdlVulkanWindow.Create` and `SdlVulkanWindow.CreateTransparent` take an optional
  `SdlAppMetadata? appMetadata` (name, version, reverse-DNS identifier such as `com.sharpastro.tianwen`, optional
  creator and url), applied in `InitSdlAndCreateInstance`, the one path they all share, before SDL starts. Existing
  calls compile unchanged.
- With nothing passed, the name and version fall back to the entry assembly's (informational version, without the
  `+revision` suffix, else its assembly version), so an ordinary app is not anonymous. No identifier is made up: a wrong one would
  group the app wrongly. Without an entry assembly (NativeAOT can have none) the fields stay unset rather than throw, so
  such apps should pass `appMetadata` explicitly.
- Consumers (pilot GUI, tianwen GUI, the FITS viewer) pass their identity on their next pin bump.
- `AppMetadataTests` checks the resolver (arguments, fallback) and reads the applied values back through
  `SDL_GetAppMetadataProperty` without `SDL_Init`.

## 7.80

**The display may sleep: SDL no longer holds it on by default.**

- SDL3 suppresses the screensaver and display sleep while one of its windows is open (`SDL_HINT_VIDEO_ALLOW_SCREENSAVER`
  is "0" unless set), and nothing here set it. That is wrong for a dashboard open all day (pilot's GUI) and undercuts
  tianwen, whose `WindowsPowerRequest` keeps the system awake but deliberately lets the display sleep.
- `SdlVulkanWindow.InitSdlAndCreateInstance` now sets the hint to "1" before `SDL_Init`. A creation option,
  `keepDisplayAwake` (default `false`, the last parameter, after `clickThroughOnFocus`), on `SdlVulkanWindow.Create`/`CreateTransparent` and `SdlVulkanApp.Create`,
  sets it to "0" instead. `SdlVulkanWindow.KeepDisplayAwake(bool)` wraps `SDL_DisableScreenSaver`/`SDL_EnableScreenSaver`
  at runtime (process-wide) for a live view or focusing screen.
- Measurement: **not measured: `powercfg /requests` needs elevation** (refused in this session, not worked round), so
  before and after are both unmeasured; the change sets the behaviour on purpose rather than inheriting SDL's.
  `ScreenSaverHintTests` asserts the hint reads "1" by default and "0" with `keepDisplayAwake`; the runtime flip
  is attempted on SDL's dummy and offscreen drivers and skips with SDL's reason, since neither supports
  `SDL_DisableScreenSaver` ("That operation is not supported"), so the flip is unverified headless.
- The pilot/tianwen pin bump is a follow-up shared with #154/#155.

## 7.79

**The click that activates an unfocused window now reaches the app.**

- SDL3 defaults `SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH` to `"0"`, so the mouse-down that focuses a window was swallowed
  and every action from another window took two clicks (SdlVulkan.Renderer #155). The hint is now set to `"1"`
  before `SDL_Init`, so one click focuses the window AND presses the button, as in native Windows and macOS apps.
  Nothing else about focus, hover or drag start changes.
- New option `clickThroughOnFocus` (default `true`, last parameter) on `SdlVulkanApp.Create`,
  `SdlVulkanWindow.Create` and `SdlVulkanWindow.CreateTransparent`. An app whose canvas does something costly or
  irreversible on a bare click passes `false`: the hint is then left alone, so SDL's default stands.

## 7.78

**Disposing the renderer waits for the frames in flight before destroying its pipelines.**

- `VkRenderer.Dispose` destroyed its pipelines and font atlases at once, and every host disposes the renderer BEFORE
  the context, whose `vkDeviceWaitIdle` was the only drain. A frame still on the GPU then held pipelines being
  destroyed: `VUID-vkDestroyPipeline-pipeline-00765`, "can't be called on VkPipeline ... that is currently in use by
  VkCommandBuffer", found under the validation layer at TransparencyProbe's exit on an Adreno X1-85. YtPlayer and
  the Android activity tear down in the same order.
- It now waits first with `TryWaitAllFramesIdle`: bounded, skipped on a GPU already known stuck, and on a timeout
  the objects are destroyed anyway, since a teardown has no rebuild behind it and ending beats hanging. A context
  disposed before its renderer has drained already, so nothing is waited for then.
- `RendererDisposeTests` disposes the renderer right after a submitted frame under the validation layer and asserts
  no error; it failed with the message above before the change.

## 7.77

**`SDLVK_VALIDATION=1` turns validation on in a Release build too.**

- The Khronos layer, the debug-utils messenger and (with `SDLVK_SYNC_VALIDATION=1`) synchronization validation
  needed a Debug build **and** the variable, so the Release/AOT build a consumer ships, the one whose GPU hangs, could
  only be validated by forcing the layer through the loader (`VK_INSTANCE_LAYERS`), with no messenger and no counts.
  The variable alone now does it, in any build. 6.19's entry said "DEBUG or SDLVK_VALIDATION=1"; the code had
  required both, and now requires only the variable.
- Unset, nothing changes: the variable is read once at start-up and checked only while the instance is created, so
  a Release build without it runs as before, with no layer and no messenger.
- Messages go to `SdlVulkanLog.Logger` (stderr unless the host sets its own). The inspector's `validation_report`
  stays Debug-only, as the inspector is.

## 7.76

**`SdlVulkan.Renderer.Debug`: a twin package built Debug, so a Debug consumer gets the inspector (#148).**

- `DebugInspector` and its options are `#if DEBUG`, and the published package is Release, so they were absent. A
  plane never finds the sibling sources, so a Debug build there restored the Release package and had no inspector.
- CI now also packs the same library from a Debug build as `SdlVulkan.Renderer.Debug` at the run's stamped version
  (opt-in `-p:DebugTwin=true`), depending on `DIR.Lib.Debug` (never `DIR.Lib`), and publishes it with the others.
  `tools/debug-twin-check` compiles a Debug consumer against it on every push.
- Consumers switch with a Configuration-conditioned `PackageReference` (see the README); restore in Visual Studio
  after switching configuration.
- DIR.Lib floats to 11.40, which carries the `DIR.Lib.Debug` twin.

## 7.75

**Depth is allocated per target only once a mesh is drawn on it (#146).**

- Every render pass used to carry a depth attachment so `BeginMeshRegion`/`DrawMesh` could draw inline, and so
  every target paid for a depth image whether or not it ever drew a mesh. Where the device has no lazily allocated
  memory (a desktop GPU, lavapipe) that is as much as the multisample colour image: **16 bytes a pixel under 4x
  MSAA**, measured, on a swapchain, an offscreen target, every cached-layer slot and the thumbnail target alike.
- The render-pass shape now has a depth-less variant (`VulkanContext.RenderPassNoDepth`,
  `SingleSampleRenderPassNoDepth`), with a `VkPipelineSet` to match (no `Mesh` pipeline: it tests depth). **Every
  target starts on it, with no depth image**: the swapchain framebuffers with the damage load pass and the
  single-sample pass, the offscreen target (7.75's first half), each cached-layer slot and the thumbnail capture.
  2D output is pixel-identical on either shape.
- A target takes depth when a mesh asks, **per target**:
  - up front, `VulkanContext.RequireMeshDepth(MeshDepthTargets)` / `VkRenderer.RequireMeshDepth(...)` with
    `Main`, `CachedLayer`, `Thumbnail` (flags, `All`), and `CreateOffscreen(meshDepth: true)`; the first frame then
    draws its meshes; or
  - lazily: `BeginMeshRegion` on a depth-less pass returns false, draws nothing, and marks that target. The next
    `BeginFrame`/`BeginOffscreenFrame` (the main target: drain, allocate the one shared depth image, rebuild the
    framebuffers and load passes), `BeginCachedLayerPass` (each slot on its own turn) or
    `BeginThumbnailCapturePass` gives it depth. `VkRenderer.TakeMeshDepthRedraw()` reports the refusal and
    `SdlEventLoop` redraws on it, so a window shows its meshes without waiting for input. A cached layer or
    thumbnail that was refused a mesh holds its picture without it until its consumer renders it again, so a
    consumer that draws meshes into one asks up front.
  - The shape then stays across resize and swapchain recreation.
- **Side-car pipelines must say which pass shape they target.** A pipeline is render-pass compatible with one
  shape only. Create it against `ctx.RenderPass` (depth) or `ctx.RenderPassNoDepth` (now public), or both, and bind
  the one matching `VkRenderer.CurrentPassHasDepth`. A side-car built only against `ctx.RenderPass` now draws on a
  target that has asked for depth and fails validation on one that has not.

## 7.74

**SDL's side mouse buttons arrive as `MouseButton.Back` and `MouseButton.Forward`.**

- `SdlWindowView.ToMouseButton` maps SDL's X1 (button 4) to `MouseButton.Back` and X2 (button 5) to
  `MouseButton.Forward`, on press and on release, through `OnPointerInput`. They used to fall through to `Left`,
  so a back/forward click acted as a left click. Left, middle and right are unchanged, and a button SDL numbers
  beyond those still maps to `Left`.
- The legacy `OnMouseDown` / `OnMouseUp` callbacks still receive SDL's raw button byte, so they see 4 and 5 as before.
- **Requires DIR.Lib 11.35**, the first release with `MouseButton.Back` and `Forward`; the pin moves from 11.17.* to 11.35.*.

## 7.73

**A transparent window blends on Windows with nothing installed: where no device gives it a premultiplied-alpha
swapchain, it is a layered window.**

- **Breaking, against 7.71:** `VulkanContext.CreateTransparent` and `CreateTransparentForSharedDevice` take the
  `SdlVulkanWindow` where they took its `VkInstance` and `VkSurfaceKHR`, since a layered window needs the window
  itself, not only its surface: `CreateTransparent(window, w, h)`, `CreateTransparentForSharedDevice(device, window,
  w, h)`.
- A transparent window's context now takes the first way the machine has for its frames to reach the screen with
  their alpha: a swapchain with `PRE_MULTIPLIED` composite alpha on the native device, one on a second device (Dozen,
  as 7.71 brought), a layered window, or else an opaque swapchain. `VulkanContext.AlphaPresentation` says which
  (`Swapchain`, `LayeredWindow`, `None`), and `PresentsAlpha` whether any.
- Without such a device, or kept off it (its context on `app.Device` rather than `DeviceFor`, or `nativeDeviceOnly`
  standalone), a transparent window on Windows is a layered one: the native device draws into an image of the
  context's own where a swapchain image would be, and `EndFrame` waits for the GPU, copies the frame out and hands it
  to the window with `UpdateLayeredWindow`, which blends it per pixel and passes clicks through wherever alpha is zero,
  shape or not. No acquire and no present; the rest of the frame (fence ledger, dropped frames, damage, recovery,
  resize, reported as a swapchain reports being out of date) is the swapchain path's. It costs a readback per frame,
  growing with the window: a 240 by 240 companion on the Adreno, measured soft-edged and click-through, took 0.3 ms
  from `BeginFrame` to `EndFrame`, against 0.09 ms for Dozen's swapchain. In exchange it needs nothing installed and
  shares the native device's resources. Tests drive the same frame path with a stand-in window on the suite's
  offscreen device, and once more under the validation layer where CI has it.
- Only where there are no layered windows either is the swapchain opaque, though NVIDIA (under Mailbox presentation)
  and Intel (under Fifo) were seen to blend its alpha anyway on an SDL transparent window, in a Remote Desktop session
  and outside anything Vulkan reports. The Adreno, at its console, blends it under neither.
- **Changed from 7.71:** a transparent context kept on the native device was an opaque swapchain, which is what a
  hard-edged companion cut out with `SetShape` used. It is now a layered window, readback and all. A companion with
  hard edges needs no readback and no second device: an ordinary context on the transparent window
  (`CreateForSharedDevice` on `app.Device`, or `VulkanContext.Create` standalone) is an opaque swapchain the shape
  cuts out (measured on the Adreno).
- `tools/TransparencyProbe` measures the cost of a frame too (`buddy`): `buddy app native` keeps the window off
  Dozen, a layered window on Windows, and `buddy app opaque` gives it an opaque swapchain.


## 7.72

**A web view that cannot take focus no longer takes the app down.** On Windows, `INativeWebView.Focus` threw when
WebView2's `MoveFocus` failed, and it is called from event handlers: a player calling it as its window was activated
died of an unhandled `Win32Exception` (0x800707DC, "The specified tag was not found"). The failure now goes to
`Trace` instead, and the next call tries again. The interface says so: focus is best effort.


## 7.71

**A window can be transparent: its pixels blend with what is behind it,** for a desktop companion or an overlay. A
per-window opt-in; ordinary windows are unchanged.

- `SdlVulkanWindow.CreateTransparent` and `SdlVulkanApp.CreateTransparentWindow` open a window with
  `SDL_WINDOW_TRANSPARENT`, not resizable, by default borderless and always on top. `VulkanContext.CreateTransparent`
  and `CreateTransparentForSharedDevice` build its context, whose swapchain asks for `PRE_MULTIPLIED` composite alpha
  wherever the surface offers it, and `PresentsAlpha` says whether it got it. Clear to alpha 0 and draw as usual: the
  pipelines' blending already leaves premultiplied pixels over a transparent clear, now pinned by a test (flat and
  overlapping translucent fills, antialiased edges, text).
- Windows' native Vulkan drivers offer no premultiplied alpha for any kind of window (measured: NVIDIA 582.66 and
  Qualcomm Adreno X1-85, 31.0.170.0, offer `OPAQUE` only, Intel `OPAQUE` and `INHERIT`). Mesa's Dozen, from
  Microsoft's "OpenCL, OpenGL, and Vulkan Compatibility Pack", does, and presents through DirectComposition: a screen
  capture over a known backdrop matched the blend to within a fraction of one 8-bit step, on NVIDIA, Intel, WARP and
  the Adreno alike. So a transparent window whose native device cannot give it alpha gets a second device, the one on
  the same GPU (matched by adapter LUID) whose surface offers it, and nothing else moves:
  `VulkanDevice.CreateForTransparentSurface` on the standalone path, `SdlVulkanApp.DeviceFor(window)` on the
  multi-window one, which makes that device once and shares it among transparent windows. Both log which device a
  transparent window got and why. Without such a device the window's swapchain is opaque, though NVIDIA (under Mailbox
  presentation) and Intel (under Fifo) were seen to blend its alpha anyway on an SDL transparent window, in a Remote
  Desktop session and outside anything Vulkan reports. The Adreno, at its console, blends it under neither.
- `SdlVulkanWindow.SetShape(RgbaImage?)` cuts the window to the non-zero alpha of an image (`SDL_SetWindowShape`, a
  one-bit window region on Windows): clicks outside it reach the window underneath, alpha still blends inside it, and
  on a window whose swapchain is opaque it gives a hard-edged cut-out. `IsTransparent` says whether a window can take one.
  A companion with hard edges needs nothing more, so it can stay on the native device with Dozen installed: its
  context on `app.Device` rather than `DeviceFor`, or `VulkanContext.Create` standalone (measured on the Adreno).
- The whole suite also passes on Dozen (the compatibility pack's and mesa-dist-win's on x64, the pack's on arm64), and
  natively on the Adreno.
- `tools/TransparencyProbe`, run by hand on Windows, measures what a machine actually does: what each device's surface
  offers (`caps`), what the screen shows for every device, present mode and window kind against a known backdrop
  (`present all`), and this API end to end (`buddy`; `buddy app native` keeps the window off Dozen). `--look` holds
  each picture until the person at the panel answers, because nothing promises a capture shows what the panel does
  (on the Surface, asked picture by picture, the two agreed).

## 7.70

**The inspector sidecar finds a local DEBUG app through DIR.Lib's per-user registry.** From DIR.Lib 11.21 a debug
inspector stops answering UDP multicast by default (that bind on every interface made Windows Firewall prompt once
per executable path, so once per worktree) and registers instead, as a `<pid>-<tcpPort>.json` file under
`LocalApplicationData/DIR.Lib/inspector`. A sidecar that only multicast could no longer see such an app.

- `SdlVulkan.Renderer.Inspector`'s discovery (`list_instances`, and the instance every other tool resolves) reads
  that folder first, listing each live entry at `127.0.0.1`, then multicasts as before, which still finds instances
  on other machines and builds older than 11.21 (which do not register). The two are merged keeping the first of each
  (address, pid), with a reply from one of this machine's own addresses counted as loopback, so an app that both
  registers and has LAN discovery on lists once, while another machine's instance that happens to share a local pid
  still shows.
- The reader follows DIR.Lib's `inspector-registry.md` by hand; the sidecar still references nothing from DIR.Lib.
  It computes the folder as the writer does, with `SpecialFolderOption.DoNotVerify` (on Unix the default option
  returns an empty path until `~/.local/share` exists), and a folder that is not there lists nothing. It reads only `<pid>-<port>.json` names, opens each sharing `ReadWrite | Delete` and closes it at once (so the app's
  own rewrite and removal are not blocked for longer than the read), skips a file that vanished or does not parse
  (including a `pid` that is not positive or a `tcpPort` outside 1 to 65535), and drops an entry whose pid is dead. An entry whose pid is running but whose process started more than 2 s away
  from its recorded `processStart` (a reused pid) is skipped and SAID: `descriptor skipped: start-time mismatch
  (<file>)` on stderr and as a `note:` line in `list_instances`, because on Linux a wall-clock step makes a live
  instance look the same until it restarts. It never deletes; the next app to start a core sweeps. The kind filter applies to registry entries
  as to replies (`sdl`, or `unknown`). The registry needs no network: a multicast that fails at the socket costs only
  its own results.

## 7.69

**A window can have its own entry in the system's media controls.** YtPlayer's video showed in Windows' media flyout
as "Unknown app": the entry was WebView2's, published from its own process, under its executable's name.

- `SdlVulkanWindow.TryGetMediaControls()` gives the window's entry in the system's media controls (Windows' media
  flyout, the lock screen, the keyboard's media keys): `SetTrack(title, artist, thumbnailUrl)`, `SetPlaying`,
  `SetSkipEnabled`, `Clear`. The presses arrive on the loop's thread as `SdlWindowView.OnMediaButton` (and
  `SdlEventLoop.OnMediaButton` for the primary window), turned into SDL user events from the thread the system calls
  on, as global hotkeys are; a host with its own loop reads them with `IsMediaButton`. Underneath are WinRT's
  SystemMediaTransportControls for the HWND, called by vtable slot, so the package takes no WinRT projection (which
  would be megabytes for a dozen calls), and the thumbnail goes over as a URL that Windows reads itself. Windows names
  the entry after the process's app id, which SharpAstro.AppShell 1.2's `AppIdentity.Claim` sets (with the Start menu
  shortcut that names it); this release takes AppShell 1.2. Null off Windows for now.
- `NativeWebViewOptions.SystemMediaControls = false` keeps WebView2 out of the media controls (and off the media
  keys), so an app that shows its own entry does not get two: Chromium's
  `--disable-features=HardwareMediaKeyHandling`, passed in the environment options. Checked against a muted video:
  without it Windows lists a session from `msedgewebview2.exe`; with it, none, and the video plays on.
- **Fix (a later 7.69 build): `OnResize` carries the size the swapchain was built at, not the window's.** The swapchain
  takes the surface's `currentExtent` whenever the surface states one (Win32, Android, X11 always do), and that need
  not be the pixel size SDL last reported for the window; yet `SdlEventLoop` handed `OnResize` the window's size on
  every path (resize, the resize after a failed `BeginFrame`, Android's surface rebuild, the GPU-error recovery), and
  `VkRenderer` projected to it as well. A host laid out to a size its frame did not have (tianwen #1574). Now
  `VkRenderer.Resize`, `RecreateForNewSurface`, `RecoverFromGpuError` and its constructor adopt the swapchain's
  extent as `Width`/`Height`, and the loop reports those. Same `Action<uint, uint>`; only the size it carries changed.

## 7.68

**A window sizes itself in design units, and resizes where it stands.** Two things YtPlayer did for itself, which any
window that sizes itself needs:

- `SdlVulkanWindow.ToWindowSize(designWidth, designHeight)` turns a size in DIR.Lib's design units into the window's
  coordinates, for `SetSize`, `SetMinimumSize` and `ResizeInPlace`. A design unit spans `DisplayScale` over the
  window's pixel density, so the window is as big on a 150% display as the layout inside it. YtPlayer multiplied by
  `DisplayScale`, which is right only where window coordinates are pixels (Windows, X11).
- `SdlVulkanWindow.ResizeInPlace(width, height)` resizes the window and keeps the corner nearest its display's own
  corner where it was, so a window parked bottom right grows up and to the left, and keeps it inside the display's
  usable area. `SetSize` keeps the top-left corner, which walks such a window off the bottom of the screen. The
  overload with a minimum size sets that too, in the order that does not move the window: SDL grows a window to a
  raised minimum at once, from its top-left corner, so the minimum is lowered, the window placed, then raised.

## 7.67

**`SetMaximizable(false)` stays set.** SDL rebuilds a resizable window's style, maximize box included, when it
restores the window from minimized, leaves fullscreen, or sets it bordered or resizable. So a window minimized
(Win+D, its taskbar button) and restored had its maximize box back. A double-click on its caption then maximized it,
and for a window that also reports caption double-clicks (7.65), did what the app does with one as well. YtPlayer's
player ended up maximized to the screen and folded to its strip at once.

The choice is now kept. `SetBordered` and `SetResizable` put it back themselves, and `SdlEventLoop` does on the
events that follow SDL's other rebuilds: restored, shown, left fullscreen. A host with its own SDL loop calls
`SetMaximizable` again after those. Checked with a window minimized and restored through posted `SC_MINIMIZE` and
`SC_RESTORE`: before, the box came back and a posted caption double-click maximized it; now the box stays off and the
double-click only reaches the app.

## 7.66

**A host hears when the page goes fullscreen.** A video's fullscreen button fills only the view, which in a small
window is not much. `INativeWebView.FullscreenChanged(bool)` says when the page enters fullscreen and when it leaves,
so a host can make its window fullscreen and the view's bounds the window's, and put both back.

On WebView2 it is raised on the window's thread, so the handler can resize the window and the view directly. In
YtPlayer, YouTube's F (which makes the document element fullscreen) now fills the screen and stays until F again. A
page that moves its fullscreen element when the view grows drops out of fullscreen, as any page would: asking for
fullscreen on YouTube's `#movie_player` directly did that, and is not what YouTube itself does.

Windows only for now: the WebKitGTK and WKWebView backends never raise it. `tools/WebViewSmoke assert-fullscreen`
puts a box fullscreen through the DevTools protocol (with a user gesture), resizes the view in the handler, and expects
the page to stay fullscreen until it is asked to leave, then the event again (WebView2; SKIPs elsewhere).

## 7.65

**A window can hide, off the taskbar too, and come back.** For an app that lives in the background between uses
and is brought up by a global hotkey (7.62):

- `SdlVulkanWindow.Hide()` takes the window off the screen, the taskbar and Alt+Tab (`Minimize` leaves it on the
  taskbar); `Show()` brings it back where it was, and `IsHidden` says which. A hidden window keeps its global
  hotkeys, and the event loop draws nothing for it, as for a minimized one, until it is shown. Android is as it
  was: the loop there still skips only a minimized window, so the resume fix's surface rebuild, which runs in the
  render pass, cannot be held off by a hidden flag SDL failed to clear.
- `SdlVulkanApp.CreateWindow(..., hidden: true)` creates it hidden, for an app that starts in the background.

**A double-click on a window's caption reaches the app.** The caption is the OS title bar, or on a borderless
window the area its SDL hit test calls draggable, which Windows treats as one. SDL gives the app neither click:
Windows takes them, to move the window. `SdlVulkanWindow.TryReportCaptionDoubleClicks()` asks for them, and each
arrives as `SdlWindowView.OnCaptionDoubleClick` (`SdlEventLoop.OnCaptionDoubleClick` for the primary window); a host
with its own SDL loop reads them with `window.IsCaptionDoubleClick(evt)`. Windows' own handling goes ahead too, so
a window that wants the double-click for itself (a player folding to its strip) calls `SetMaximizable(false)`.

Windows only for now: elsewhere it returns false. Like global hotkeys, it rides on SDL's Windows message hook, now
shared by both. `tools/WebViewSmoke assert-caption` double-clicks the smoke window's title bar for real with
`SendInput` and expects the event, with the window not maximized; `assert-hotkey` now hides the window before its
press and shows it on it.
## 7.64

**A file dialog starts in the folder it was asked to, not the one above.** `SdlFileDialogs` gives a start folder a
trailing separator, which is how SDL tells a folder from a file. Without one the Windows dialog took the last
component for a file name: asked to start in `…\Tests\Assets`, it opened `…\Tests` with "Assets" in the name box.
Found by the first real use, in the PDF viewer; checked there against the live dialog.

## 7.63

**The platform's file dialogs, without blocking the window.** `SdlFileDialogs` implements DIR.Lib 11.17's
`IFileDialogs` over SDL's dialogs (the common item dialog on Windows, the desktop portal or zenity on Linux,
NSOpenPanel on macOS): `new SdlFileDialogs(window)` and then `OpenAsync`, `SaveAsync` or `OpenFolderAsync` with a
title, filters, a start folder, a suggested save name and, for open, many files. Call it on the main thread (SDL
requires it). The call returns at once, and the task completes on the event loop's thread: SDL may answer on any
thread, so the answer comes back as an SDL user event, which `SdlEventLoop` delivers itself and a host with its own
loop passes to `SdlFileDialogs.TryDeliver(evt)`. A cancelled token completes the task as cancelled and drops the
dialog's late answer (SDL cannot close a dialog it has shown). A save path is given the filter's extension when the
user typed none, on every platform.

Nothing is marshalled: SDL keeps the filter array until it calls back, which SDL3-CS's array overload does not
guarantee, so the filters, their strings and the properties stay in native memory until then, and the callback is
an `UnmanagedCallersOnly` function pointer, as AOT needs. `SdlFileDialogsTests` covers the filter patterns and where
the dialog starts. `tools/WebViewSmoke assert-filedialog` shows the real thing through a host's own loop: a dialog
closed with an Escape pressed by `SendInput` answers with no paths on the loop's thread, and one whose token is
cancelled completes cancelled at once while its late answer is delivered and dropped (Windows; SKIPs elsewhere).

DIR.Lib 11.17.

## 7.62

**A key combination can reach the app from anywhere on the desktop.** For a small, always-on-top window (a
player, say) that should come forward at a keystroke, whichever app is active:

- `SdlVulkanWindow.TryRegisterGlobalHotKey(id, modifiers, key)` registers a combination: Ctrl, Shift, Alt and
  the Windows key (`Keymod.GUI`), with a character found on the keyboard layout active now (Shift added where
  the layout needs it: `/` is Shift+7 on a German one) or F1 to F24. It returns false when another app holds
  the combination or Windows reserves it (Win+S, Win+F), or for a character the layout has no key for.
  `UnregisterGlobalHotKey(id)` gives it back, and so does disposing the window.
- A press arrives as `SdlEventLoop.OnGlobalHotKey(id)`, on the loop's thread. The press is input to the app,
  so there `Raise()` brings the window to the front and takes the keyboard, which an app in the background is
  otherwise refused. A host with its own SDL loop reads presses with `SdlVulkanWindow.IsGlobalHotKey(evt, out id)`.

Windows only for now: elsewhere registration returns false. It takes SDL's Windows message hook, of which a
process has one, to turn `WM_HOTKEY` into an SDL user event. `tools/WebViewSmoke assert-hotkey` registers
Ctrl+Alt+Shift+F24, checks a second registration is refused, presses it for real with `SendInput`, and
expects it back in the loop (Windows; SKIPs elsewhere).

## 7.61

**Text can be laid out once and drawn every frame from a persistent buffer.** On DIR.Lib 11.12.
`AddBatchedSdfGlyphAtBaselineByGid` builds a glyph's quad, copies it into the per-frame vertex ring
and draws it, on every frame. A caller whose text does not change (a PDF page) paid that for every
glyph of every redraw. In a PDF viewer panning a CAD sheet, that was 98,425 glyphs and 8-12 ms of
CPU a frame. This release adds a persistent path beside the batch, which is unchanged.

- **`LayoutSdfGlyph`** lays one glyph out in the caller's own space (a page's points, say) as a 48-byte
  instance: the quad's corner and edge vectors, its cell in the atlas page, and its size. It reports
  `Quad`, `Blank`, `Pending` (queued, lay out again later) or `Refused` (the large tier is full), along
  with the atlas page it samples and that page's stamp.
- **`DrawPersistentSdfGlyphs`** draws a range of instances at an origin and a scale, through a new
  instanced pipeline (`sdfinst.vert`/`sdfinst.frag`). The quad comes from the vertex index, as for
  strokes. Each glyph derives its antialiasing band and its coverage path (two samples below 64 px a em,
  one from it) from its own size times the scale, where a batch pushes them per draw. So one draw holds
  glyphs of any mix of sizes, and laying a glyph out once serves every zoom. The draw keeps the atlas
  page hot in its LRU.
- **`SdfPageStamp`** says when a page's glyphs were recycled, so the caller lays out again, and
  **`UsesLargeSdfTier`** is the batch path's tier rule, for a caller choosing a tier per glyph.

`PersistentSdfGlyphTests` draws the same glyphs both ways and requires the same pixels. It covers 3 to
90 px a em, a layout scale other than 1, and a rotated, compressed glyph. Doubling the band an instance
carries fails every case.

Measured in that viewer, offscreen, Release, Adreno X1-85, minimum of 30 redraws of a resident sheet:
the text draw went from 12.1 to 0.63 ms, and the sheet's GPU time from 69.4 to 45.3 ms, because a draw
per run (320) replaced a batch per run and size (5,534).

## 7.60

**A frame's main pass can run single-sampled on a device that multisamples.** `VkRenderer.SingleSampleMainPass`
is read when the next main pass begins, so a consumer decides it per frame, as late as its `OnPreRenderPass`
hook, once it knows what the frame draws. It is for a frame that only blits a cached layer (antialiased
when it was drawn, in its own multisampled pass, which is untouched) and draws chrome over it. Such a frame
gained nothing from multisampling the whole window, and on a tiling GPU paid for it: in a PDF viewer on an
Adreno X1-85 at 2880x1814 an idle frame was 3.11 ms of GPU at 4x and is 2.24 ms single-sampled (three runs
alternated in one window).

- **What draws the same either way:** text, rounded boxes, ellipses, textured quads, axis-aligned
  rectangles, and now lines (below). In that viewer the whole window differed between the two in 67
  pixels, all of them the pan icon's triangle heads, which DIR.Lib 11.10 draws as pixel strips instead.
- **What does not:** geometry that takes its edges from MSAA (`DrawTriangles`, persistent fills and
  strokes, meshes) aliases in such a frame; that is why it is the consumer's call.
- **What it costs:** a single-sample render pass on the device (`VulkanDevice.SingleSampleRenderPass`),
  single-sample framebuffers over the images the multisampled pass already resolves into, a single-sample
  depth image, and a second pipeline set per renderer, about 50 ms of driver compile, built off the render
  thread (`SingleSampleMainPassReady`). `VulkanContext.LastFrameSingleSampled` says what a frame did.
- **What it gives back:** the damage path's partial repaint, which a multisampled pass cannot do (its
  transient image cannot be reloaded), now works for single-sampled frames.

**Lines are antialiased in the shader.** `DrawLine`, `DrawPolyline` and `DrawPolylineDashed` drew flat
triangles, smooth only where MSAA made them so. They now draw a box per segment through the rounded-box
pipeline with no rounding, whose coverage is a distance in the box's own frame, so a line comes out the same
at any sample count. A batched polyline still draws as one draw call, and still matches its segments drawn
one by one.

`SingleSampleMainPassTests` holds a frame of chrome to the same pixels single-sampled as at 4x (it fails
with the old lines), a layer drawn at 4x to the same blit either way, triangles to aliasing when
single-sampled, and frames that switch sample count, with a cached layer and a resize between them, to
silence under the validation layer.

## 7.59

**A frame that outgrows the vertex ring keeps its draws.** The per-frame ring grows on demand, but it
used to grow only at the NEXT frame start: the frame that ran out dropped every draw after the write
that did not fit, and was presented anyway. In a PDF viewer the first draw of a dense sheet, 98,425
glyphs into a 16 MB ring, came up as an empty page for one frame and then came back, a flicker on every
first visit. The slot a frame runs out in now moves to a bigger buffer there and then. The draws already
recorded keep the old buffer, which `DeferDestroy` frees once the frame has retired, and the other slot
takes the whole frame's demand at its own next start, where the swap is free. A write is dropped only
past `VertexRingMaxBytes` (512 MB), as before. Every draw binds `VertexBuffer` after its own write, so
none needed changing, except the bitmap glyph batch, which draws many writes as one range: it now draws
the part in the old buffer when the ring moves under it. `VulkanContext.VertexRingGrownMidFrame` counts
the moves. `VertexRingGrowthTests` holds a frame that grew, and a bitmap glyph batch that straddled the
move, to the pixels of a frame that fitted; without the batch's split it draws the wrong glyphs.

**Frames are timed whole.** `VkRenderer.LastPreFlushMs` and `LastPreRenderPassMs` are the CPU time the
last `BeginFrame` spent in the consumer's two hooks. A consumer that draws a cached layer does it in
`OnPreRenderPass`, so a frame timed around its draw callback left the layer out, and `frame.slow`
reported it as `begin`, which reads as the GPU. It now reports `hooks` separately. `SDLVK_FRAME_LOG=1`
logs one line for every frame drawn, in every build: begin, hooks, render and end on the CPU, and the
GPU time and sections of the latest frame to complete. `frame.slow` catches spikes only, so a steady
25 ms frame never showed there. Every cached-layer pass is now its own GPU section
(`VulkanContext.CachedLayerGpuSection`), recorded outside the pass, so it is a measurement on a tiling
GPU too. Opening it closes any section the caller still had open.

## 7.58

**A window can tell, and show, that it is taking input; and stay unmaximized.** Additions to
`SdlVulkanWindow`, for an app that draws its own chrome (a borderless, always-on-top player, say), which
until now reached past the renderer to SDL and Win32 for them:

- `IsActive`: the window is the active one, the one keys go to. On Windows it is the foreground window,
  so it holds while an embedded web view has the keyboard focus; elsewhere SDL's input focus. Read it
  from `OnLoopIteration`.
- `SetMaximizable(bool)`: on Windows, the maximize box, which also decides whether a double-click on a
  hit-test draggable area maximizes the window. A no-op elsewhere.
- `SetMinimumSize(w, h)` and `TryGetUsableDisplayBounds(...)`: SDL's minimum size, and the display's
  area less its taskbar, for a window that resizes itself and must stay on screen.

**And a web view can give its page input the page trusts.** `INativeWebView.CallDevToolsProtocolMethodAsync
(method, json)` calls a Chrome DevTools Protocol method: `Input.dispatchMouseEvent` clicks with
`event.isTrusted` true, which a page script's own `click()` never is, and which some sites (YouTube's ad
Skip button) insist on. WebView2 only; on WebKitGTK the task faults with `PlatformNotSupportedException`.
A class implementing `INativeWebView` outside this package must add the method.
`tools/WebViewSmoke assert-cdp` checks the click arrives trusted (Windows; SKIPs on WebKitGTK).

## 7.57

**A web view can patch the pages it loads, and keep its sign-in.** Two additions to
`SdlVulkan.Renderer.WebView`, for an app that hosts a site it does not control (a YouTube player that
hides Shorts, say):

- `INativeWebView.AddScriptAtDocumentStartAsync(js)` registers a script that runs at the start of every
  document from then on, top-level and frames, before the page's own scripts: WebView2's
  `AddScriptToExecuteOnDocumentCreated`, WebKitGTK's document-start user script. It may be called before
  `AttachToWindow`; scripts registered then reach the first navigation. The task completes once the
  browser has the script. WebView2 registers asynchronously, and a navigation sent before it confirms
  may miss the script, so on Windows a navigation requested after the call is held until it does: a
  caller need not await before navigating. A class implementing `INativeWebView` outside this package
  must add the method.
- `NativeWebView.Create(NativeWebViewOptions)` with `UserDataFolder`, where the browser keeps its
  profile. On Windows it is WebView2's user data folder (the default, beside the executable, already
  persisted). On Linux cookies go to `cookies.sqlite` there; WebKitGTK's default kept them only for the
  life of the process, so a sign-in did not survive a restart. `Create()` is unchanged.

`tools/WebViewSmoke assert-docstart` checks both on Linux in CI: a script registered before attach runs
ahead of the page and its CSS applies, with a profile folder set.

## 7.56

**`MeasureText` remembers what it measured.** A measurement runs the whole shaper (GSUB, GPOS, bidi
itemization) and resolves every glyph. UI chrome measures the same strings on every frame it paints:
a sidebar fitting its page labels, a tab strip fitting its titles, both re-measuring as they trim a
label to an ellipsis. On a 219-page document in a PDF viewer, that was two thirds of the CPU of a frame
that drew nothing new.

Results are now cached by font, size and text, looked up from the span without allocating. A
measurement is cached only if every glyph resolved: one that came back with no advance and no
height would freeze a short width. The cache is dropped when `TextShaper` is replaced, and cleared
once it holds 4,096 entries.

Measured offscreen, Release, Adreno X1-85: fitting twelve page labels the way that sidebar does,
every one a sheet title of about 50 characters that overflows, went
from 1.90-1.94 ms to 0.08-0.09 ms per frame, two runs alternated.

## 7.55

**The stroke shader draws round caps.** `DrawPersistentStrokes(..., roundCaps: true)` draws each segment
with a half-disc fan of `StrokeRoundCapTriangles` (8) triangles at each end, built in `stroke.vert` from the
vertex index alone: 54 vertices an instance instead of 6, and nothing stored. Two segments that meet both
cap the point, and the half-discs cover a round join, so a round-capped, round-joined polyline needs its
segments and nothing else; a zero-length segment is a disc. Without the flag a segment is its quad, as it
always was. A consumer tessellating its own cap and join wedges for such strokes can drop them: on one CAD
sheet of 1.35 million short round-capped lines they were 498 MB of a 518 MB upload. Additive;
`StrokeRoundCapRenderTests` covers it.

## 7.54

**A thumbnail capture completes on an offscreen context.** The capture's copy rides the frame fence, and
the swapchain `BeginFrame` snapshots it once that fence has been waited. `BeginOffscreenFrame` mirrors that
frame's contract (the wait, GPU timing, deferred destroys) but left the snapshot out, so offscreen a capture
was recorded and never came back: `ThumbnailCaptureBusy` stayed true for good and no later capture could
start. Found writing a headless test for a consumer's capture logic, which could not run at all. Fixed by
the one missing call; `OffscreenThumbnailCaptureTests` fails without it.

## 7.53

**CFF and Type 1 text keeps its hairlines, and all text draws at pdfium's weight**, on DIR.Lib 11.6.

DIR.Lib 11.6 takes SharpAstro.Fonts 1.13, which fixes the MTSDF of every CFF or Type 1 glyph with a counter
(a, e, o, d, ...). Its generator read contour windings as TrueType's, so each outer edge of those glyphs drew
about half a texel inside the outline; at reading size the top of a Times `a` drew with a gap in it whatever
the shader did. The `.sdfg` disk cache moves to format 7, so the thin fields are not served from disk.

That also corrects 7.52, whose 0.1 px edge shift was calibrated against an "11% lighter than pdfium" measured
on a Type 1 paper: most of that gap was the thin glyphs. With correct fields 0.1 px drew 3-13% heavier than
pdfium, and TrueType text, which never had the bug, 3-7% heavier. Both text shaders now shift the edge
0.025 px, which puts whole-page ink within 0.6% of pdfium's at 150-300 dpi on a Type 1 paper and on TrueType
documents (one page -1.8%). The two-sample design and its cost are unchanged.

## 7.52

**MTSDF text at pdfium's weight, for about half 7.51's cost.** 7.51 drew text at exact area coverage, which
kept thin strokes but turned out about 11% lighter than pdfium, and so than every viewer built on it: pdfium
draws small text heavier than its outlines (FreeType's LCD filter, the subpixels averaged back to grey, then a
text-gamma table).

- **The edge shifts 0.1 px outward** in both text shaders, a constant on the threshold that costs nothing. On an
  arXiv paper in Times the ink comes to within 2-3% of pdfium's at 150-300 dpi (it was 10.5% short), and the
  hairline top of an `a` at 29 px/em to about 0.8 px. pdfium's gamma table alone got a third of the way. Its
  rounding of glyph origins to whole pixels is not copied: in a viewer that scrolls by fractions of a pixel,
  text would step against the lines around it.
- **Two samples a pixel below 64 px/em, not four:** a quarter pixel either side of the centre on the diagonal.
  With the shift they keep thin strokes as steadily as four from 9 px/em up. A page of reading-size text takes
  1.21 ms of GPU a frame, against 1.46 with 7.51's four samples and 0.82 with 7.50's one (Adreno X1-85,
  1802×2332, medians of three rounds); large text costs what it always did. The thin-stroke test moves from 7
  to 9 px/em: at 7 a hyphen is mostly its two ends, and the diagonal pair, whose x and y are coupled, scores 0.83
  there (four samples 0.91, one 0.03). At 9 px/em one sample scores 0.38 and two 0.95.
- **The `sdfEdge` push constant is now one screen pixel in field units** (`VkSdfFontAtlas.FieldUnitsPerPixel`,
  replacing `SdfEdgeConstant`); each shader derives its band and shift from it, and the band is capped so it
  never reaches past where the field goes flat.

**A correction to 7.51:** its entry says the large-text pipeline is separate because a branch inside `sdf.frag`
did not save the four-sample cost. The page that was measured was 61 px/em, below the 64 px/em threshold, so
the branch was never taken; that claim is unproven. The separate pipeline stands on its own measurement.

## 7.51

**MTSDF text keeps strokes thinner than a pixel.** `sdf.frag` computed a pixel's coverage from ONE
sample of the distance field at the pixel centre, blended over half a pixel. That estimates coverage
well at an edge and badly across a stroke thinner than a pixel: two neighbouring centres can both fall
just outside it, and the stroke draws nearly white. Found as a Times `a` in an arXiv paper drawn at
reading size (about 29 px/em) without the hairline top of its bowl, with `n`, `e` and `o` thinned the
same way. The shader now averages four samples on a rotated grid inside the pixel, each blended over a
quarter pixel, so coverage is the pixel's area rather than its centre's distance. Measured on real
atlas cells against exact area coverage (four glyphs, four sizes, five sub-pixel offsets): mean error
0.032 to 0.012, worst pixel 0.63 to 0.28, and no stroke lost. The new test draws the same hyphens at
eight sub-pixel phases, at 7 px/em, where DejaVu's hyphen is about 0.6 px thick: the ink on screen
varied 0.16 to 5.82 px² with the phase alone, and now varies 3.07 to 3.32.

The samples follow the pixel's footprint in texture space (`dFdx`/`dFdy`), so rotated text samples its
own pixel, and each is clamped to half a texel inside the glyph's own cell. **An SDF vertex is now
32 bytes, not 16**: a new `sdf.vert` carries the cell (`u0, v0, u1, v1`) as a flat attribute. The clamp
is needed because the texels around a cell are not "outside the glyph": the atlas uploads only the
rectangle its new cells span and never clears a page, so they hold uninitialised memory on a new page
and old glyphs on a recycled one. A first version let samples reach 4.5 texels past the cell, and in
CI (lavapipe, which reuses freed memory as it is) a hyphen drawn low in its row picked up 0.8 to 1.4 px²
of an earlier test's ink in the row below. The one-sample shader had a small version of the same leak,
since bilinear filtering at a quad's edge read the gutter texel; that is closed too.

The `sdfEdge` push constant now carries each sample's half band, a quarter pixel, from
`VkSdfFontAtlas.SampleHalfBand`, clamped at 0.45 field units rather than the 0.25 of DIR.Lib's
`ScreenPxHalfBand`, which at 7 px/em cut the band to under half and turned each sample into an on/off
test. DIR.Lib is unchanged.

**Text of 64 px/em and up draws through a pipeline of its own with one sample**
(`SdfLargePipeline`, `sdflarge.frag`, chosen per batch by `VkSdfFontAtlas.IsSingleSample`). There a
Times hairline spans about two pixels, one sample estimates coverage as well as four, and four only
cost. It is a separate pipeline rather than a branch in `sdf.frag` because a branch didn't save the
cost: the four-sample path compiled into the same shader kept a page of large text at the four-sample
price. GPU time a frame on an Adreno X1-85, 1802×2332 target, medians of three rounds (the GPU's
clock moved between rounds, so read the ranges, not the digits):

| Page | Before | After |
|---|---|---|
| Body text at 29 px/em (212 dpi) | 0.82 ms | 1.44 ms |
| Reference list at 29 px/em (212 dpi) | 1.10 ms | 1.64-2.13 ms |
| Body text at 75 px/em (600 dpi) | 3.9-5.0 ms | 4.1 ms |
| Reference list at 81 px/em (800 dpi) | 9.1 ms | 6.9-9.0 ms |

So the four samples cost about 0.6-1 ms a frame on a page of reading-size text, and nothing once text
is large. An SDF vertex is twice the bytes it was.

## 7.50

**`SdlEventLoop.OnLoopIteration` is public, in every configuration.** Asked for by TianWen, whose window must tell
its node, every few seconds, that it is still alive and can show a prompt: a frozen window, whose render thread is
stuck, must stop counting as someone who will answer, or it holds the night on a prompt nobody sees. The hook fires
once every loop iteration whether or not anything was drawn, about every 16 ms while the loop idles and never while
it is stuck, so it is that proof of life. It was DEBUG-only and internal (the inspector's pump), and
`OnPostFrame`, the only public per-iteration callback, fires only after a rendered frame, so an idle, healthy window
would have fallen silent. Additive: the cost in Release is one null check per iteration; the debug inspector still
chains onto it as before. `OnPostFrame`'s summary no longer claims to run on every iteration.

## 7.49

**A host can keep running after its GPU is declared wedged, with its window still closable.** Asked for
by TianWen, whose night had to END when the Adreno's rejected submits tripped 7.48's dead-device rule:
the window is the rig's process there, and the only answer the loop gave was to stop. Additive; no
existing behaviour changes for a host that exits on the wedge, as every host so far does.

- **`SdlWindowView.IsGpuWedged`**, true from the moment `OnGpuWedged` fires and never cleared. The loop
  then treats that window as INERT: it never renders it, never resizes its swapchain and never polls a
  recovery for it, while its events are still dispatched and its `CheckNeedsRedraw` is still asked every
  iteration (a redraw it asks for is never drawn; a shutdown drain uses that check to stop the loop). So
  `Run` may be called again after it stopped for a wedge, and the window stays movable and closable
  (`SdlVulkanWindow.SetTitle` needs no GPU, so a host can say why nothing is drawn). Before, a second
  `Run` rendered the dead window on any armed redraw and polled the abandoned recovery task, which was
  never cleared, so it declared the wedge again and stopped at once.
- **Every path that gives up on the device now tells the host.** A recovery task that FAULTED, and a
  recovery that threw while being started, used to stop the loop without firing `OnGpuWedged`, which a
  host could only read as the user quitting. All six terminal paths now go through one hand-off.
- **The single-window loop forwards `OnGpuWedged` and `IsGpuWedged`**, as it already forwarded
  `OnRenderDegraded`. They were reachable only on a view from `AddWindow`, so a single-window host (the
  TianWen GUI) could not subscribe at all.
- **DEBUG:** the inspector's screenshot answers at once, with an error, on a wedged window instead of
  waiting 15 s for a frame that will never come; the `gpu_fault` tool describes the re-run.

Unchanged, and deliberately so: a NON-Vulkan exception from `OnRender` is an app bug and is still
rethrown out of `Run` (after the frame is resolved), for the host to handle.

## 7.48

**An upload recorded into a frame that never reaches the queue is recorded again, and a GPU wedge can
be faked on demand to prove it.** The first came out of the second: with the Adreno's rejected submits
faked in a live TianWen GUI, a notification written during the storm had lost every letter first drawn
then ("Displa recovered from a stall on the Home vie"), for the life of the process.

- **Fix: work recorded into a dropped frame is no longer lost.** The glyph atlases' flushes, deferred
  texture uploads and a cached-layer pass all advanced their bookkeeping when RECORDED (dirty rect
  reset, texture marked uploaded, slot marked rendered), so a frame the driver refused, one whose
  submit failed, one recovery discarded, or one begun and never ended carried the work away unexecuted
  and nothing uploaded it again. `VulkanContext.OnFrameDropped(cmd, rollback)` registers what undoing
  recorded work means; the context runs it exactly when the frame does not reach the queue, and drops it
  when the submit is accepted. The atlases put the region back in line (only that region: a whole-atlas
  re-upload is the kind of burst the Adreno is suspected of choking on) and restore a first flush's
  initial transition; a `VkTexture` whose upload was dropped is recorded into the next frame on its own,
  so no consumer needs to know; a cached-layer slot that was never rendered goes back to unrendered. A
  consumer that tracks what a slot HOLDS registers its own rollback. The thumbnail and screenshot captures
  are now cancelled on every kind of drop, not only a rejected submit. Event 212 logs each re-queue.
- **Fix: a one-shot never waits without a bound, and never on a GPU known stuck.**
  `VulkanDevice.ExecuteOneShot` ended in an unbounded `vkQueueWaitIdle`, reached on the render thread
  for every frame of a consumer's live image upload, so a hung GPU froze the window before the loop's
  bounded fence wait noticed anything. It now waits on a fence for at most 5 s (past the Windows GPU
  timeout); a timeout leaks the pending command buffer, marks the device stuck and throws
  `VkException(Timeout)`, and while the device is known stuck it throws at once without submitting. A
  timeout thrown from inside a frame now resolves that frame (`AbortFrame`) before the loop retries.
  `CreateFromBgra` frees its texture when the submit did not take. A caller must treat what a
  timed-out one-shot read as still in use.
- **`VulkanContext.QueueTextureUpload(texture)`**: records a `CreateDeferred` texture's upload at the
  start of the next frame, before any render pass, for a texture made where a one-shot would block the
  render thread or submit in the middle of a frame. Draw it once `IsUploaded` is true.
- **Fix: a device that keeps refusing work is declared dead.** With every submit rejected, the
  mid-frame recovery rebuilt sync and swapchain about 1.8 times a second forever over a frozen window.
  Eight recoveries on `VK_ERROR_INITIALIZATION_FAILED` with no clean frame, over at least 5 s, now hand
  off to `OnGpuWedged` and stop the loop (event 117), as a device loss does. Only a refusal counts: an
  error the app causes every frame is not a dead device.
- A texture's staging buffer is freed through the deferred-destroy schedule, and `CleanupStaging` is a
  no-op until the upload is recorded; a frame begun and never ended is noticed before the next begin
  reads its captures.
- **Fix: the swapchain acquire is bounded.** It waited `UINT64_MAX`, the one wait left in a frame
  begin with no bound; it now waits 500 ms (10 ms while the previous acquire timed out) and throws
  `VkException(Timeout)` into the loop's existing stall handling. It runs before the frame ordinal
  moves, so an acquire that fails leaves no ordinal pointing at a frame that never began.
- **Fix: the mid-frame recovery runs on the sacrificial task.** It ran synchronously on the render
  thread, where a teardown call that blocks on a hung driver (`vkFreeMemory` has been seen not to
  return) froze the window for good. It now runs as the fence-stall recovery does, on a task the loop
  only polls, with the same 4 s deadline.
- **Fix: a cached layer left open by a frame that threw does not outlive it.** The next frame drew at
  the layer's scale into the layer's target, and `AbortFrame` left the layer's render pass open. Every
  begin forgets an open layer, and an abort closes its pass.
- **Fix: a swapchain image whose frame was dropped repaints in full.** Taking an image's damage clears
  it, so a dropped frame lost the region it owed with nothing painted; the image now clears and
  repaints in full on its next turn (`SwapchainDamage.MarkImageFull`).
- On DIR.Lib 11.2 (`SdfFontAtlas.RequeueUpload`).

**A GPU wedge can be faked, on demand, on a healthy GPU (DEBUG).** Every path that answers a wedge
(the rejected-submit streak, the event loop's mid-frame recovery, the load-shed request, the
device-loss hand-off) could only be exercised by a driver failing on its own, about twice a month on
the Adreno X1-85. `GpuFaultInjection`, on `VulkanDevice.FaultInjection` (and forwarded from
`VulkanContext`), answers the device's submits with the fault INSTEAD of submitting, which is what a
rejected submit does on the real driver: `RejectSubmits(count)` returns
`VK_ERROR_INITIALIZATION_FAILED` (the Adreno's answer after an engine reset; no count = until
cleared), `LoseDevice()` returns `VK_ERROR_DEVICE_LOST` from every submit, `Clear()` disarms. Nothing
reaches the GPU while it is armed. Compiled out of Release.

- **One submit seam.** The frame submit, the offscreen submit and `ExecuteOneShot` all go through
  `VulkanDevice.QueueSubmit`, so the fault reaches all three, and the ledger says when a wedge is
  injected. Arming and clearing log at Warning (events 210, 211); the lines the renderer writes in
  reply are the real ones.
- **Inspector.** `gpu_fault` (`gpuFault` on the wire) arms, clears or reads the fault in a live app:
  `reject` with an optional count, `lost`, `clear`, `status`. `lost` ends the event loop, as a real
  loss does, so the app usually exits.
- **Fix: a rejected `ExecuteOneShot` no longer leaks its command buffer.** It threw straight out of the
  submit, one command buffer per immediate texture upload tried while the device refused work. Found
  by faking the refusal.


## 7.47

**Every frame's GPU time is measured, and a window renders at most once per display refresh.** Both
came out of the same wedge: every GPU wedge on record (2026-08-19, 2026-09-22, an Adreno X1-85) was
the OS resetting the process's GPU context after one submission ran past the Windows GPU timeout,
2 s by default (LiveKernelEvent 141), and nothing logged could say what ran long.

- **GPU frame timing.** Each frame is bracketed with timestamp queries outside the render pass and
  read back once its fence has been waited, so the readback never stalls. `LastGpuFrameMs`,
  `PeakGpuFrameMs`, `SlowGpuFrames`, and `LastGpuSections` from `BeginGpuSection`/`EndGpuSection`;
  a frame over `SlowGpuFrameBudgetMs` (250 ms) is logged as event 208 with its sections. Sections
  are accurate on an immediate-mode GPU and only a hint on a tiler, which may bin the whole pass.
- **Frame pacing.** The loop drew a frame per redraw request, and the swapchain prefers Mailbox,
  which never waits for vblank, so a surface asking for a redraw on every pointer move drew at the
  mouse's report rate. Each clean frame now sets the window's next due time one display refresh
  (read from SDL, re-read once a second) after the frame started; requests before then fold into
  that frame, and the loop waits for exactly the time until it is due rather than spinning.
- **Inspector.** `frame_stats` reports `framesBegun` (sample twice for a frame rate) and the GPU
  timing; `move` takes `mods`, carried on every motion event.
- **A pointer move carries the keyboard's modifiers** (DIR.Lib 11.0's `MouseMove.Modifiers`), read
  as a press already read them.
- On DIR.Lib 11.0.


## 7.46

**An ellipse can be drawn at any affine placement, anti-aliased, with a pixel-width stroke, and a
page of them in one call.** The primitive could only be drawn upright, one at a time, with a hard
edge; all three limits are gone, and the shape itself now lives on the abstraction.

- **Rotated and sheared ellipses are DIR.Lib 10.4's shape, overridden here.** `FillEllipse` and
  `DrawEllipse` take the four corners of a parallelogram, the images of local `(-1,-1)`,
  `(+1,-1)`, `(+1,+1)`, `(-1,+1)`, or a centre plus two semi-axis vectors. Declaring them on
  `Renderer<TSurface>` rather than on `VkRenderer` is what makes them reachable from a CPU surface
  and a browser too, for the reason `DrawTriangles` already records in its own documentation; this
  repo supplies the one-draw override of the two virtuals and inherits the convenience forms.
- **The shader is the pixel-distance rule the base declares, evaluated with the GPU's gradient.**
  `ellipse.frag` and `ellipseinst.frag` take `r = |local|`, divide `r - 1` by the length of
  `(dFdx(r), dFdy(r))` to get a signed distance in PIXELS for any rotation, scale or shear, and
  cover a fill as `clamp(0.5 - d, 0, 1)` and a stroke of width `w` as `clamp(0.5 + w/2 - |d|, 0, 1)`.
  So the edge is anti-aliased, and a stroke is the same pixel width at every point of every
  ellipse. The quad is padded by `w/2 + 1` px along each axis so the rim has somewhere to land,
  and the single-discard shape is kept for the llvmpipe reason `ellipse.frag` records.
- **The rect ring converts nothing any more.** `DrawEllipseOutline(rect, ..)` used to derive a
  local hole fraction from the LONGER semi-axis while WebGL derived its own from the SHORTER, so
  the same call drew two different rings; both now expand the rect through DIR.Lib's one
  `Renderer.EllipseCorners` and hand the stroke to the shader as the pixel width it is.
- **`EllipseInstancedPipeline` is the bulk form.** One draw per ellipse is right for chrome, a few
  dots and swatches a frame, and wrong for a drawing full of circles, a chart or a marker overlay.
  One instance carries its centre, both semi-axis vectors, its stroke width in pixels (0 fills) and
  its colour in 44 bytes, and the quad's six vertices come from `gl_VertexIndex` exactly as the
  stroke pipeline already does it, so there is no per-vertex binding at all; `DrawEllipseInstances`
  issues one `vkCmdDraw` for the lot. Colour and stroke moving from the push block to the instance
  is what makes this a second pipeline rather than a second entry point on the first, since the
  fragment shader has to be built for it; the rule it evaluates is the same one.

**Requires DIR.Lib 10.4**, the release that declares the shape and states the rule. The pin moves
with it.

Fourteen tests, all running on the GPU rather than skipping, and the ones that matter measure
rather than probe: ink is white over black so a pixel's red channel is its coverage, a 2:1 ellipse
stroked 3 px reads 3 px across the major axis AND the minor (the hole fraction this replaces read
1.5 across the minor), an edge pixel the boundary crosses 0.2 px past its centre reads a fraction
(the single-discard shader read 0 or 255 there and nothing between), and one instance matches the
single-draw path to one level of one channel, for a fill and for a stroke. The rotation case is at
45 degrees on purpose: a right-angle turn is only a swap of width and height, so a bounding-box
implementation would pass it, while at 45 degrees that bounding box is a circle covering two
probes the real ellipse rejects, and a second test asserts the circle does cover them.

**A device that rejects every submit is reported, not counted as healthy.** A rejected
`vkQueueSubmit` (`ErrorInitializationFailed`, the Adreno driver's answer) was absorbed as one dropped
frame, which is right for the two-frame transient it was measured on in August, and wrong for what
happened on 2026-09-22: every frame rejected for minutes, no exception, each one counted by the event
loop as a clean frame, so no recovery was ever attempted and the window froze on its last frame over
a process that was otherwise alive, running a session and still changing state under the clicks. Now `VulkanContext.LastFrameSubmitted` tells the loop
a dropped frame apart from a drawn one, so it neither ends a recovery storm nor stops asking for a
frame, and `RejectedSubmitStreakLimit` (three) consecutive rejections throw the driver's own result,
which puts the device through the mid-frame recovery, the backoff and the host's load-shed callback
like any other mid-frame failure. What it does not do, and what remains open, is recreate the device:
a device that stays dead across recoveries leaves the window frozen on its last frame and the process
alive, which is the least bad outcome available to a renderer that does not own the host's GPU resources.

## 7.45

**Atlas uploads obey the queue's `minImageTransferGranularity`.** Both font atlases flushed their
dirty rectangle with `vkCmdCopyBufferToImage` whatever the queue family allows, which is legal only
at granularity (1,1,1) — every desktop driver, hence no symptom until now. A queue reporting
**(0,0,0)** takes whole subresources only, and Mesa's `dzn` (Vulkan over D3D12, the hardware Vulkan
a WSL guest gets) reports exactly that: Khronos validation flagged every glyph flush as
`VUID-vkCmdCopyBufferToImage-imageOffset-07738`, and the undefined behaviour surfaced as
intermittent heap corruption at teardown. `VulkanDevice` now reads the granularity once at device
creation (`MinImageTransferGranularity`, forwarded by `VulkanContext`), and
`ImageTransferGranularity.Snap` widens a dirty rect to something the queue accepts: untouched at
(1,1,1), the whole subresource at (0,0,0), otherwise rounded out to a multiple and clamped at the
image edge, which the spec permits. The snap happens before the staging copy, so the bytes uploaded
are the bytes the copy claims, and the snapped rect always contains the dirty one — a narrower one
would leave stale texels and draw the previous glyph. `VkTexture` already copied whole images and is
unchanged. Measured on an Adreno X1-85 through dzn: seven validation errors a run before, none after.

## 7.44

**Rebuilt against DIR.Lib 10.2**, which is additive: popover triggers and groups, presses declared on a
tab item, and layout scroll containers. Nothing here changed to take it and every test passes as it
was; the minor keeps the chain in step, so a consumer taking 7.44 gets the DIR.Lib this was built and
tested against.

**`VkTexture.CreateDeferred` has its documentation back.** 7.31's `BytesPerPixel` was inserted between
that method and its doc comment, so `CreateDeferred` showed none and `BytesPerPixel` showed two
summaries, one of them naming a `pixelData` it does not take (CS1734 on every build since). The comment
is back on the method it describes.


## 7.43

**Rebuilt against DIR.Lib 10.0**, which cut seven things the 9.x line had kept alive for consumers that
had stopped using them. Exactly one reaches this library, and it is DEBUG-only: `HitResult.SliderHit(int)`
is gone, a slider being a `Content.Slider` leaf whose hit carries its live `SliderState`, so
`describe_ui` reports the slider's VALUE where it used to report an index into a parallel array the
consumer no longer keeps. That is also what a person driving the inspector was asking for.

**A minor, not a major, and the distinction is the org's rather than a judgement call**: DIR.Lib 9.0 was
a breaking release too and this library went 7.x to 7.x across it. A rebuild against a breaking
dependency is a rebuild.

**`dotnet build -c Release` could not have caught this.** `DebugInspector` is inside `#if DEBUG`, so the
Release build the package is made from never compiles the line that broke. The Debug build is the one
that reports it -- which is the same asymmetry `../.github/CLAUDE.md` warns about from the other
direction, where a Debug consumer of a Release package cannot find `DIR.Lib.Diagnostics` at all.

## 7.42

**Rebuilt against DIR.Lib 9.4**, whose three `Layout.Node.Wrap` additions the pixel surface renders
through the shared engine: `FirstLineReserve` (the FIRST line stops short so a run flows under a
floated corner, where a `Dock` would narrow every line and start dropping children), `LeadingGap` on a
child (group separation suppressed when the child begins a line) and `MaxLines` (the tail is DROPPED
past the cap, not clipped -- a clipped child still registers its region and keeps taking the clicks
aimed at what covers it).

A currency rebuild: nothing here calls them yet, so the declared dependency range moves and nothing
else does.

## 7.41

**The per-frame vertex ring grows on demand instead of dropping draws.** `vertexBufferSize` used to
be a fixed allocation per frame in flight, committed at startup for every window whatever it turned
out to draw, and a frame that needed more silently lost its remaining draws: `WriteVertices` returned
`uint.MaxValue`, the caller skipped the draw, and the hole stayed until something else asked for a
redraw. A consumer that could not afford holes therefore asked for the worst case up front — 256 MB a
slot, 512 MB of host-visible, persistently mapped memory before a document was even open — and on an
integrated GPU that is system RAM.

Now a frame that runs out records what the WHOLE frame needed (every write is counted whether it fit
or not, because the frame keeps issuing them), and the slot grows at the start of its next frame,
once its in-flight fence has retired and nothing on the GPU can still read the old buffer. Sized to
the larger of double and a quarter over the demand, rounded to a megabyte, capped at
`VertexRingMaxBytes` (512 MB a slot). The two slots grow a frame apart, since each can only be
replaced on its own turn; the request stays pending until both fit. `SdlEventLoop` re-arms the
window's redraw when its frame overflowed, so the frame after paints what the overflowing one could
not, and the offscreen path grows the same way at `BeginOffscreenFrame`.

Telemetry to size the initial ring from, instead of guessing: `VertexRingPeakBytes` (the most one
frame has ever written), `VertexRingCapacityBytes`, `VertexRingOverflowFrames`, and
`VertexRingOverflowed` for the frame just drawn. The debug inspector's `frame_stats` reports the first
three.

## 7.40

**Rebuilt against DIR.Lib 9.3**, which adds `Layout.Builder.Dropdown` and `PopoverState.ContentKeys`.
Nothing here changes. The rebuild is mandatory rather than a nicety because the engine's records reach
this surface directly: 9.1 added an optional parameter to a record's primary constructor, which is
source-compatible and a BINARY break, and the published consumer threw `MissingMethodException` until it
was rebuilt. A dev box cannot see it, because `UseLocalDirLib` compiles the sibling from source and never
takes the package path CI does.

## 7.39

**The instance was Vulkan 1.0, so every chained physical-device query was quietly returning
nothing.** `VkInstanceCreateInfo` carried no `pApplicationInfo`, and by spec that means an instance at
API version 1.0. The failure mode is the bad kind: a core-1.1 `*2` query such as
`vkGetPhysicalDeviceProperties2` still answers — through the 1.0 entry point — filling the base struct
and ignoring the `pNext` chain entirely. So every chained struct read back zeroed, which is
indistinguishable from a driver that does not implement the feature. On this Adreno,
`VkPhysicalDeviceDriverProperties` reported an empty driver name and a driver ID of 0; both are real
once the instance asks for 1.1.

The instance now requests the version the loader supports, capped at 1.1 — which is where the `*2`
queries became core, and asking for more would only refuse to create an instance on an older loader.
`vkEnumerateInstanceVersion` is itself a 1.1 function, so a loader that does not export it *is* 1.0 and
is treated as such.

**`VK_EXT_memory_budget` is now requested when the device offers it**, and `VulkanDevice` exposes
`TryGetDeviceMemoryBudget` beside `MemoryBudgetAvailable`. This answers a question
`VkPhysicalDeviceMemoryProperties` cannot: a heap's `size` is its CAPACITY, and where the GPU's memory
IS system memory that capacity is all of RAM — so a residency budget built on it reads "plenty" at
the exact moment the machine has begun paging, which is when a budget was supposed to intervene.
`heapBudget` is the driver's own estimate of what this process may use given everything else running,
and it moves as other applications come and go.

The figure comes from ONE device-local heap: the one this process is using most, or the largest before
anything is allocated. Two tempting alternatives are both wrong, and were measured rather than
reasoned about. Summing overcounts on a unified-memory device, where heaps can be views on the same
physical RAM — this Adreno reports a 7,989 MB heap and a 4,095 MB one, which together claim more than
the machine has. Taking the least headroom sounds safer and is worse: it selects the 4,095 MB heap
that nothing allocates from, whose headroom therefore never moves, so the number would sit still while
the heap actually in use filled up.


## 7.38

Rebuilt against **DIR.Lib 9.2** (from 9.0), the wave that gave the engine an `InputRouter`, a slider and
a popover as declarations. No source change was needed here and nothing in this library moved.

That is worth a sentence rather than silence, because 9.2 does add five members to `IPixelWidget`, which
breaks an implementer that does not derive from `PixelWidgetBase`. This library has none: its windows and
surfaces sit beside the widget tree rather than implementing it, so the additions are invisible from here.
The rebuild is what makes the new `InputEvent` shapes bind, and it is mandatory for that reason rather
than for any change in this package.

## 7.37

Rebuilt against **DIR.Lib 9.0** (from 8.20). Also carries a `.gitattributes` that checks text files out as LF, which is a working-tree change only — the blobs were already stored that way.

**No code change here, and none was needed.** DIR.Lib 9.0's break is in the pre-layout scale's
currency: `ListScrollController.SetExtent`, `TapOrDragGesture.Arm`, `FloatingPalette`'s offsets and
the layout helpers' `dpiScale:` argument now take a `DesignScale` — surface units per design unit, per
axis — instead of a bare float. This backend calls none of them, which was checked rather than
assumed.

It is released anyway, and that is the point of a lockstep: a consumer holding two backends that pin
different DIR.Lib majors does not get a choice about which one wins, it gets whichever NuGet unifies
to. 9.0 is the first DIR.Lib major where that would be a break rather than a surprise.
*Later in 7.37:* `Directory.Packages.props` moved from `src/SdlVulkan.Renderer/` up to `src/`, so it
covers the whole repo rather than one project of five. The other four had each opted out of central
management because they sat outside its subtree — which was the file's placement, not their nature.
Ten inline pins folded in at their existing versions, so consumers see nothing; the Tests' Vortice
.Vulkan pin no longer has to be kept in step with the library's by hand.

*Later in 7.37:* republished so the **DIR.Lib floor this package declares** moves onto the 9.0 build
that takes `SharpAstro.Fonts` 1.12.901, and with it `SharpAstro.Png` 3.14. Nothing here changed: the
pin is `9.0.*` and floats onto it by itself. The publish is the whole of it. A package declares the
floor its own pack resolved, and NuGet hands a consumer exactly that floor for a transitive
dependency, never the newest — so anyone reaching Png only through this backend was restoring a
decoder six published families behind the Codecs repo, and would have kept restoring it however often
Fonts.Lib or DIR.Lib republished.

## 7.36

**Android: `SDL3-CS` and `SDL3-CS.Android` move from 3.4.10.5 to 3.4.16**, clearing both constraints
that had held that pin for a year. No renderer code changed and the desktop TFM is untouched — it
stays on the 3.5.0 preview — so this reaches only Android consumers.

**The page-size one is a Google Play blocker, not advice.** Play refuses an upload whose 64-bit
native libraries are laid out for 4 KB pages, because an Android 15+ device may use 16 KB ones. Up to
3.4.10.5 the shipped `libSDL3.so` was 4 KB aligned — `warning XA0141` in every consumer build, and in
a real app bundle the *only* misaligned library among ninety, since everything the .NET Android SDK
emits is already aligned. 3.4.16 aligns `arm64-v8a` and `x86_64`. `armeabi-v7a` and `x86` are still
4 KB, which does not matter: 16 KB pages are a 64-bit concern and a Release build publishes neither.

**The mismatch that forced 3.4.10.5 is gone too.** 3.4.12.x shipped a Java bridge built for 3.4.10
against a 3.4.12 native, and SDL's own C/Java version check rejects that at launch. 3.4.16 reads Java
3.4.16 against a native `SDL-3.4.16-release-3.4.16`.

**What was verified, and what was not.** Both halves were read out of the packages (the `.aar`'s
`SDLActivity` constants against the native's version string, and the `p_align` of every `PT_LOAD`
segment), and the `net10.0-android` target compiles against 3.4.16 with no errors — which matters
because a consumer runs a renderer built against one SDL3-CS managed assembly and loads another.
**It has not been launched on a device.** A version mismatch fails SDL's startup check and nothing
else, so that remains the one test this release cannot claim. README.md now carries both package
checks so the next bump starts from evidence rather than from a guess.

## 7.35

Rebuilt against **DIR.Lib 8.20**, from 8.19. No renderer code changed; the declared floor moves so
this repo's own CI exercises 8.20 rather than meeting it only through a consumer. 8.20 is additive
(a `ListCursor` API nothing here uses), so 7.34 already bound against it correctly.

*(Written after the fact: 7.35 was released without an entry here. The commit message carried the
reasoning and this file did not, which is the gap the header above exists to prevent.)*

## 7.34

Rebuilt against **DIR.Lib 8.19**, from 8.14. No renderer code changed; this exists so the backend is
compiled and tested against the DIR.Lib a consumer will actually load. A consumer pinning a newer
DIR.Lib than its backend does gets that version anyway — NuGet unifies to the highest request — but by
luck rather than by intent, and nothing in this repo's CI had ever run against it.

What the five minors bring, all additive or fixes:

- **8.15 is the one with a visible behaviour change.** `FontFallbackResolver` now asks Unicode's
  `Emoji_Presentation` (UTS #51) FIRST, so a codepoint whose default presentation is emoji is drawn
  from the emoji face even when a text face happens to carry an outline for it. A text-presentation
  codepoint — the chess and card pictographs among them — resolves exactly as before.
- **8.17** takes SharpAstro.Fonts 1.12, capping TrueType hinting recursion at 128 frames. A crash fix,
  not a trade-off: a self-calling `CALL`/`LOOPCALL` used to exhaust the stack, and a .NET stack
  overflow cannot be caught.
- **8.14** draws a text selection's fill UNDER the glyphs rather than over them.
- **8.16 / 8.18 / 8.19** add and then finish `FloatingPalette`; nothing here consumes it.

## 7.33

**BREAKING: `OnKeyDown` takes the DIR.Lib event rather than `(key, modifiers)`.**
`SdlWindowView.OnKeyDown` and `SdlEventLoop.OnKeyDown` are now `Func<InputEvent.KeyDown, bool>?`.

The reason is a fact the old signature could not carry. SDL delivers a HELD key as a stream of KeyDown
events, and a consumer that reads each one as a fresh press drives any toggle bound to that key at the
OS repeat rate: a held key starts and stops the action several times a second, and nothing downstream can
tell those events apart. The loop now fills in `InputEvent.KeyDown.Repeat` (DIR.Lib 8.14) from the SDL
event and carries it through, never filtering here, because a STEP action wants every repeat and only the
consumer knows which of its keys step.

**`OnKeyUp`**, the release half, on both `SdlWindowView` and `SdlEventLoop` (`Func<InputEvent.KeyUp,
bool>?`, DIR.Lib 8.14). Additive: it is null by default and the loop dispatches the SDL key-up event only
when something is listening, so a host that binds nothing does no extra work and sees no extra redraws.
It exists for a binding whose meaning is "while held" rather than "on press". Note that SDL delivers no
key-up when the window loses focus mid-hold, so a consumer must treat held as a state it can be talked
out of; the safe shape is for the release to RESTORE something, leaving a lost release in the visible
paused state rather than in one that keeps running invisibly.

Port, which removes a line rather than adding one, since every consumer was already wrapping the two
arguments back into this record before handing it to a widget:

```csharp
// before
loop.OnKeyDown = (key, mods) => widget.HandleInput(new InputEvent.KeyDown(key, mods));
// after
loop.OnKeyDown = evt => widget.HandleInput(evt);
```

A handler that switches on the key reads `evt.Key`, and one that wants to ignore held keys checks
`evt.Repeat`. Requires DIR.Lib 8.14.

## 7.32

**A pipeline that masks a texture's alpha by a second texture.** `VkRenderer.DrawMaskedQuad` draws a
quad sampling two textures, taking colour from the first and multiplying its alpha by the second, so
a mask can be applied at draw time instead of being baked into the texture that carries the content.
`VkPipelineSet.MaskedPipeline`, `VulkanContext.CreateMaskedDescriptorSet` /
`AllocateMaskedDescriptorSet` / `FreeMaskedDescriptorSet` and `MaskedPipelineLayout` are the surface;
`masked.frag` is the new shader and is baked like the rest.

**A texture's staging buffer is sized by the format it was given, not assumed to be four bytes a
pixel** (`VkTexture.BytesPerPixel`, a closed set that THROWS on a format it does not know rather
than guessing again). The old assumption went wrong in two directions and neither is the one it
looks like: WIDER than four bytes and the buffer is too small, so the copy into it throws and no
format above 32 bits could be uploaded at all; NARROWER and the buffer is merely oversized, the
bytes still landing where the image copy reads them because Vulkan takes the copy's extent from the
image rather than the buffer — so a single-channel texture rendered CORRECTLY and quietly cost four
times the staging it needed, on exactly the path meant to save memory. Adding a format is one line,
and being wrong about one now fails loudly on the wide side rather than silently on the narrow.
`SingleChannelTextureTests` covers both directions.

**A headless context may be driven from whichever thread is running it.** `AssertQueueThread` checks
thread identity because on the windowed path a single owning render thread is how "nothing touches
this concurrently" is guaranteed, and identity is a faithful proxy for that. An offscreen context has
no render thread at all — successive jobs drive it one at a time, legitimately arriving on
different pool threads without ever overlapping — so there the proxy stops holding and starts
raising false alarms: a serial test collection failed on whichever pool thread it drew second, having
done nothing wrong. Headless devices are marked `MarkQueuePrivate` at creation and opt out; the
windowed path keeps the check, where the invariant it stands for is real.

## 7.31

**Every render pass carries a depth attachment, and a mesh is drawn inline in the frame.** 7.30's
scene target rendered depth-tested content into a single-sample offscreen image of its own and
composited it as a textured quad — an intermediate, with its own pass, its own pipelines, and no MSAA.
Now the shared render pass has a depth attachment, and so does every pass compatible with it (the
damage load pass, the offscreen target, the cached layer, the thumbnail capture): `VulkanDevice.
CreateCompatibleRenderPass` builds them all in one shape — colour (0), depth (1), resolve (2 under
MSAA) with the shared dependency pair — so the pre-baked pipelines bind into all of them, and what
used to be five hand-kept copies of that shape is one function. `VulkanContext.Depth.cs` owns a depth
image per render target (transient, never stored, never sampled; on a tiler it need not be allocated
at all) and the framebuffer and clear-value helpers that follow the attachment order.

`VkRenderer.BeginMeshRegion(x, y, w, h)` / `DrawMesh` / `EndMeshRegion` replace `EnsureSceneTargets`
/ `BeginScene` / `DrawMesh` / `EndScene` and the whole `SceneTarget*` surface. A region clears the
depth under its rect (so two models on one frame never test against each other), clips to it, and
maps the meshes' clip space onto it — folded into the matrix rather than set as a viewport, so a rect
half off the frame is an ordinary case. Inside the region depth decides; outside it the frame is
painter's-order as before, because every 2D pipeline now states a depth-stencil state that tests
nothing — which is what a pass with a depth attachment requires of every pipeline created against it,
and what a side-car pipeline must now supply too (see CLAUDE.md). The mesh pipeline lives in
`VkPipelineSet.Mesh`, created against the device pass at the device's sample count, so a model's
edges are antialiased by the same MSAA as the strokes around it. The mesh SPIR-V is unchanged; the
bake manifest is refreshed for a comment.

Two render-area corrections fell out of tracking the paintable region per pass: an offscreen frame
never set it, so a clip popped to empty mid-frame reset the scissor to an empty rect; and a cached
layer or thumbnail pass inherited the previous swapchain frame's region rather than its own.

**Transient attachments take lazily allocated memory where the device offers it**
(`VulkanDevice.FindTransientMemoryType`): the new depth image and the multisample colour images, which
already carried `TransientAttachment` usage but still asked for plain device-local memory. On a tiler
they are kept in tile memory and never backed at all; a desktop GPU has no such type and takes
device-local as before. Found the expensive way: at 300 dpi under 4x MSAA a large sheet's offscreen
target is 16 bytes a pixel per transient image, the depth doubled it, and a shared-memory Adreno refused
the second gigabyte. `TransientMemoryTests` asserts the choice on a device that offers the type.

`MeshRegionDepthTests` replaces `SceneTargetDepthTests`: geometry beats draw order inside a region,
painter's order holds against it, each region's depth is its own, the rect places and confines the
model (including half off the frame), the cached-layer pass depth-tests too, and a viewer-shaped frame
is silent under the validation layer at 4x MSAA. BREAKING for the scene-target API, which shipped
only in 7.30.

## 7.30

**The stroke pipeline is instanced: 16 bytes a segment, not 144.** A stroked line segment is a quad
— two triangles, six vertices — and the vertex buffer held all six, each carrying the segment's two
endpoints plus a per-vertex `(side, end)` selector: 6 floats × 6 vertices = 144 bytes to describe one
line, the endpoints identical across all six. Now one instance per segment carries just the endpoints
(`vec2` + `vec2` = 16 bytes), and `stroke.vert` expands the quad's six vertices from `gl_VertexIndex`
against a constant corner table that reproduces the old six in the same winding — so the rasterised
result is unchanged, a memory and bandwidth change with no visual one. `DrawPersistentStrokes` and
`DrawStrokeSegments` now take a segment count and issue `vkCmdDraw(6, segmentCount, …)`; the stroke
binding is per-instance at a 4-float stride. On a dense drawing of millions of hatch segments the ~9×
cut in stroke vertex data is the dominant cost in three places at once — GPU vertex memory, upload
bandwidth, and the persistent buffer's resident footprint. The stroke SPIR-V is re-baked to match.

## 7.29

**Swapchain teardown flushes the present queue.** `RecreateSwapchain` / `PrepareForSurfaceLoss` /
`RecoverFromGpuError` now follow their bounded fence drain with a `vkQueueWaitIdle` on the
graphics+present queue before `CleanupSwapchain` destroys the swapchain and its per-image
render-finished semaphores, but only when the drain SUCCEEDED. `TryDrainDevice` waits on the
graphics-submit fences; present is a separate queue operation gated by no fence, so a fence-only
drain leaves the swapchain images and present semaphores still in use by `vkQueuePresentKHR` when
they are destroyed. A validation run flags it on every window resize as
`vkDestroySwapchainKHR ... currently in use by VkQueue` (VUID-vkDestroySwapchainKHR-swapchain-01282)
and `vkDestroySemaphore ...` (VUID-vkDestroySemaphore-semaphore-05149): benign on desktop NVIDIA
(it serialised), but the destroy-while-in-use pattern that surfaces on Adreno as a rejected
`vkQueueSubmit`. The `CleanupSwapchain` comment that claimed a fence drain was enough is corrected.

Gated on drain success so the no-hang property is preserved: a successful drain proves the GPU is
healthy, so the queue-wait returns within a frame; on a drain timeout (wedged GPU) the teardown is
forced regardless, exactly as before. A device lost during the flush is routed through
`NoteDeviceLost` like every other queue op, never thrown. This is a windowed path (it needs a real
surface), so it is verified under the validation layer on a live resize rather than in headless CI;
the offscreen validation tests are unchanged and still pass.

## 7.28

**Deferred destruction** (`VulkanContext.DeferDestroy` / `VkRenderer.DeferDestroy`,
`PendingDeferredDestroys`). A consumer hands an image view, image, memory, buffer or shared-pool
descriptor set to the context instead of destroying it, and the context destroys it once every frame
that could reference it has retired: the frame being recorded and every frame in flight. The
retirement is read off the same fence waits the frame loop already performs, so it costs no drain.
`VkTexture.Dispose` goes through it, so disposing a texture in the frame that drew it is now legal.

Why: the drains this library offered (`TryWaitAllFramesIdle`, `TryWaitPriorFramesIdle`) retire
PREVIOUS frames and cannot retire the one being recorded, so a consumer destroying a resource
mid-frame was correct only if nothing earlier in the same frame had bound it, a property of call
order across hooks the consumer usually does not own. The TianWen FITS viewer got that wrong while
reasoning correctly about fences: a pre-render-pass hook bound a document's channel views, the
render callback replaced the document and destroyed them, and the frame reached the GPU with dangling
views. The validation layer reads it as "vkCmdBindDescriptorSets(): ... invalid state ... VkImageView
was destroyed"; the driver as `nvlddmkm 153`; Windows as a `LiveKernelEvent 141` watchdog with the
process gone (2026-08-27, twice in a day). Adoption notes, including the per-frame descriptor-set
pattern that goes with it: `docs/deferred-destroy-adoption.md`.

**The damage pass's `loadOp LOAD` is now ordered after its own layout transition.** The shared
external dependency admitted `COLOR_ATTACHMENT_WRITE` alone; a LOAD reads, and synchronization
validation reported a READ_AFTER_WRITE hazard once per swapchain image on every partial frame. The
read is admitted in `FillSubpassDependencies` for every pass, because dependencies are not among the
things render-pass compatibility exempts: widening the LOAD pass alone made it incompatible with the
framebuffers and pipelines built against the clearing pass (VUID 00904 / 02684). Only consumers of
`AddFrameDamage` ever ran that pass.

## 7.27

The inspector's MCP surface exposes `move`. The verb has existed on the wire since 7.25 -- added
precisely because press-based verbs could not drive hover -- but it was never declared as a tool, so
every MCP-driven session had a hole exactly where hover behaviour lives and the only way through was to
open the loopback socket by hand.

Hover is the one gesture no other verb can synthesize: `click`, `drag` and `press_hold` all arrive with
a button DOWN, so a hover highlight, a tooltip, a cursor change or a pointer-tracking readout is
unreachable through any of them. That is a real gap on a GPU host, where hover genuinely exists -- unlike
a terminal, whose mode 1002 reports motion only while a button is held, which is why Console.Lib's
inspector deliberately has no bare `move` at all.

Nothing changes on the wire; this is the tool declaration the verb was always missing.

## 7.26

Follows DIR.Lib 8.13, whose `DrawText` now seats the baseline on the FACE's metrics instead of on the
run's own ink, and takes the same fix here.

Centring the measured bounds made the baseline a function of the text: "a" landed at one height, "b"
lower because its ascender inflated the box, "g" higher because its descender did. A single label never
looks wrong; a ROW of independently centred labels cannot agree, which is where it shows -- a board's
file letters step at b, d and g, a toolbar steps wherever one caption carries a descender.

The formula itself is no longer restated here. It now lives in `DIR.Lib.TextBaseline`
(`LineHeightFactor`, `LineHeight`, `WithinLine`), because it previously existed in four copies -- this
renderer, RgbaImageRenderer, WebGlRenderer, and a fourth INVERTED copy in MathLayout.GlyphBox that was
reconstructed from a comment describing the others. That fourth copy is what broke when the original
changed.

Face metrics come from `SdfFontAtlas.Rasterizer.GetVerticalMetrics`, already reachable; a face declaring
no hhea falls back to the run's ink exactly as before.

## 7.25

Damage-based repaint: a frame can preserve the previous one and paint only the region that changed.
`BeginFrameRenderPass` picks a `loadOp = Load` variant of the swapchain pass (identical in attachments,
samples, subpass refs and dependency pair, so the pre-baked pipelines stay compatible) and confines the
frame to the accumulated damage; render area and scissor are the region while the VIEWPORT stays the
full surface, because an app submits geometry in surface coordinates and shrinking the viewport would
squash the frame into the region rather than crop it to it. `AddFrameDamage` / `MarkFullFrameDamage`
declare it, and every clip is intersected with it -- DIR.Lib has already intersected a clip with its
parents but knows nothing about damage, so a widget clipping to its own pane would otherwise repaint
that whole pane on a frame that needed a status bar.

Damage is tracked PER SWAPCHAIN IMAGE, which is the only hard part. With 2-3 images rendered in
rotation the image acquired this frame holds the frame from 2-3 frames ago, so what must be repainted
into it is the union of every frame's damage since THAT image was last painted. Using the current
frame's damage instead leaves stale pixels that appear only at particular frame counts and only in the
images that missed an update -- an intermittent rendering glitch with no visible connection to
bookkeeping. `SwapchainDamage` is a separate type for that reason: the Vulkan half is mechanical, this
half has the algorithm, and nine tests exercise it with no device.

MSAA takes the clearing path unconditionally, since the multisample attachment is transient and cannot
be reloaded from the resolved image; `CreateLoadRenderPass` returns Null, which is correct rather than
merely safe.

`SdlWindowView.OnBeforeFrame` runs once a frame is committed to, before the pass opens -- damage has to
be declared there, because by the time `OnRender` runs the pass is already begun. Deliberately distinct
from `CheckNeedsRedraw`, which decides WHETHER to draw and is a predicate: giving that side effects
would mean a declined frame reconfigures the next one.

The inspector gains a `move` verb. Both existing pointer verbs press a button, and a press means
something -- in a viewer it starts a pan -- so a whole class of hover-driven behaviour was undrivable:
highlights, tooltips, the cursor shape, and any repaint decided by where the pointer is. That last one
forced the issue, since a viewer's most frequent redraw is the pointer crossing the image and there was
no way to synthesize it at all.

Rebuilt against DIR.Lib 8.8 for `LayoutDamage` and its unconditional layout capture.

## 7.24

A cached layer: `VulkanContext.CachedLayer` renders expensive, rarely-changing content into a
SAMPLEABLE secondary target on the live device, so a frame that changed nothing but its chrome blits it
instead of re-shading it. Built for a FITS viewer whose image quad runs a heavy stretch/debayer shader,
where a mouse move that only updated a status-bar readout was re-rendering the whole image; the same
shape fits any app with a cheap overlay over an expensive picture.

It is a sibling of `ThumbnailCapture`, not of `CreateOffscreen`: the pass is recorded into the frame's
OWN command buffer from the `OnPreRenderPass` hook, so there is no extra submit, no extra fence and no
queue-stalling wait. The attachment finalises as `ShaderReadOnlyOptimal` and carries `Sampled` usage
plus a descriptor set, so the result is drawn with the existing `DrawTexture` and needs no new shader or
pipeline in any consumer.

ONE TARGET PER FRAME IN FLIGHT, which is correctness rather than tuning. The frame fence retires frame
N-2, never N-1, so a single shared target would be rewritten while the previously submitted frame was
still sampling it -- the hazard `VkFontAtlas.Grow` guards with a drain, and which the Adreno X1-85
answers by failing the next `vkQueueSubmit`. Draining instead would be worse than the problem: content
that changes every frame, like a zoom drag, would stall the render thread on each one. Per-slot, a
change costs `MaxFramesInFlight` re-renders and then nothing. `IsCachedLayerSlotRendered` is part of the
API because a slot that has never been rendered is still in `Undefined` layout, and sampling it is
undefined content rather than an error, so nothing would throw and nothing would warn.

The shared subpass-dependency pair now covers a fragment-shader read as well as a transfer read. Every
render pass here must declare an IDENTICAL dependency list or the pre-baked pipelines they share stop
being render-pass compatible (VUID-vkCmdDraw-renderPass-02684), so a pass needing different
synchronisation cannot simply declare its own; widening the existing entry is the only shape available,
and widening only ever adds ordering.

`TryWaitPriorFramesIdle` is public. A consumer can own GPU images too, and a pipeline destroying a
sampled texture faces exactly the hazard this bounded drain exists for -- previously its only options
were an unbounded `vkDeviceWaitIdle` or nothing.

Also fixes an unresolvable `cref` in `SdlInputMapping`'s docs (an extension member cannot be crefed).

## 7.23

`SdlVulkanWindow` implements `SharpAstro.AppShell.IActivatableWindow`, so `window.Activate()` brings a
window forward for a single-instance hand-off with the correct restore behaviour. The three members it
needs already existed; what was missing was the RULE, and the rule is not obvious. Two applications
wrote it independently and both got it wrong the same way: restore, then raise. Restoring un-maximises
(`SW_RESTORE`, which is also what this class's own `Restore` summary says it does), so opening a second
file knocked a maximised window back to its floating size. Raising without restoring is equally wrong
in the other direction, leaving a minimised window off-screen at -21333,-21333 while it holds input
focus. Restore only when actually minimised; a window minimised FROM maximised comes back maximised.

Adds a dependency on SharpAstro.AppShell (one small managed assembly whose own only dependency,
`Microsoft.Extensions.Logging.Abstractions`, this package already had). Referenced on every TFM
including android: its Windows-only foreground grant is already guarded at runtime, and activation is
a real concern on android too, so excluding that leg would assert otherwise.

## 7.22

The inspector screenshot no longer reads the swapchain image it just presented. The old readback ran
after vkQueuePresentKHR and transitioned an image the presentation engine owned, without re-acquiring
it -- two spec violations the Khronos validation layer reports on every single screenshot
(WRITE_AFTER_PRESENT, plus "layout transition on a presentable image that has not been acquired"),
verified against SDK 1.4.357 with synchronization validation on. An illegal barrier against an image
the compositor still holds entitles the driver to park the whole queue behind it, and the readback ran
at exactly the wedge-shaped moment: between frames, right after a present, with the next frame
queueing up behind it. That makes it the leading candidate for the field wedge below -- a real
submission sitting unretired for seconds, no TDR, self-resolving -- and it is in any case the only
spec violation the layer finds in the renderer.

The capture now rides the frame itself: `screenshot` became a frame-spanning inspector verb,
`RequestPresentCapture` marks the next presented frame, the copy is recorded into that frame's own
command buffer between vkCmdEndRenderPass and the present (the process owns the acquired image
there), and the readback is consumed at the BeginFrame that waits the same fence index -- the
ThumbnailCapture pattern, no extra submit, no extra fence, no queue-stalling wait. A screenshot must
never be able to wedge the app it is observing.

The GPU-wedge recovery no longer rebuilds a swapchain on a device it has already abandoned. Two
bounded waits meet on this path and each is correct alone. SdlEventLoop stops waiting for the
sacrificial recovery task after GpuWedgeRecoveryDeadlineMs, abandons it, and hands the terminal
decision to the host; its comment justified that by saying the task's thread stays blocked forever
on a dead device. TryDrainDevice makes that false. It is bounded too -- deliberately, so an
unbounded vkDeviceWaitIdle cannot freeze the window -- and it FORCES its teardown on timeout. So a
recovery that is merely slow rather than permanently blocked reliably wakes up, finishes rebuilding
sync objects, and calls CreateSwapchain against a surface the host destroyed while it slept. Seen in
the field on an Adreno X1-85: an access violation inside vkGetPhysicalDeviceSurfaceCapabilitiesKHR,
after 2.5s of stuck fence, a 1s drain timeout and a 4s abandon. The surface handle is not nulled by
the destroy, so it is dangling rather than Null and no null check would have caught it.

`VulkanContext.Abandon()` is the missing signal, set by the loop BEFORE it invokes OnGpuWedged.
`RecoverFromGpuError` checks it at three points -- entry, after the drain (where the deadline is
realistically blown), and immediately before the swapchain rebuild -- and returns instead of
touching anything the host may now free. `IsAbandoned` exposes it; `VkRenderer.AbandonDevice()` is
the pass-through the loop calls.

Dispose then LEAKS the device, surface and instance on an abandoned context rather than freeing
them. That is the same decision the loop already made about the thread, carried through: a thread
nobody can join is still entitled to read those handles, so freeing them is what turns a benign leak
into an access violation. It is reachable only while the app is on its way down, and it logs event
116 rather than doing it silently.

Pinned by AbandonedContextTests, which reproduces the crash rather than describing it -- deleting
the checkpoints kills the run with exit code -1073741819, the code the field crash reported. The
Dispose half is deliberately unpinned: it only matters in the true race, so every deterministic
sequence a test can write is already safe by the checkpoints, and an assertion that survives the
removal of its own subject is worse than none.

## 7.21

Follows DIR.Lib to 8.3, with nothing to port. 8.0's one breaking change is TabBar becoming
TabBar&lt;TSurface&gt; : PixelWidgetBase&lt;TSurface&gt;, which this backend references zero times;
8.1 through 8.3 are additive -- the tab strip becomes a shared Layout tree (TabStripTree), plus
CompositeWidget&lt;TSurface&gt; and IconKind.Plus / Minus.

A pin bump rather than a feature, and it earns a release because this was the LAST member still
declaring 7.29 while consumers pin 8.3. A declared version below what the graph actually resolves
makes that consumer unify DIR.Lib upward BY VERSION rather than by intent -- correct by accident,
and it stops being correct the moment two lagging members disagree. Console.Lib 4.26 and
WebGl.Renderer closed their halves already, so this closes the set.

## 7.20

The IME half of text input, which SDL has always delivered and this backend threw away.
SdlWindowView.OnTextEditing surfaces SDL_EVENT_TEXT_EDITING (the in-progress composition, its
caret and its selection length); the event loop had a TextInput case and NO TextEditing case at
all. With a CJK input method every keystroke before the commit arrives on that event and nowhere
else, so an app handling only TextInput can accept Latin and nothing else -- and sees nothing on
screen while the user types.
SdlVulkanWindow.SetTextInputArea wraps SDL_SetTextInputArea, which is the only way to tell the
platform where the caret is. SDL does not track it, so without the call an input method has
nothing to anchor to and puts its candidate window over the text being typed.
Follows DIR.Lib to 7.29, whose TextInputRenderer.Render is binary-breaking (it returns the caret
rect now and takes a fallback resolver), so this rebuild is required rather than bookkeeping.

## 7.19

Follows DIR.Lib to 7.28, where a text field is a declaration (Layout.Content.TextInput).
describe_layout reports one: what it holds, whether it has the keyboard, and its placeholder
so an EMPTY field is still identifiable rather than an anonymous rect. "Which box is
focused" is the question every text-input bug starts from and was unanswerable from a layout
dump. Painting the leaf itself is the base class's job, so this backend needs no other
change; the pin is a FLOOR rather than a follow, since the type does not exist before 7.28.

## 7.18

Follows DIR.Lib to 7.27, where clips nest: a push inside a push draws in the intersection and
a pop restores the enclosing clip. The base owns the stack now, so this backend implements
ApplyClip/ClearClip -- one absolute, already-intersected region, which is what
vkCmdSetScissor takes anyway -- instead of overriding PushClip/PopClip. BeginFrame and
BeginOffscreenFrame drop the stack: a scissor lives on the command buffer, so a fresh one has
already discarded the region, and a widget that threw between its push and its pop must not
leave every later frame clipped to a rect nobody can name.

## 7.17

Follows DIR.Lib to 7.26 and overrides its new DrawTriangles, so a caller holding this as a
Renderer<TSurface> keeps the FlatPipeline: one draw call for the whole list, against the
base's row-at-a-time fill, which here costs a quad and a push-constant update per scanline.

## 7.16

The inspector's `scroll` takes modifiers, like `click`, `clickLabel` and `drag` already do.
A wheel tick usually means something ELSE with one held -- Ctrl zooms, Shift scrolls
sideways -- so hard-coding None left those readings unreachable from a script or a test.
Worse for an app that reads the modifier off the global keyboard state rather than off the
event: nothing but a real key press moves that state, so no amount of synthesized input can
get there. Both halves change together, the MCP tool and the in-process handler.

## 7.15

Follows DIR.Lib to 7.21 (cross-axis alignment on a Stack, plus icons that draw at their
declared size and ink one consistent height). All of it lands in the shared painter and the
arrange pass, so there is no renderer change; the pin keeps the family on one DIR.Lib.

## 7.14

Follows DIR.Lib to 7.19, whose three theme marks (ThemeSystem / ThemeLight / ThemeDark)
this backend paints through the same PixelWidgetBase every other leaf goes through, so
there is no renderer change here. Taken in lockstep so a consumer of this and Console.Lib
resolves ONE DIR.Lib by intent rather than by highest-version.

## 7.13

No renderer code change: the lockstep rebuild that re-pins DIR.Lib 7.14 -> 7.18, so a
consumer of both resolves ONE DIR.Lib rather than unifying two by luck. 7.18 adds
Layout.Content.Icon, whose pixel drawing lives on DIR.Lib's own PixelWidgetBase, so
nothing here had to learn about it.

## 7.12

Additive. SdlVulkanWindow.SetIcon(RgbaImage) sets the window's icon: the title bar mark, the
taskbar or dock button, the alt-tab entry. Not a Windows convenience: a Win32 icon resource in
the executable covers Windows and nothing else, because X11 and Wayland read the icon from the
window, so a Linux build without this call shows the desktop's placeholder. Takes RgbaImage,
which is what every decoder in this stack already produces, and hands SDL the little-endian
spelling of memory-order RGBA. SDL copies the pixels, so the caller's array is free on return.

## 7.11

The fallout of one device-loss investigation, plus a lockstep rebuild against DIR.Lib 7.14.
Five renderer changes, four of them things the validation layer had been unable to tell us.
VK_ERROR_DEVICE_LOST is now TERMINAL rather than entering mid-frame recovery. Recovery rebuilds
sync and the swapchain and continues, which cannot work once the device and every object it owns
are gone: each later call returns DEVICE_LOST again, so the rebuild re-failed on the next submit
-- three attempts burned inside 34ms, and the right outcome (hand the session to a successor)
was reached only because the recover-streak detector happened to trip. That detector logs a
"recovery storm" and asks the app to shed load, which reads as a workload problem and points
diagnosis away from a dead device. Event 110 "Recovering swapchain" is now logged only for
errors that path can actually recover; new event 115 names device loss for what it is.
Each swapchain image gets its OWN present-wait semaphore. render-finished was allocated per
frame in flight and indexed by frame slot, but vkQueuePresentKHR's wait completes only when the
presentation engine is done with the IMAGE, and image count is not bounded by MaxFramesInFlight
-- so with more images than frames a new submit could re-signal a semaphore an earlier present
was still waiting on (VUID-vkQueueSubmit-pSignalSemaphores-00067). Its lifetime moves onto the
swapchain so a resize that changes the image count rebuilds it. Latent while presentation is
regular, which is why it surfaced over Remote Desktop.
ONE shared subpass-dependency pair, in VulkanDevice.FillSubpassDependencies, used by all six
creation sites. Every pass had written its own: render-pass compatibility covers dependencies,
so pre-baked pipelines drawing in a pass with a different dependency count were rejected
(VUID-vkCmdDraw-renderPass-02684, "2 != 1"). And every external dependency used srcAccessMask =
0, which orders execution but creates no memory dependency against the previous frame's storeOp
write, so the layout transition raced it -- a WRITE_AFTER_WRITE hazard on every alternating pair
of frames. srcAccessMask is now ColorAttachmentWrite.
Diagnostics that were lying: validationReport now reports layerAvailable and active, not just
the DEBUG + SDLVK_VALIDATION gate -- on a host with no Khronos layer installed it read
enabled:true with zero messages and zero hazards, indistinguishable from a clean run, and was
read as one during the very investigation above. A zero message count is evidence only when
active is true. The sync-hazard counter matched only the retired "SYNC-HAZARD" token and so
reported zero while the ring buffer plainly held WRITE_AFTER_WRITE messages.
And a DiscreteGpu PREFERENCE (never a requirement, so integrated-only hosts are unaffected):
both pickers took the first device meeting their requirements, so a machine with both cards ran
on whatever the loader enumerated first, possibly integrated graphics on shared memory. New
event 501 records device name, type, driver version, API version, queue family and how many
devices were enumerated -- nothing had logged the selection, so no GPU report could be
attributed to hardware from our own logs.
Then the rebuild: the DIR.Lib pin moves 7.11 -> 7.14 (that the OLD pin and this package's NEW
version both read 7.11 is coincidence -- the two version lines are independent). Everything in
that range is upstream text metrics, which a Vulkan surface feels in full, being a pixel surface.
DIR.Lib 7.12 is additive: TextInputRenderer takes a palette, the shape TabBar got in 7.10.
DIR.Lib 7.13 bounds the SDF atlas's rasterize retry -- directly relevant here, because this
backend's VkSdfFontAtlas is a thin shell over that core, and an unrasterizable glyph used to
pin IsDirty true and present as "the render never settles" with nothing naming a font. It ALSO
carried an unannounced measurement fix, only written up in 7.14's notes: a whitespace advance
now comes from the font instead of being borrowed from the 'n' glyph, and in DejaVu every
measured space had been 1.99x too wide. Text laid out with space padding therefore moves
narrower here; U+2007 FIGURE SPACE is the pad that holds a column, being digit-width by
definition.
DIR.Lib 7.14 makes TextFit.ShrinkToWidth return a size it actually measured, so a run fitted
with TextTrim.Shrink stops drawing a fraction of a pixel past its rect. Shrink is opt-in and
the default is End, so a consumer that never asks for it renders byte-identically.
Released alongside Console.Lib 4.20 and WebGl.Renderer 1.18, deliberately as a set: a consumer
holding two backends built against different DIR.Lib minors unifies on the higher one by luck
rather than by intent.

## 7.10

Offscreen rendering survives a rejected queue submit. 7.9 made a rejected submit a dropped
frame on the swapchain path only; offscreen submits still went through CheckResult, so on the
same Adreno rejection an offscreen frame threw -- and threw BEFORE advancing the frame index,
leaving that fence reset with nothing behind it and the next BeginOffscreenFrame waiting on it
with ulong.MaxValue. That is the unsignalable-fence wedge 7.9 removed, reached through the
rejection path instead: moving vkResetFences next to the submit closed the begin-to-end window
but not this one. Any headless consumer could hang outright rather than fail -- a raster or
thumbnail worker, or a test. Reproduced on a page carrying 2.5M stroke commands.
An offscreen submit now RETRIES a rejection rather than degrading: the swapchain path can drop
a frame because the next one corrects it, but offscreen the frame IS the output, so dropping it
would read back whatever the target held and return stale or blank pixels indistinguishable
from a render. A rejected submit never consumed the command buffer, so re-submitting is the
same work, not a duplicate; attempts are capped and a persistent rejection still surfaces.
A submit that did not take is recorded as not pending, so no wait can park on a fence that can
never signal, and readback throws rather than mapping a buffer the copy never wrote.
Also on the swapchain path: the pending mark is now cleared on a rejection instead of only
being absent on an index never submitted under, so a rejection FOLLOWING a successful submit on
the same index no longer leaves the mark set against a just-reset fence. No API change.

## 7.9

GPU wedge root cause: a queue submit the driver REJECTS is no longer treated as one in
flight. Adreno (X1-85 / qcdx8380, Windows-on-ARM) returns VK_ERROR_INITIALIZATION_FAILED from
vkQueueSubmit, which is not a legal return there, and this code tolerated it on the belief that
the work executed anyway and the fence still signalled. It does not. A rejected submit left a
fence reset with nothing behind it, so the next BeginFrame waited on a fence nothing could ever
signal -- indistinguishable from a hung GPU while the GPU sat idle (Windows logged no TDR for
any of these). Escalation recreated the sync objects, which bought exactly MaxFramesInFlight
more frames before the same stall, and it looped: a field capture caught 7 cycles / 14
rejections and a black-but-responsive window. A rejected submit is now a dropped frame -- fence
left unmarked and never waited on, present skipped, acquire semaphore replaced (it is signalled
with no queued wait, so reuse would double-signal a binary semaphore), pending thumbnail copy
cancelled, frame index advanced.
Two related fixes. (a) The frame fence is now reset immediately before the submit that signals
it rather than in BeginFrame, so no exit between the two -- atlas grow, texture upload, the
consumer's whole render callback -- can orphan it; same on the offscreen path, where the wait is
unbounded and the resulting hang therefore permanent. (b) The stuck-fence recovery drain now
runs even in stuck mode (it is on the sacrificial task, where a block is abandonable): tearing
down fences and semaphores a merely-SLOW frame still references is illegal, and is what poisoned
the driver into rejecting every later submit.
Diagnostics that found it, and keep it findable: a submission ledger (does the fence being
waited on have work behind it, and how many frames back) plus device-object churn and the idle
gap since the last clean frame, all on the wedge breadcrumb and the "fence late" line;
VK_ERROR_DEVICE_LOST is now reported distinctly instead of as a generic VkException, so a log
can tell a real GPU reset from a fence with no submission. Queue/command-pool external
synchronization is ASSERTED (submission is single-owner per device) rather than locked, DEBUG
only. Unconditional diagnostics now go through an injectable ILogger (SdlVulkanLog, defaulting
to a stderr sink so an unwired host still gets the breadcrumbs) with every message a
[LoggerMessage] source-generated event. NEW dependency: Microsoft.Extensions.Logging.Abstractions
(interface-only). NEW: VulkanContext.AbortFrame/SubmissionLedger/DeviceLost,
VkRenderer.AbortFrame/SubmissionLedger, VulkanDevice.ChurnCounters/DeviceChurn, SdlVulkanLog.
Version jumps 7.6 -> 7.9 to match the downstream fork this landed in first, so a consumer
tracking either cannot see the chain move backwards.

## 7.6

Lockstep rebuild against DIR.Lib 7.8 (7.5 -> 7.8; 7.6 and 7.7 needed nothing here). No renderer
code change. DIR.Lib 7.8 makes the PIXEL painter fit every text run to the rect the layout engine
arranged for it, this renderer being one of the surfaces that painter draws on. 7.7 had shipped
Layout.Content.Text.Trim as an author's declaration that only the cell painter acted on, so on a
GPU surface an over-wide run still drew over its neighbour; TextTrim gains Shrink (scale down,
keep every character) and None (draw whole and overhang, the previous behaviour). BEHAVIOUR
CHANGE upstream, not additive: a run left on the default TextTrim.End now ellipsizes here where
it used to overhang. 7.6 also drops a superseded local pin bump that upstream had already landed.
Later in 7.6, no X.Y bump: AssemblyVersion is now DERIVED from VersionMajorMinor in
src/Directory.Build.props for every project, instead of being restated as a literal per csproj.
This repo had the worst case in the family -- SdlVulkan.Renderer published 6.11.0.0 from 6.12 all
the way through 7.6, and both WebView projects published 6.0.0.0, each against an informational
version two majors ahead. The VersionPrefix half of exactly this drift was fixed earlier and the
AssemblyVersion half was missed, which is the more damaging half: CI stamps -p:Version and
-p:FileVersion but NOT -p:AssemblyVersion, so a stale VersionPrefix only spoils a local pack while
a stale AssemblyVersion spoils the package. All three are now 7.6.0.0.
Deliberately NOT a minor bump: the value only moves UP, and the runtime rejects a loaded assembly
LOWER than the compiled reference, never higher, so anything already built against 6.11.0.0 or
6.0.0.0 keeps loading. Floating 7.6.* consumers take it on the next restore. Only Major.Minor is
significant -- the build counter stays out -- so republishing 7.6 does not churn identity again.
Matches DIR.Lib 7.8's identical correction; the property now lives in the props file in all seven
sibling repos, with none left in any csproj.

## 7.5

Lockstep rebuild against DIR.Lib 7.5, and the pin now tracks the latest RELEASED DIR.Lib
rather than trailing a line behind it. No renderer code change. DIR.Lib 7.5 resolves installed
fonts by the family each face DECLARES rather than by a guessed file name, so a face whose file
isn't named after it resolves (Segoe UI Symbol lives in seguisym.ttf) and so does every face of
a .ttc past the first -- both previously unreachable. Those are named by FontFaceId (a path, or
path#index for a collection), which ManagedFontRasterizer honours; since the id is already every
glyph/atlas/shaper cache key, faces separate without further plumbing. FontFallbackResolver
gains TryResolveFont/CanRender and role-based construction. ADDITIVE.

## 7.4

Lockstep rebuild against DIR.Lib 7.4, no code change here. 7.4 gives PixelMeasureContext
per-axis scales plus a CellAuthored factory, the mirror of Console.Lib's
CellMeasureContext.PixelAuthored, so a cell-authored tree can now be arranged on a pixel
surface. It also adds PixelWidgetBase Arrange/Paint/RenderLayout overloads taking the measure
CONTEXT instead of a bare dpiScale, so the painter reads FontPath / FontScale / corner radius
from the same object the measure pass used; previously the scalar was threaded into both by
hand. Additive, and the scalar overloads delegate to the context ones with an isotropic
context, so this renderer paints byte-identically. Rebuilt to keep the family on one DIR.Lib.

## 7.3

DebugInspector folds onto DIR.Lib's DebugInspectorCore (needs DIR.Lib 7.3): 940 -> ~600 lines. Gone
are the private TCP accept loop, the newline framing, the discovery responder and descriptor, the
command queue, the whole InspectorCommand record hierarchy with its BuildCommand parser, and the
batch/wait state machine -- all of it duplicated what the core does. What stays is what is actually
SDL: the verbs, ResolveInputKey/ResolveModifier, and pressHold, now expressed as a background
IDebugInspectorOperation (pressed in Begin, released in Advance, deliberately NOT exclusive so a
screenshot can be taken mid-hold). The prior split had been measured -- only ~48 lines touched SDL --
but that measured SDL-COUPLING, not shareability: a frame-stepped batch calls no SDL function yet
presumes a loop with frames, which the core could not express until now.
ONE discovery protocol: `dir-inspect` on 239.255.77.91:47892, was `sdlvk-inspect` on
239.255.77.90:47891. A sidecar now tells surfaces apart by the `kind` field ("sdl") rather than by
which port answered, and drops replies it cannot drive. The sidecar moves in lockstep.
SECURITY: DebugInspectorOptions.BindAddress defaulted to IPAddress.Any, so this command server --
which injects input, captures the framebuffer and reads app state -- accepted connections from the
whole LAN. The core binds LOOPBACK with no opt-out.
BREAKING, but only in a DEBUG build: DebugInspectorOptions loses BindAddress, Port, DiscoveryGroup
and DiscoveryPort. The core owns addressing, so they could only be accepted-and-ignored, and a
`Port = 5000` that silently does nothing is worse than one that fails to compile. The whole type is
#if DEBUG, so it is ABSENT from this published Release package and no package consumer can hit
this; only a local Debug build against the sibling can, which is exactly who needs telling.
EnableDiscovery survives -- not announcing yourself is still a real choice.
ping now answers with the core's {"ok":true,"protocol":N,"app":"..."} rather than the bare string
"pong". The sidecar accepts BOTH, because it ships separately from the app it drives: its old
`GetString() ?? "pong"` would have THROWN on an object, and its liveness probe would have reported a
healthy app as dead. It also now connects to 127.0.0.1 instead of the discovery reply's source
address, which a Hyper-V or WSL bridge makes the one address guaranteed to refuse a loopback-bound
server. Corrected a sidecar comment claiming the pump drains from OnPostFrame: it is
OnLoopIteration, which is why a minimized window still answers and is correctly reported alive.
4 host-contract tests (suite 43 passed / 1 skipped).

## 7.2

The inspector REFUSES an unrecognised `mods` string instead of resolving it to None. Behaviour
change, deliberate: resolving the unknown to None delivered a BARE key or click, and a bare key is
frequently a different valid binding rather than a no-op -- so a typo ("ctlr") or an unsupported
modifier ("Cmd") surfaced as the app IGNORING a correct chord rather than as a bad request. The
worked example is chess, which flips its board on Ctrl+F while bare `f` selects file f. This also
made ResolveModifier the one resolver in DebugInspector that did not reject what it could not
understand -- ResolveInputKey has always thrown, listing the valid names -- so this is a
consistency fix as much as a safety one. A partial match still resolves ("ctrl+cmd" is Ctrl), so a
chord that CAN be delivered is never blocked; only text with nothing recognisable is refused. The
four sidecar tools carrying `mods` (click, key, drag, pressHold) all default to "None" and say so,
so no existing driver changes. Console.Lib 4.7 made the same call for the terminal inspector's
`key` verb; the two now agree. ResolveModifier/ResolveInputKey are internal (not private) with
InternalsVisibleTo the test project, since what an injected key MEANS is otherwise only observable
against a live SDL window -- 20 tests.

## 6.33

VkRenderer.FillRoundedRectangle: a real GPU override of DIR.Lib 6.20's scanline
fallback. One rounded-box SDF quad per rect instead of one FillRectangle per row, with
antialiased corners, and single-coverage so a translucent fill blends exactly once.
New roundrect.vert/.frag + RoundRectPipeline. The box parameters (half extents, radius)
ride on VERTEX ATTRIBUTES, not push constants, so the shared 84-byte push block stays
byte-identical across every pipeline -- growing it for one pipeline is the per-stage
mismatch that ellipse.vert documents as an llvmpipe shader-compiler SEGV. A zero radius
delegates to FillRectangle, so the square path is untouched.
Re-pins DIR.Lib 6.19.* -> 6.21.* (FillRoundedRectangle lands in 6.20, Layout.Node.Radius
in 6.21; pinning straight to 6.21 keeps this a single re-pin).

## 6.30

DeviceTransform GPU compose (needs DIR.Lib 6.17). VkRenderer.DeviceTransform folds the
content->device affine into the projection in UpdateProjection — the compose stays a
Matrix3x2 (2D affine, no wasted lanes) and only widens to the mat4 push-constant at upload,
so the whole frame (text included) rotates/scales as one. Identity transform is byte-
identical to the previous screen-space projection. Verified via offscreen render + readback
(180° flip moves a top-left fill to the bottom-right).

## 6.19

Opt-in Vulkan validation diagnostics (VulkanValidation). The Khronos validation layer's
output was previously enabled in DEBUG but dropped to the loader's default sink; now a
debug-utils messenger routes it to a prefixed stderr line + a bounded ring buffer. Adds an
opt-in SYNCHRONIZATION validation switch (the GPU memory-hazard checker behind the wedge
class) and a validation_report inspector/MCP tool. Gated: DEBUG or SDLVK_VALIDATION=1
(+ SDLVK_SYNC_VALIDATION=1 for sync); zero cost + no layer in a normal Release build.

## 6.18

Idle the render loop while a window is minimized. A minimized window reports a non-zero
pixel size on Windows, so the old size guard never caught it and the loop busy-spun through
swapchain recreation (~270ms/frame) for invisible frames. Gate redraw on the SDL minimized
flag (SdlVulkanWindow.IsMinimized): ~0% CPU while minimized, instant repaint on restore.
DEBUG-only: inspector minimize/maximize/restore commands + a per-iteration command pump
(SdlEventLoop.OnLoopIteration) so commands drain on a minimized window.

## 6.17

GPU-wedge resilience. Stuck-fence recovery now runs on a sacrificial background task the
render thread only polls (on a truly hung GPU the driver can block INSIDE vkFreeMemory /
teardown — observed on Adreno: the old synchronous recovery froze the render thread
permanently); deadline blown or repeated stuck escalations → OnGpuWedged (new host
callback) + clean loop stop. SDF atlas: per-frame upload BYTE budget alongside the glyph
count cap (MTSDF quadrupled bytes/glyph vs R8), one-frame quarantine for glyphs on a
just-appended page (first transition and first sample no longer share a submission), and
a FrameStats wedge breadcrumb logged at fence-stuck escalation.

## 6.16

Re-pin DIR.Lib 6.6 -> 6.8. DIR.Lib 6.8's DIR.Lib.Shaping satellite is rebuilt against
SharpAstro.Fonts.Shaping 1.5.551 (Fonts.Lib F6 zero-alloc bidi + F7 HarfBuzz-style
coverage-digest lookup skipping, ~3-4x faster shaping). Renderer core is unchanged; apps
that plug DIR.Lib.Shaping's ShapingTextShaper into renderer.TextShaper get the speedup.

## 6.15

Shaped-text GID-direct atlas fetch. DrawText/MeasureText now honor ShapedGlyph.Glyph (the
substituted glyph id from an ITextShaper -- GSUB ligatures, Arabic joined forms, ...),
fetching the SDF/bitmap atlas by glyph id instead of the source codepoint. Adds
VkFontAtlas/VkSdfFontAtlas.GetGlyphByGid + VkRenderer.PreWarmSdfGlyphByGid. Opt-in via
renderer.TextShaper (e.g. DIR.Lib.Shaping's ShapingTextShaper); the default AdvanceShaper
per-rune path is byte-identical to before. Bumps DIR.Lib pin 6.5 -> 6.6.

## 6.9

Inspector describe_layout MCP tool -- serializes the FULL arranged DIR.Lib.Layout tree (depth +
kind + rect + content/bg/hit chrome), not just the clickable subset describe_ui shows. Needs
DIR.Lib's ArrangedNode.Depth + PixelWidgetBase.GetCapturedLayout + LayoutInspection (DIR.Lib 6.0.x).

## 6.8

Inspector render-thread watchdog -- render_liveness MCP tool + ProbeRenderAsync (ALIVE/BLOCKED/DEAD
via a short-budget ping that round-trips ON the render thread; detects a wedged render loop).

## 6.7

Lockstep rebuild against DIR.Lib 6.0 (layout namespace + Layout.Builder DSL).

## 6.5

Live-device thumbnail capture (VulkanContext.ThumbnailCapture + VkRenderer
BeginThumbnailCapture/EndThumbnailCapture/TryGetThumbnailCapture) — re-issues already-
tessellated geometry into an offscreen target at thumbnail scale, non-blocking readback.
Plus SDF atlas per-page LRU eviction (replaces EvictAll thrash) + bounded disk-load drain.

## 6.4

Rebuilt against DIR.Lib 5.0 (layout engine + PixelMenuWidget); removes VkMenuWidget
(superseded by DIR.Lib's surface-agnostic PixelMenuWidget).

## 6.0

Multi-window — one VulkanDevice shared across windows (SdlVulkanApp); VulkanContext split into
device-level (VulkanDevice) + per-window state; multi-window SdlEventLoop. Breaking: standalone
consumers move to SdlVulkanApp. Adds opt-in SDF glyph disk cache + window placement/morph API.

## 5.1

VkRenderer overrides DIR.Lib's PushClip/PopClip → Vulkan scissor (needs DIR.Lib >= 4.4).

## 5.0 (breaking)

Multi-page SDF glyph atlas. The atlas is now a list of fixed-size
page textures (default 2048²); a full page appends a new page instead of reallocating, so
glyph-atlas growth no longer does a vkDeviceWaitIdle + image realloc + re-upload (the visible
frame stall). Internal change to VkSdfFontAtlas + the SDF draw path; the public VkRenderer API
is source-compatible (the optional sdfInitialAtlasDim param now sizes a page).
