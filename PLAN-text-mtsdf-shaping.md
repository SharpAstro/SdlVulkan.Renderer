# PLAN: MTSDF Text + Glyph-ID Shaping Seam

## Goal

Adopt two text-quality upgrades across the font stack (Fonts.Lib → DIR.Lib →
SdlVulkan.Renderer), inspired by [SUIsei](https://github.com/AtomicBlom/SUIsei)
(MIT), which prebakes MTSDF atlases keyed by glyph id behind a swappable
`ITextShaper` seam:

- **Track B — MTSDF glyphs.** Replace the single-channel R8 SDF atlas with
  multi-channel MTSDF (RGB = per-channel signed pseudo-distance, A = true
  signed distance). Sharp corners at any scale; the alpha channel enables
  outline/glow/weight effects as pure shader work later.
- **Track A — glyph-id keying + shaping seam.** Key glyph atlases by glyph id
  instead of codepoint, and introduce an `ITextShaper` seam so a HarfBuzz
  satellite package can provide ligatures/kerning/complex scripts, with an
  advance-based default shaper that reproduces today's output exactly.

Per the DIR.Lib policy in `CLAUDE.md`, the substantive pieces land **upstream**
(Fonts.Lib, DIR.Lib); this repo only re-keys its atlases, switches the page
format, and adjusts one shader.

## Non-Goals

- **GSUB harvesting / build-time atlas baking.** SUIsei needs a GSUB lookup
  walk to enumerate ligature glyphs *ahead of time* because it prebakes a
  fixed atlas. We rasterize at runtime: a shaper hands us glyph ids on demand
  and the atlas caches them. Nothing to harvest.
- **GSUB parsing in Fonts.Lib.** The shaping seam is satisfied by HarfBuzzSharp
  (satellite package); Fonts.Lib's `TODO.md` keeps shaping out of scope.
- **Single-page shelf packing / embedded static atlases** (prebake-world
  designs; our multi-page + LRU + disk-cache pipeline stays).
- **Replacing the bitmap/emoji atlas** (`VkFontAtlas`) — color glyphs stay
  bitmap; only the SDF path changes format.

---

## Where the stack stands today (surveyed 2026-07-05)

**Fonts.Lib is already glyph-id-keyed below the cmap lookup.** Codepoints only
enter at `OpenTypeFont.GetGlyphId(...)`; everything downstream is GID-keyed:
`DrawGlyph(uint gid, IGlyphSink)` (`OpenTypeFont.cs:382`), `RenderSdf(uint gid,
float ppem, float spread)` (`OpenTypeFont.cs:475`), `LoadGlyphOutline`,
`RenderGlyphHinted`, `RenderColor`, and — notably — `GetKerning(uint leftGid,
uint rightGid)` (`OpenTypeFont.cs:408`, GPOS pair-adjustment + legacy `kern`),
which **no production code calls today**.

The codepoint assumption is a thin, enumerable layer:

| Location | Construct |
|---|---|
| DIR.Lib `ManagedFontRasterizer.cs:37-65,530-554` | public API takes `Rune` (+ `charCode`/`GlyphMapHint`); a GID-direct `RasterizeGlyphByGid` (line 73) exists but is unused in production |
| DIR.Lib `RgbaImageRenderer.cs:18,302-347,443-450` | `(Font, Size, Rune)` glyph cache; per-rune `DrawText`/`MeasureText` loops, no kerning |
| DIR.Lib `FontFallbackResolver.cs:27` | codepoint→font cache |
| DIR.Lib `TextInputState.cs` / `TextInputRenderer.cs:82-107` | caret/selection as raw UTF-16 char indices; caret X via substring re-measure (assumes 1 char = 1 glyph) |
| this repo `VkFontAtlas.cs:12`, `VkSdfFontAtlas.cs:18` | `GlyphKey(string Font, float Size, Rune Character, int CharCode)` in both atlases |
| this repo `VkRenderer.cs:103-140` | per-rune `MeasureText` with codepoint-range emoji routing |

The `CharCode` + `GlyphMapHint` members of `GlyphKey` exist *because* the same
codepoint can map to different glyph indices in CID/subset fonts — i.e. the
current key already carries a workaround for the identity problem GID keying
solves natively.

**SDF generation lives in Fonts.Lib** (`Rasterizer/SdfRasterizer.cs:122`,
analytic SIMD winding+distance, 1 byte/pixel, 128 = edge, `spread` in pixels),
is wrapped by DIR.Lib's `ManagedFontRasterizer.RasterizeGlyphSdf`, and uploaded
as-is by `VkSdfFontAtlas` (R8_Unorm pages, `SdfRasterSize = 128`,
`SdfSpread = 4`). The fragment shader (`VkPipelineSet.cs:172-187`) samples `.r`
with `fwidth`-based smoothstep; the `sdfEdge` push-constant slot is dead but
retained for layout compatibility. The true signed distance `SdfRasterizer`
already computes **is** MTSDF's alpha channel — only the RGB pseudo-distance
machinery is new.

**Type1/PFB caveat:** Type1 fonts have no numeric GIDs — glyphs are addressed
by name via `Encoding`/`Differences` (`ManagedFontRasterizer.cs:627-648`).
GID keying needs either synthetic per-font ids assigned by Fonts.Lib or a
legacy name-keyed variant of the atlas key.

---

## Track B — MTSDF (first: contained, zero API breakage)

1. **Fonts.Lib: `RenderMtsdf(uint gid, float ppem, float spread)`** returning a
   4-channel bitmap, alongside `RenderSdf`. New machinery to add (adapting
   SUIsei's managed msdfgen port, ~1,250 LOC, MIT — itself a port of MIT
   msdfgen): `edgeColoringSimple`, the bisector-gated per-channel
   perpendicular-distance selector, the cubic solver, the winding-aware
   **overlapping-contour combiner** (matters for PDF subset fonts, which are
   full of overlapping contours), and the true-distance error-correction pass.
   Existing outline access (`DrawGlyph` → `IGlyphSink`), spread convention, and
   128 = edge normalization carry over. The SIMD fast path needs per-channel
   rework; scalar-first is acceptable (see costs below).
2. **DIR.Lib:** `SdfGlyphBitmap` grows a channel count (or a sibling
   `MtsdfGlyphBitmap`); `ManagedFontRasterizer.RasterizeGlyphMtsdf(...)`.
3. **This repo:**
   - `VkSdfFontAtlas` pages `R8_Unorm` → `R8G8B8A8_Unorm`
     (`VkSdfFontAtlas.cs:765`); the R-swizzle image view (line 793) goes away;
     staging/dirty-rect upload logic is unchanged apart from stride.
   - Shader: sample `median(r,g,b)` instead of `.r`
     (`VkPipelineSet.cs:179`); the `fwidth` smoothstep and push-constant
     layout are untouched.
   - `SdfGlyphDiskCache.FormatVersion` bump + channel-count field — the cache
     is designed for this (v2 precedent at `SdfGlyphDiskCache.cs:40`; stale
     files self-truncate and rebuild).
4. **Memory:** RGBA is 4× per texel (16 × 2048² pages: 64 → 256 MB worst
   case). MTSDF preserves corners at lower raster sizes, so dropping
   `SdfRasterSize` 128 → 64 offsets the growth entirely while improving corner
   fidelity. Decide with an A/B on glyph-heavy real documents (CJK, embedded
   subset fonts) before landing.
5. **Later, free wins:** outline/glow/variable-weight via the alpha (true
   distance) channel — shader-only changes, no atlas or upstream work.

**Costs:** first run after upgrade re-rasterizes every glyph (one-time cache
invalidation); rendered text pixels change (sharper corners) so any golden
screenshots churn once; MTSDF is ~3-4× the distance evaluations per glyph —
absorbed by the existing async off-render-thread rasterization + disk cache.

## Track A — GID keying + shaping seam (staged; every stage ships green)

- **A1 — GID-keyed atlases (this repo + DIR.Lib; no visual change).**
  `GlyphKey` becomes `(Font, Size, uint Gid)` in both atlases; codepoint→GID
  resolves once at the draw boundary (via `RasterizeGlyphByGid`). The
  `CharCode`/`GlyphMapHint` key workaround collapses away. Type1 needs the
  synthetic-GID decision (above). Disk-cache key change — **coordinate the
  `FormatVersion` bump with Track B** so users eat one invalidation, not two.
- **A2 — `ITextShaper` seam (DIR.Lib).** `ShapedGlyph(Gid, Cluster, XAdvance,
  XOffset, YOffset)` runs; default `AdvanceShaper` reproduces today's
  per-rune advance summation exactly (zero visual change).
  `Renderer<T>.DrawText`/`MeasureText` (`Renderer.cs:387`) become template
  methods: shape once, then draw per-GID; `VkRenderer.MeasureText` and
  `RgbaImageRenderer` lose their per-rune loops. Optional cheap win: the
  `AdvanceShaper` can apply Fonts.Lib's already-parsed-but-never-called
  `GetKerning` — kerned text before any HarfBuzz work (visual churn:
  opt-in flag).
- **A3 — HarfBuzz satellite (new package).** ~~`SharpAstro.Fonts.HarfBuzz`
  wrapping HarfBuzzSharp behind `ITextShaper` (~100 LOC adapter; SUIsei's is
  90). Separate package mirrors the WebView satellite pattern — core stays
  dependency-light. HarfBuzzSharp ships cross-platform natives and is
  P/Invoke-based (NativeAOT-fine). Opt-in per app/font.~~
  **Superseded (2026-07-06): A3 is now a pure-managed shaping engine of our
  own — see `PLAN-text-shaping-engine.md`.** The native wrapper reintroduced
  per-RID native assets (win-arm64 risk) into a deliberately native-free
  stack; HarfBuzzSharp survives only as a dev-only golden-fixture generator.
- **A4 — cluster-aware text input (the long tail).** `TextInputState` caret
  math must map through shaped-run cluster indices instead of substring
  re-measure once ligatures exist. Only misbehaves when a real shaper is
  installed — under `AdvanceShaper`, clusters are 1:1 and current behavior
  holds — so A4 trails A3, gated by keeping editable widgets on the default
  shaper until it lands.

## The compat contract (what downstream consumers actually see)

`VkFontAtlas`, `VkSdfFontAtlas` (and thus `GlyphKey` and `GlyphInfo`) are
`internal sealed` — the external glyph surface is exactly:

- Nine public `VkRenderer` methods carrying `(Rune character, int charCode,
  GlyphMapHint hint)`: `PreWarmGlyph`, `DrawSingleGlyph`,
  `DrawGlyphAtBaseline`, `AddBatchedGlyph`, `AddBatchedGlyphAtBaseline`,
  `AddBatchedSdfGlyph`, `AddBatchedSdfGlyphAtBaseline`, `PreWarmSdfGlyph`,
  `PreWarmSdfGlyphBatch` (`VkRenderer.cs:717-1165`), plus the identity-free
  batch brackets and `DrawText`/`MeasureText`.
- `SdfGlyphDiskCache` (fully public, including `AppendGlyph(s)` /
  `DiskGlyphEntry` carrying the `(CharCode, Rune, Hint)` trio) and the
  `VkRenderer` constructor's `sdfDiskCache`/`rasterizer` params.

**These signatures are the compat layer for A1**: they stay unchanged and
resolve `(Rune, charCode, hint)` → GID at entry. A useful side effect: disk
entries can then store the GID resolved at rasterization time, so bulk cache
loads no longer need font parsing to reconstruct identity.

## Downstream consumer: pdf-viewer (surveyed 2026-07-05)

Consumption is a **source-level submodule chain** — pdf-viewer →
`lib/SdlVulkanRenderer` (DrawboardLtd fork) → nested DIR.Lib fork submodule —
with plain `ProjectReference`s, no NuGet gate. The fork's text-pipeline files
are byte-identical to upstream at fork `origin/main` (renderer 6.12). Changes
land only when the submodule pin is bumped, so breaking stages are absorbed
one pin-bump at a time with compile errors surfacing at bump time.

Findings that shape the plan:

- **pdf-viewer never reaches atlas internals** — every PDF-content glyph goes
  through the compat surface above (`VectorPageRenderer.cs:1272-1326`,
  `PreWarmSdfGlyphBatch` dedup key is literally the `(Font, Rune, CharCode,
  Hint)` 4-tuple). No custom pipelines sample the glyph atlas. A pure
  signature-compatible refactor builds with **zero pdf-viewer changes**.
- **`SdfTextThreshold = 0`** (`VectorPageRenderer.cs:1393`): 100 % of PDF text
  renders through the SDF atlas — Track B changes every text pixel in the
  shipped viewer, and all RMS golden baselines (`test-baselines.json`, via
  `PageVulkanRender`) need one `UPDATE_BASELINES=1` regeneration.
- **One untyped sync point:** `Program.cs:78` hard-codes
  `new SdfGlyphDiskCache(..., rasterSize: 128f, spread: 4f)` which must match
  the atlas constants — if Track B changes raster size, this must move in
  lockstep (mismatch is only silently invalidated, not an error).
- **Three charCode→glyph resolution strategies coexist** and the A1 boundary
  resolver must reproduce all three: CID-direct via `/CIDToGIDMap`
  (`ContentStreamParser.cs:1420-1427` — the PDF.Lib backend *already passes
  resolved GIDs* as `charCode` with `CharCodeIsGID`), TrueType-subset cmap
  search with a deliberate SPUA codepoint trick for symbolic fonts
  (`VectorPageRenderer.cs:1301-1312`, forces Unicode-lookup misses), and
  Type1 encoding-vector/glyph-name lookup. Additionally,
  `TextGlyphCommand.CharCode`/`IsCidFont` carry **different semantics per
  backend** (PDF.Lib: resolved GID + treat-as-GID flag; SDK: raw charCode +
  actual font type) — do not conflate them at the new boundary.
- **Precedent regression:** the fork's `test-baselines.json:2` documents that
  a 3.4→3.5 renderer+Fonts.Lib bump broke CID Identity-H glyph routing on
  customer CAD PDFs — and RMS diffing is known to under-detect wrong-glyph
  bugs (`Cjk0522GlyphSelection.cs`). Gate any A-stage bump on:
  `Cjk0522GlyphSelection`, `PageVulkanRender` (both backends, all dpis), and
  `PerFontGlyphCompare`.
- **No text selection/caret code exists in pdf-viewer** (no `TextInputState`
  usage; search is a page-label index, not content text) — A4's cluster work
  does not affect this consumer at all.

## Breakage assessment

| Blast radius | Verdict |
|---|---|
| Public draw APIs (`DrawText`/`MeasureText`, `VkRenderer` glyph methods) | Signatures unchanged (they are the compat layer); internals re-plumbed |
| pdf-viewer source | Zero changes to compile; `Program.cs:78` raster-size/spread sync if B changes them |
| Disk caches (`.sdfg`) | One coordinated format bump; self-healing by design |
| Rendered pixels | Change once (MTSDF corners; kerning only if opted in) — pdf-viewer regenerates all RMS baselines once |
| Glyph identity (CID/symbolic/Type1 PDFs) | The real Track A risk — semantic, not compile-time; gated by the fork's glyph-selection tests |
| Text-input carets | Only under a HarfBuzz shaper with ligature clusters — gated behind A4; no pdf-viewer exposure |
| Type1/PFB fonts | The one genuine design wart; synthetic-GID vs legacy-key decision needed |
| Emoji / bitmap atlas path | Untouched (effectively dead for PDF content anyway at `SdfTextThreshold = 0`) |

## Sequencing & publish chain

**B → A1 (shared cache bump) → A2 → A3 → A4.** Track B alone delivers the
visible quality win. Each stage rolls through the usual three-step NuGet
chain (Fonts.Lib → DIR.Lib → this repo, per `CLAUDE.md`); since every stage is
non-breaking, versions roll independently — no lockstep release needed.

## Open questions

- MTSDF raster size after the A/B (64 vs 96 vs keep 128 and eat memory)?
- Type1: synthetic GIDs in Fonts.Lib vs a name-keyed legacy atlas key?
- Does `AdvanceShaper` apply GPOS/kern by default or behind a flag (visual
  churn on existing documents)?
- Vendor SUIsei's generator sources into Fonts.Lib (with MIT attribution) or
  have the author publish `SUIsei.Text` as a NuGet package we reference?
