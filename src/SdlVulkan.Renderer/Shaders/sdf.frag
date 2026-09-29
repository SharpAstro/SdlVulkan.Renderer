#version 450
// MTSDF text pipeline: samples a multi-channel signed distance field atlas
// (RGBA8; RGB = per-channel pseudo-distance, A = true distance). The edge is
// reconstructed from median(r,g,b), which keeps sharp corners crisp where a
// single-channel SDF would round them off. Its vertex stage is sdf.vert: the
// textured layout plus the glyph's cell in the atlas page.
//
// This is the SMALL-text shader, below 64 px/em. Text at and above that size goes through
// sdflarge.frag, one sample a pixel, on a pipeline of its own (VkSdfFontAtlas.SingleSampleMinPx):
// there the thinnest stroke spans about two pixels and one sample is as good as two.
//
// The sdfEdge push constant is ONE SCREEN PIXEL in distance-field units, computed per draw from
// the batch fontSize (VkSdfFontAtlas.FieldUnitsPerPixel); the band and the edge shift below are
// fractions of it. A caller that never sets it (0) falls back to fwidth of the field, which
// estimates the same thing.
//
// Why not fwidth normally: the reconstructed median(r,g,b) is piecewise-linear with derivative
// jumps along MSDF channel-switch boundaries (and along the generator's error-correction
// collapses). fwidth() spikes at those seams, ballooning the AA band exactly where the
// field value hovers near 0.5 -- which rendered as faint detached gray dashes hugging the
// shallow bottom curves of round glyphs (o/c/e/g/b, the "defective o" class). An analytic
// band is what the reference msdfgen shader uses (screenPxRange) and is immune to seams.
//
// The alpha channel (true distance) is available for outline / glow / weight
// effects; the base text pass reconstructs coverage from the RGB median only.
//
// TWO SAMPLES A PIXEL, not one at its centre. One sample measures how far the pixel CENTRE is
// from the nearest edge, which estimates coverage well at an edge and badly across a stroke
// thinner than a pixel: two neighbouring centres can both fall just outside it, and the stroke
// draws nearly white. At reading size (29 px/em) that erased the hairline top of a Times 'a'
// and thinned n/e/o. The two samples sit a quarter pixel either side of the centre on the
// diagonal, so a horizontal or vertical stroke is always straddled, each blended over a
// sqrt(2)-quarter-pixel band (half the pixel's area apiece). Modelled on an ideal stroke, the
// ink stays within 1-3% across sub-pixel phases, where one sample swings 30-50%; four samples on
// a rotated grid were no steadier and cost half as much again (a page of 212 dpi body text:
// 0.82 ms of GPU with one sample, 1.21 with two, 1.46 with four, on an Adreno X1-85). A stroke
// running exactly along the pairs' own diagonal sees them as one sample; italic stems lean the
// other way.
//
// EDGE SHIFTED OUT BY 0.1 px. Exact area coverage drew text about 11% lighter than pdfium (and
// so than DB PDF and every viewer built on it), whose small text is heavier than its outlines:
// it renders glyphs through FreeType's LCD filter, averages the subpixels back to grey and
// applies a text-gamma table. Measured on an arXiv paper in Times at 150-300 dpi, a 0.1 px shift
// brings the ink to within 3% of pdfium's and the hairlines to about 0.8 px at 29 px/em; the
// gamma table alone got only a third of the way, and pdfium's snapping of glyph origins to whole
// pixels is not copied (it would make text step against scrolling geometry). It is a constant
// offset on the threshold, so it costs nothing.
//
// Every sample is clamped into the glyph's own cell (vCell), half a texel in, so bilinear
// filtering never reaches a texel outside it. Outside a cell is not "outside the glyph": the
// atlas uploads only the rectangle its new cells span, and never clears a page, so the space
// around a cell holds whatever the page held before -- uninitialised device memory on a new
// page, the glyphs a recycled page used to carry. On lavapipe, which reuses freed memory as it
// is, samples that left the cell picked up an earlier test's glyphs, and only in the order a CI
// run happened to take. Inside the cell the field is exact to its edge, where the spread padding
// has already fallen to "outside", so a clamped sample reads what an unclamped one should have.
layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in vec4 vCell;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float sdfEdge; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
void main() {
    vec2 dx = dFdx(vTexCoord);
    vec2 dy = dFdy(vTexCoord);
    // One screen pixel in field units. The field goes flat 4 texels outside an edge (at 0), so the
    // band must not reach below it: past that point every sample reads the same 0, the band is cut
    // on one side, and a thin stroke's ink comes to depend on its sub-pixel phase again (at 7 px/em
    // a hyphen swung 0.83:1 before this cap). The cap binds only below about 8 px/em.
    float px = pc.sdfEdge > 0.0 ? pc.sdfEdge : fwidth(median(texture(uTexture, vTexCoord).rgb)) + 1e-4;
    float t = max(0.5 - 0.1 * px, 0.05);
    float ws = min(0.3536 * px, t - 0.01);
    // The cell, pulled in by half a texel so a bilinear tap at its edge reads only its own texels.
    vec2 halfTexel = 0.5 / vec2(textureSize(uTexture, 0));
    vec2 lo = vCell.xy + halfTexel;
    vec2 hi = vCell.zw - halfTexel;
    float alpha = 0.5 * (
        smoothstep(t - ws, t + ws, median(texture(uTexture, clamp(vTexCoord - 0.25 * (dx + dy), lo, hi)).rgb)) +
        smoothstep(t - ws, t + ws, median(texture(uTexture, clamp(vTexCoord + 0.25 * (dx + dy), lo, hi)).rgb)));
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
