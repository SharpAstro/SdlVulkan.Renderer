# Changelog

Release notes for SdlVulkan.Renderer, one entry per `Major.Minor`, newest first.

The version NUMBER is not here: it lives in `src/Directory.Build.props` (`VersionMajorMinor`), and the
build job reads that property back rather than restating it, so a package can never declare a version
this file disagrees with. Bump it there and add the entry here, in the same commit.

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
