#version 450
// MTSDF text pipeline: samples a multi-channel signed distance field atlas
// (RGBA8; RGB = per-channel pseudo-distance, A = true distance). The edge is
// reconstructed from median(r,g,b), which keeps sharp corners crisp where a
// single-channel SDF would round them off. Uses the same vertex layout as
// TexturedPipeline.
//
// Edge softness: the sdfEdge push constant carries the ANALYTIC half-width of each
// sample's smoothstep band in distance units -- a quarter of a screen pixel, computed per
// draw from the batch fontSize (see VkSdfFontAtlas.SampleHalfBand). The old
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
// reach cap of 3 texels this shader first had, shrank the footprint enough that a hyphen's
// ink still swung 0.75:1 with its sub-pixel phase (MtsdfTextRenderTests pins it).
//
// The offsets follow the pixel's footprint in texture space (dFdx/dFdy), so rotated text
// samples its own pixel. Their reach is capped at 4.5 texels so a sample never reads another
// glyph's ink: a cell's own ink sits at least 4 texels (the spread) inside it, and the next
// cell's ink starts 5 texels past its edge (a 1-texel gutter plus that cell's spread), so
// anything a sample reaches beyond its own cell is padding, which reads as outside, as the
// true field there would. The cap engages only below ~5 px/em.
layout(location = 0) in vec2 vTexCoord;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float sdfEdge; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
float coverage(vec2 uv, float w) {
    return smoothstep(0.5 - w, 0.5 + w, median(texture(uTexture, uv).rgb));
}
void main() {
    vec2 dx = dFdx(vTexCoord);
    vec2 dy = dFdy(vTexCoord);
    // Half-width of each sample's smoothstep band in distance units = a quarter of a screen
    // pixel, since each sample stands for a quarter of the pixel. Analytic per-draw value when
    // provided; fwidth fallback otherwise.
    float ws = pc.sdfEdge > 0.0
        ? pc.sdfEdge
        : fwidth(median(texture(uTexture, vTexCoord).rgb)) * 0.25 + 1e-4;
    // Texels per screen pixel, to cap the samples' reach inside the cell's padding.
    vec2 texSize = vec2(textureSize(uTexture, 0));
    float texelsPerPx = max(length(dx * texSize), length(dy * texSize));
    float reach = min(1.0, 4.5 / max(0.375 * texelsPerPx, 1e-4));
    dx *= reach;
    dy *= reach;
    float alpha = 0.25 * (
        coverage(vTexCoord - 0.125 * dx - 0.375 * dy, ws) +
        coverage(vTexCoord + 0.375 * dx - 0.125 * dy, ws) +
        coverage(vTexCoord + 0.125 * dx + 0.375 * dy, ws) +
        coverage(vTexCoord - 0.375 * dx + 0.125 * dy, ws));
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
