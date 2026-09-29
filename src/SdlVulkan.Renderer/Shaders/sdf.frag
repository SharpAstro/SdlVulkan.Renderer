#version 450
// MTSDF text pipeline: samples a multi-channel signed distance field atlas
// (RGBA8; RGB = per-channel pseudo-distance, A = true distance). The edge is
// reconstructed from median(r,g,b), which keeps sharp corners crisp where a
// single-channel SDF would round them off. Its vertex stage is sdf.vert: the
// textured layout plus the glyph's cell in the atlas page.
//
// This is the SMALL-text shader, below 64 px/em. Text at and above that size goes through
// sdflarge.frag, one sample a pixel, on a pipeline of its own (VkSdfFontAtlas.SingleSampleMinPx):
// there the thinnest stroke spans about two pixels and one sample is as good as four. The two
// are separate pipelines rather than one shader with a branch because the branch did not save
// the cost: a GPU sizes a shader for its most expensive path, and with this four-sample path
// compiled in, the one-sample branch still ran 9.5 ms a frame on a page of 600 dpi text that
// the old shader drew in 5.1.
//
// Edge softness: the sdfEdge push constant carries the ANALYTIC half-width of each
// sample's smoothstep band in distance units -- a quarter of a screen pixel, computed per
// draw from the batch fontSize (see VkSdfFontAtlas.SdfEdgeConstant). The old
// fwidth(dist)-based band is kept only as a fallback when the slot is 0 (a caller that
// never sets it).
//
// Why not fwidth: the reconstructed median(r,g,b) is piecewise-linear with derivative
// jumps along MSDF channel-switch boundaries (and along the generator's error-correction
// collapses). fwidth() spikes at those seams, ballooning the AA band exactly where the
// field value hovers near 0.5 -- which rendered as faint detached gray dashes hugging the
// shallow bottom curves of round glyphs (o/c/e/g/b, the "defective o" class). An analytic
// band is what the reference msdfgen shader uses (screenPxRange) and is immune to seams.
//
// The alpha channel (true distance) is available for outline / glow / weight
// effects; the base text pass reconstructs coverage from the RGB median only.
//
// Coverage is the mean of four samples on a rotated grid inside the pixel, each with a
// quarter-pixel band, not one sample at the centre with a half-pixel band. One sample
// measures how far the pixel CENTRE is from the nearest edge, which estimates coverage well
// at an edge and badly across a stroke thinner than a pixel: two neighbouring centres can
// both fall just outside it, and the stroke draws nearly white. At reading sizes that erased
// the hairline top of a Times 'a' (about 0.6 px at 29 px/em) and thinned n/e/o. Measured on
// real atlas cells against exact area coverage: mean error 0.032 -> 0.012, worst pixel
// 0.63 -> 0.28, and no stroke lost. The band is per SAMPLE and computed for it rather than
// half the per-pixel one, because that one is clamped at 0.25: at 7 px/em the clamp, and a
// cap on the samples' reach this shader first had, shrank the footprint enough that a
// hyphen's ink still swung 0.75:1 with its sub-pixel phase (MtsdfTextRenderTests pins it).
//
// The offsets follow the pixel's footprint in texture space (dFdx/dFdy), so rotated text
// samples its own pixel, and every sample is clamped into the glyph's own cell (vCell), half
// a texel in, so bilinear filtering never reaches a texel outside it. Outside a cell is not
// "outside the glyph": the atlas uploads only the rectangle its new cells span, and never
// clears a page, so the space around a cell holds whatever the page held before --
// uninitialised device memory on a new page, the glyphs a recycled page used to carry. On
// lavapipe, which reuses freed memory as it is, samples that left the cell picked up an
// earlier test's glyphs, and only in the order a CI run happened to take. Inside the cell
// the field is exact to its edge, where the spread padding has already fallen to "outside",
// so a clamped sample reads what an unclamped one should have.
layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in vec4 vCell;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float sdfEdge; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
void main() {
    vec2 dx = dFdx(vTexCoord);
    vec2 dy = dFdy(vTexCoord);
    // Half-width of each sample's smoothstep band in distance units = a quarter of a screen
    // pixel, since each sample stands for a quarter of the pixel. Analytic per-draw value when
    // provided; fwidth fallback otherwise.
    float ws = pc.sdfEdge > 0.0
        ? pc.sdfEdge
        : fwidth(median(texture(uTexture, vTexCoord).rgb)) * 0.25 + 1e-4;
    // The cell, pulled in by half a texel so a bilinear tap at its edge reads only its own texels.
    vec2 halfTexel = 0.5 / vec2(textureSize(uTexture, 0));
    vec2 lo = vCell.xy + halfTexel;
    vec2 hi = vCell.zw - halfTexel;
    float alpha = 0.0;
    alpha += smoothstep(0.5 - ws, 0.5 + ws,
        median(texture(uTexture, clamp(vTexCoord - 0.125 * dx - 0.375 * dy, lo, hi)).rgb));
    alpha += smoothstep(0.5 - ws, 0.5 + ws,
        median(texture(uTexture, clamp(vTexCoord + 0.375 * dx - 0.125 * dy, lo, hi)).rgb));
    alpha += smoothstep(0.5 - ws, 0.5 + ws,
        median(texture(uTexture, clamp(vTexCoord + 0.125 * dx + 0.375 * dy, lo, hi)).rgb));
    alpha += smoothstep(0.5 - ws, 0.5 + ws,
        median(texture(uTexture, clamp(vTexCoord - 0.375 * dx + 0.125 * dy, lo, hi)).rgb));
    alpha *= 0.25;
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
