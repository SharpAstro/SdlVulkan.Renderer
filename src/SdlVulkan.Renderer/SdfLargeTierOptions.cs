namespace SdlVulkan.Renderer;

using DIR.Lib;

/// <summary>
/// Configuration for the optional large-text SDF tier — a second glyph atlas at a bigger raster
/// for text drawn large on screen, so it stops magnifying the base (64px) field. Passing this to
/// <see cref="VkRenderer"/> enables the tier; null (the default) keeps the renderer single-tier.
/// </summary>
/// <param name="RasterSize">Raster size (px) the tier bakes glyphs at, e.g. 128.</param>
/// <param name="DiskCache">Disk cache for the tier's glyphs — must be keyed at <paramref name="RasterSize"/>.
/// Null just means re-raster per session.</param>
/// <param name="MinOnScreenPx">Tier-select threshold (device px): SDF batches whose on-screen
/// fontSize exceeds this draw from the tier. 0 → the base atlas's raster size (i.e. anything that
/// would magnify the base field). Hosts showing dense documents should pass a HIGHER value so
/// body-text-at-zoom stays base-tier (see the viewer's LargeTierMinOnScreenPx rationale).</param>
/// <param name="MaxPages">Hard page cap (power of two; each page is 4MiB at the default 1024²).
/// At the cap the tier REFUSES further inserts — glyphs draw from the base atlas via the fallback
/// pass — rather than evict-all thrashing on working sets larger than the cap.</param>
public sealed record SdfLargeTierOptions(
    float RasterSize,
    SdfGlyphDiskCache? DiskCache = null,
    float MinOnScreenPx = 0f,
    int MaxPages = 4);
