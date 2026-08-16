# Changelog

Release notes for SdlVulkan.Renderer, one entry per `Major.Minor`, newest first.

The version NUMBER is not here: it lives in `src/Directory.Build.props` (`VersionMajorMinor`), and the
build job reads that property back rather than restating it, so a package can never declare a version
this file disagrees with. Bump it there and add the entry here, in the same commit.

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
