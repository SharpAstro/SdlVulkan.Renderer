# PLAN: Pure-Managed Shaping Engine (A3, revised)

## Goal

Replace the planned HarfBuzzSharp satellite (`PLAN-text-mtsdf-shaping.md`, Track
A3) with **our own pure-managed OpenType shaping engine** in the Fonts.Lib repo:
**`src/SharpAstro.Fonts.Shaping`** (name decided 2026-07-06), plugging
into the `ITextShaper` seam that shipped in DIR.Lib 6.5 (A2). .NET 10,
`IsAotCompatible`, zero native dependencies.

Why managed instead of wrapping native HarfBuzz:

- **The stack's identity is native-free.** `ManagedFontRasterizer` exists
  specifically to replace FreeType ("no native dependencies, no GC pinning,
  AOT-compatible"). A native shaping binary reintroduces exactly what was
  engineered out — per-RID native assets, win-arm64 coverage risk, publish
  weight.
- **We only need a subset.** HarfBuzz's ~250k LOC is dominated by generated
  Unicode data and complex-script engines (Indic/USE syllable machines). The
  scripts our products actually render in UI text (Latin/Greek/Cyrillic/CJK +
  Arabic/Hebrew) need the OT-layout core, a default shaper, and the Arabic
  joining shaper — a bounded, well-specified slice.
- **PDF page content never shapes** (pre-positioned glyphs; `SdfTextThreshold`
  path draws by charcode/GID). Shaping serves *UI text* — labels, file names,
  inputs — so scope follows UI needs, not full typographic parity.

## Non-Goals

- **Indic / SEA / Universal Shaping Engine.** Syllable clustering + reordering
  is where the multi-year dragon lives. Deferred indefinitely; re-evaluate
  against real product demand. Without it, Devanagari/Thai/Khmer render
  per-cmap (today's behavior) — wrong, but no worse than now.
- **Bidi inside the shaper core (UAX #9).** HarfBuzz itself doesn't do bidi —
  callers pass pre-segmented single-direction runs, and so does our shaper
  core. Bidi *is* planned, but as a separate engine-package utility feeding
  the adapter (stage H6, see "Run itemization"), never as shaper-internal
  magic.
- **Vertical text** (`vhea`/`vmtx` exist in Fonts.Lib for metrics, but no
  vertical shaping features).
- **Variation-aware GPOS deltas** (ItemVariationStore-adjusted positioning).
  Fonts.Lib already parses HVAR/MVAR for metrics, so the hook exists; the
  engine reads default-instance values first. Follow-up if variable UI fonts
  materialize.
- **AAT (`morx`), Graphite, `JSTF`, optical bounds, line breaking.**
- **Replacing `AdvanceShaper`.** It stays the default `ITextShaper` — the
  engine is opt-in per renderer/app, exactly like the A2 kerning flag.

## Naming (decided: `SharpAstro.Fonts.Shaping`)

`src/SharpAstro.Fonts.HarfBuzz` was the A3 name when the package *wrapped*
HarfBuzzSharp. For our own engine that name would be misleading (it's another
project's name; implies binding parity we don't promise) — and it burns the
natural name for a *real* HB wrapper, which we may still want someday as an
A/B reference implementation behind the same `ITextShaper`. Decided:

- **`SharpAstro.Fonts.Shaping`** — the engine (this plan).
- `SharpAstro.Fonts.HarfBuzz` — reserved, if ever, for an actual
  HarfBuzzSharp-backed shaper used to cross-validate ours.

## Where the stack stands (surveyed 2026-07-06)

- **A2 shipped the seam.** `ITextShaper.Shape(text, fontPath, fontSize,
  rasterizer, List<ShapedGlyph>)` with `ShapedGlyph(Source, Glyph?, Cluster,
  XAdvanceAdjust, XOffset, YOffset)` (DIR.Lib 6.5, `TextShaping.cs`). Contract:
  base advance/bearings come from the *renderer's* glyph cache; the shaper
  contributes **adjustments** (px) on top. `Glyph != null` means "substituted —
  key the atlas by this identity, not by `Source`".
- **A1 made atlases identity-keyed** (GID / Type1 name) — but their public
  `GetGlyph` entry points still take a `Rune` and resolve internally. A
  substituting shaper needs a **GID-direct fetch** (small renderer-repo
  change; the keys already support it).
- **GPOS in Fonts.Lib is a kerning-only slice**
  (`Tables/Gpos/GposTable.cs`): LookupType 2 only, ScriptList/FeatureList
  deliberately unparsed, `lookupFlag` ignored, **no Extension (type 9)
  unwrapping** — fonts that wrap PairPos in Extension lookups silently get no
  kerning today. Coverage + ClassDef parsing exist (both formats) and the
  `BigEndianReader`/`Tag` IO layer is solid. GSUB and GDEF are entirely absent
  (`TODO.md:71` explicitly scoped shaping out — this plan supersedes that).
- **Raw table access is internal.** `OpenTypeFont` retains the SFNT
  `ReadOnlyMemory<byte>` per table but exposes no public
  `TryGetTable(Tag, out ReadOnlyMemory<byte>)`. The engine package needs one
  (tiny, generally useful core addition).
- **`Rune.GetUnicodeCategory` is built into CoreLib** (AOT-safe, no ICU) —
  general category needs no table of ours. `string.Normalize` is **not**
  reliable under `InvariantGlobalization` — do not depend on it (see Unicode
  data).
- Test infra: xunit, checked-in subset fonts (`DejaVuSans.ttf` has GPOS kern +
  marks; `*_subset.ttf` fixtures precedent), and the A2 `TextShaperTests`
  pattern to extend.

## Architecture

```
Fonts.Lib repo
├── src/SharpAstro.Fonts                  (core — unchanged scope: "FreeType")
│   └── + TryGetTable(Tag) raw accessor   (only core change)
├── src/SharpAstro.Fonts.Shaping          (NEW — the engine, "HarfBuzz" scope)
│   ├── Otl/       GSUB+GPOS+GDEF parsing & lookup execution (shared core)
│   ├── Ucd/       generated RVA property tables + accessors
│   ├── Shapers/   DefaultShaper, ArabicShaper
│   └── ShapeBuffer / ShapePlan / Shaper (public API)
└── tools/UcdGen                          (offline generator, not shipped)
    tools/HbFixtureGen                    (dev-only, HarfBuzzSharp-based)

DIR.Lib repo
└── src/DIR.Lib.Shaping                   (NEW satellite — ITextShaper adapter)
        references DIR.Lib + SharpAstro.Fonts.Shaping

SdlVulkan.Renderer repo
└── GID-direct atlas fetch + draw-by-identity in the DrawText primitives
```

Layering rules that force this shape:

- `ITextShaper` lives in DIR.Lib (downstream of Fonts.Lib), so the engine
  package **cannot** implement it without inverting the dependency — the
  adapter must live DIR.Lib-side (satellite, mirroring the WebView pattern).
- The engine consumes `OpenTypeFont` directly (it needs GSUB/GPOS/GDEF bytes),
  not `ManagedFontRasterizer`. The adapter bridges: it needs the *same*
  `OpenTypeFont` instance the rasterizer loaded (memory-font registrations!).
  Either `ManagedFontRasterizer` gains a public
  `TryGetOpenTypeFont(fontPath)`, or the adapter keeps its own engine-side
  font handle keyed by the same font id. Decide at H5; the accessor is
  simpler and honest.
- **All OTL parsing + execution lives in the engine package**, not core
  (option (a)). Core keeps its kerning-only `GposTable` untouched (it serves
  `GetKerningPx`/`AdvanceShaper`); the engine parses its own view of
  GPOS — including Extension unwrapping, fixing that gap in the shaped path.
  No IVT, no public parse-model in core.

### The A2 integration contract (key design decision)

HarfBuzz's output `x_advance` *includes* the base advance. Ours deliberately
does not: the engine reports per glyph

```
(gid, cluster, xAdvanceDeltaFU, xOffsetFU, yOffsetFU)   — FUnits
```

where `xAdvanceDeltaFU` is **only** the GPOS/kern adjustment relative to the
glyph's `hmtx` advance. The adapter scales deltas to px
(`fu * fontSize / UnitsPerEm`) and emits `ShapedGlyph` with `Glyph` set to the
(possibly substituted) identity. The renderer keeps sourcing the **base**
advance from its own cache — for substituted glyphs, the cache advance *of the
substituted GID* via the new GID-direct fetch. This preserves A2's invariants:
MeasureText ≡ DrawText per construction, renderer-specific raster/rounding
chains stay authoritative, and `AdvanceShaper` output remains the exact no-op
baseline. Cluster semantics stay UTF-16 offsets; a ligature merges clusters to
the min (HB cluster-level-0 behavior) — exactly what A4's caret work expects.

## Engine design

- **`ShapeBuffer`** — reusable, allocation-free steady state. Parallel arrays
  (`uint[] Gids`, `int[] Clusters`, `int[] AdvDelta/XOff/YOff`, `ushort[]
  Masks`, `byte[] GlyphClasses`), grown geometrically, pooled by the caller.
  In-place GSUB editing with a single scratch array (HB's approach).
- **`ShapePlan`** — resolved once per (font, script, direction, feature set)
  and cached (`ConcurrentDictionary`, like `_fonts`): ScriptList/FeatureList →
  applicable lookups in order, with per-feature masks. Shaping then = walk
  lookups, apply to masked glyphs.
- **OTL core (shared GSUB/GPOS)** — Coverage, ClassDef (lift the proven
  patterns from core's GposTable), GDEF glyph classes + mark-attachment
  classes + mark-filtering sets, `lookupFlag` skipping, Extension unwrapping,
  and the contextual matching engine (context / chained-context, formats 1-3)
  shared by GSUB 5/6 and GPOS 7/8.
- **GSUB** types 1 (single), 2 (multiple), 3 (alternate), 4 (ligature),
  5/6 (contextual), 7 (extension), 8 (reverse chained).
- **GPOS** types 1 (single), 2 (pair — full ValueRecords this time, both
  sides), 3 (cursive), 4 (mark-to-base), 5 (mark-to-ligature),
  6 (mark-to-mark), 7/8 (contextual), 9 (extension).
- **Shapers.** `DefaultShaper` (Latin/Greek/Cyrillic/CJK/Hebrew): default
  feature set `ccmp, liga, clig, calt, rlig` + `kern, mark, mkmk`; direction
  reverse for RTL + `Bidi_Mirroring_Glyph` remap. `ArabicShaper`: joining-type
  analysis (from UCD `ArabicShaping.txt` data) assigns `isol/init/medi/fina`
  masks, then the standard feature ordering (`ccmp, isol/fina/medi/init, rlig,
  calt, liga` + GPOS). No normalization pass in v1 — input is assumed NFC
  (true of our UI strings; `ccmp` handles font-level composition), documented
  limitation.

## Run itemization (iterated 2026-07-06)

`ITextShaper.Shape` receives a whole line with one font; someone must split it
into shapeable runs. Itemization bundles two axes — **script** (which shaper,
which OT script tag) and **direction** (LTR/RTL) — and direction is where bidi
hides. Staged design:

**Placement: the engine package, not the adapter.** The itemizer needs the
Script property table and nothing from DIR.Lib — it's pure Unicode, font-free.
Ships as a public utility (`ScriptItemizer`, later `BidiAlgorithm`) in
`SharpAstro.Fonts.Shaping`; the `DIR.Lib.Shaping` adapter just calls it.
Directly testable in engine tests.

**H5 — script itemization + script-implied direction (level B).**
Per-codepoint Script property; Common (punctuation/digits/spaces) and
Inherited (combining marks) attach to the *preceding* real-script run
(leading Common attaches forward); runs get direction from a fixed RTL-script
list (Arab, Hebr, Syrc, Thaa, Nkoo, …) and are emitted in **logical order**,
each internally reversed when RTL. This fully covers pure-RTL labels and the
common `ملف.pdf`-style mixed filename in an LTR UI. Known, documented
failures — exactly the UAX#9 rule set: digits adjacent to RTL (`فصل 12`),
punctuation clusters at run boundaries, multiple RTL runs split by neutrals,
and RTL paragraph contexts (Arabic-first UI). Script_Extensions and
bracket-pair script matching: skipped in v1 (noted; Indic is fenced out
anyway).

**H6 — UAX#9 implicit bidi (level C, separate ship).** `BidiAlgorithm` in the
engine package implementing the implicit rules (P2-P3, W1-W7, N0-N2, I1-I2,
L1-L2 reordering) over a `Bidi_Class` table + `BidiBrackets.txt` pairs (both
via UcdGen). The adapter gains a paragraph-direction option (default LTR) and
emits runs in **visual order** — DrawText's pen loop then just works, and
MeasureText is order-independent. Unicode ships a complete conformance corpus
(`BidiTest.txt` + `BidiCharacterTest.txt`, thousands of cases) — checked-in
fixtures in exactly the hb-shape harness style, for free. **A4 dependency:**
once runs reorder, caret math must map visual↔logical — editable widgets stay
on `AdvanceShaper` until A4, per the A2 gating, so nothing regresses
meanwhile.

## Unicode data: RVA tables + offline generator (the source-gen question)

**Storage: RVA blobs.** Every table is emitted as
`static ReadOnlySpan<byte> Foo => new byte[] { … };` — C# compiles this to a
data segment in the PE (no allocation, no static ctor, shared pages,
AOT-perfect; the same pattern CoreLib uses for its own character tables).
Multi-byte values are little-endian via `BinaryPrimitives` (all our RIDs are
LE).

**Encoding: per-property, generator-chosen.** The generator measures both a
two-stage trie (dense; fast O(1)) and a sorted-range table (sparse; binary
search) per property and emits the smaller unless the property is on the
per-glyph hot path (then trie). Expected budget — tiny:

| Property | Source | Needed by | Est. size |
|---|---|---|---|
| Joining_Type/Group | ArabicShaping.txt | ArabicShaper | ~4-8 KB |
| Canonical_Combining_Class | UnicodeData.txt | mark fallback ordering | ~5-10 KB |
| Bidi_Mirroring_Glyph | BidiMirroring.txt | RTL mirroring | ~2 KB |
| Script (+extensions later) | Scripts.txt | run itemization (H5) | ~15-25 KB |
| Bidi_Class | UnicodeData.txt | UAX#9 (H6) | ~10-15 KB |
| Bidi_Paired_Bracket | BidiBrackets.txt | UAX#9 N0 (H6) | ~1 KB |
| General_Category | — | (CoreLib `Rune.GetUnicodeCategory`) | 0 |

**Generation: offline tool, checked-in output — not a Roslyn source
generator.** `tools/UcdGen` reads a checked-in UCD snapshot — **pinned to
UCD 17.0.0** (latest stable; final files dated 2025-08-15), vendored under
`data/ucd/17.0.0/*.txt` with the Unicode license note — emits `Ucd/*.g.cs`
with a provenance header (UCD version + tool hash); regeneration is a script;
generated files are committed and reviewed like any code. Rationale: UCD churn is ~annual, so
paying source-generator complexity (AdditionalFiles plumbing, per-build parse
cost, painful debugging, cache-invalidation subtleties — see SUIsei.SourceGen's
own equatable-wrapper workarounds) buys nothing; offline-gen + checked-in is
what CoreLib, ICU, and HarfBuzz themselves do. The tool's emit core is written
as a library so it *could* be lifted into an incremental generator later if
UCD data ever becomes build-time-configurable.

**Seed from `sebgod/ucd-lib`** (2019, netcoreapp2.1): transplant + modernize
its UCD line parser (comment/range handling), the PropertyValueAliases
long-name↔short-tag mapping (short tags align with OT script tags), and the
cached `FileLoader` (for the snapshot-refresh script). Its `RangeIndexNode`
(in-memory pointer tree) and the `ucdxml` route don't fit the flat-RVA
emitter, which is new work either way.

## Testing

- **Golden fixtures generated by real HarfBuzz — as a dev-only tool.**
  `tools/HbFixtureGen` references HarfBuzzSharp (the native binding we chose
  *not* to ship) and dumps `(font, text, features, direction)` →
  `gid:cluster:xAdv:xOff:yOff` JSON. Fixtures are checked in; CI never touches
  native HB. Every engine stage must match HB on its supported feature slice
  (glyphs/clusters exactly; positions exactly in FUnits, since both read the
  same tables). This is the credibility backbone — and keeps the door open to
  A/B-ing the engine against a real HB wrapper behind `ITextShaper`.
- **Test fonts:** existing DejaVuSans (liga fi/fl, GPOS kern + marks) +
  add OFL subsets: Noto Naskh Arabic (joining + rlig + marks), a
  mark-stacking case (Noto Sans with combining diacritics), a
  contextual-alternates font, and one font known to wrap kerning in Extension
  lookups (regression for the core gap).
- **Property tests:** cluster monotonicity (LTR nondecreasing / RTL
  nonincreasing), cluster values are valid UTF-16 boundaries, advance-delta
  sum equals HB totals, buffer reuse leaves no stale state, and
  `DefaultShaper` with **all features disabled** ≡ `AdvanceShaper` glyph/
  advance stream (the A2 no-op baseline, now provable end-to-end).
- **DIR.Lib/renderer side:** extend `TextShaperTests` with the adapter;
  offscreen Vulkan test drawing "ffi waffle" with the engine vs glyph-level
  expectations (proves GID-direct atlas path).

## Staging (each ships green; usual three-repo publish chain)

| Stage | Content | Est. new LOC |
|---|---|---|
| **H0** | Core: public `TryGetTable`; engine skeleton: ShapeBuffer/ShapePlan, Script/Feature/Lookup resolution, Coverage/ClassDef/GDEF, lookup walk w/ flags + Extension; `tools/HbFixtureGen` + harness | ~1,200 |
| **H1** | GSUB 1/4 + GPOS 2 (full pair) + GPOS 1; DefaultShaper LTR; **ships: real fi/fl ligatures + kerning at parity with hb** | ~900 |
| **H2** | GPOS 4/5/6 (marks) + GSUB 2/3; CCC fallback ordering; **ships: combining diacritics position correctly** | ~800 |
| **H3** | Contextual/chained (GSUB 5/6/8, GPOS 7/8) + GPOS 3 (cursive); **ships: calt/contextual fonts correct** | ~1,000 |
| **H4** | `UcdGen` + joining/mirroring tables; ArabicShaper; RTL in DefaultShaper (Hebrew rides this); **ships: Arabic joining + Hebrew RTL** | ~800 + tool ~500 |
| **H5** | Engine `ScriptItemizer` (level B, logical order) + `DIR.Lib.Shaping` adapter (itemize → per-run shape, px mapping per the A2 contract) + renderer GID-direct atlas fetch/draw; opt-in wiring (`renderer.TextShaper = …`) | ~850 across repos |
| **H6** | Engine `BidiAlgorithm` (UAX#9 implicit, Bidi_Class + brackets tables); adapter paragraph-direction option + visual-order runs; BidiTest/BidiCharacterTest conformance fixtures; **ships: digits/punctuation/RTL-paragraph cases correct** | ~900 |

Total ≈ 6-7k hand-written LOC + generated data (H0-H5 ≈ 5-6k; H6 +~900). For
scale: the MTSDF port was ~1.25k; the existing `Tables/` tree is of the same
order. This is a multi-week project, not the multi-year one — *because*
USE/Indic stays fenced out and bidi is a bounded, conformance-tested UAX#9
implementation rather than shaper-internal complexity.

## Risks & honest costs

- **Correctness surface.** OT contextual lookups are the classic bug farm;
  the hb-fixture harness is non-negotiable and must run per-PR.
- **The engine will lag HB** on exotic fonts (broken tables HB tolerates,
  AAT-only fonts, obscure features). Mitigation: fall back to `AdvanceShaper`
  per font on any parse failure — never worse than today.
- **Cluster-level divergence from HB** (we fix level 0) is fine for A4 but
  must be documented for anyone comparing dumps.
- **`SharpAstro.Fonts.Shaping` won't make Arabic *appear* in the renderer by
  itself** — fallback font selection for Arabic glyphs (FontFallbackResolver)
  and A4 caret work are separate, existing tracks.
- **Perf target:** warm shape of a typical UI label ≤ ~2-5 µs (plan cached,
  buffer pooled); `AdvanceShaper` remains the zero-cost default so nothing
  regresses when the engine isn't installed.

## Decided

- **Name: `SharpAstro.Fonts.Shaping`** (2026-07-06). `SharpAstro.Fonts.HarfBuzz`
  stays reserved for a possible real-HB cross-validation wrapper.
- **UCD pin: 17.0.0** (2026-07-06) — latest stable, final files 2025-08-15.
- **Adapter home: `DIR.Lib.Shaping` satellite** (2026-07-06) — new package in
  the DIR.Lib repo referencing DIR.Lib + the engine; DIR.Lib core stays
  dependency-light, mirroring the WebView satellite pattern.
- **Fix core's `GetKerning` Extension-lookup gap in H1** (2026-07-06) — small
  independent patch to core's `GposTable` (unwrap LookupType 9 around
  PairAdjustment) riding the H1 PR, with an Extension-wrapped test font. Fixes
  silent no-kerning for such fonts on the `AdvanceShaper(applyKerning)` path
  too, not just the engine path.
- **Itemization: staged, engine-side** (2026-07-06) — H5 ships a script
  itemizer with script-implied direction (logical run order); H6 ships UAX#9
  implicit bidi with visual-order runs + the Unicode conformance corpus. See
  "Run itemization".

## Open questions

*(none — all resolved; totals: ≈ 6-7k hand-written LOC across H0-H6.)*
