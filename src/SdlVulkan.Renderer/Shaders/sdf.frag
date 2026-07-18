#version 450
// MTSDF text pipeline: samples a multi-channel signed distance field atlas
// (RGBA8; RGB = per-channel pseudo-distance, A = true distance). The edge is
// reconstructed from median(r,g,b), which keeps sharp corners crisp where a
// single-channel SDF would round them off. Uses the same vertex layout as
// TexturedPipeline.
//
// Edge softness: the sdfEdge push constant carries the ANALYTIC half-width of the
// smoothstep band in distance units -- half a screen pixel, computed per draw from the
// batch fontSize (see VkSdfFontAtlas.ScreenPxHalfBand). The old fwidth(dist)-based band
// is kept only as a fallback when the slot is 0 (a caller that never sets it).
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
layout(location = 0) in vec2 vTexCoord;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float sdfEdge; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
void main() {
    float dist = median(texture(uTexture, vTexCoord).rgb);
    // Half-width of the smoothstep band in distance units = half a screen pixel.
    // Analytic per-draw value when provided; fwidth fallback otherwise.
    float w = pc.sdfEdge > 0.0 ? pc.sdfEdge : fwidth(dist) * 0.5 + 1e-4;
    float alpha = smoothstep(0.5 - w, 0.5 + w, dist);
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
