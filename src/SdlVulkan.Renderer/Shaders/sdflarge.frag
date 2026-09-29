#version 450
// MTSDF text pipeline for LARGE text, 64 px/em and up (VkSdfFontAtlas.SingleSampleMinPx): one
// sample at the pixel centre, blended over half a pixel. sdf.frag, the small-text shader, takes
// two samples a pixel so that a stroke thinner than a pixel keeps its ink; at this size the
// thinnest stroke a text face draws spans about two pixels and one sample estimates coverage as
// well. Measured on a page of body text at 600 dpi (75 px/em), this draws in the GPU time the
// one-sample shader always did.
//
// Like sdf.frag it clamps the sample into the glyph's own cell (vCell), half a texel in, because
// the texels around a cell are whatever the atlas page held before, and it shifts the edge out
// by 0.1 px, so text keeps its weight when a zoom carries it across 64 px/em (at this size the
// shift is under 2% of a stem). The sdfEdge push constant is one screen pixel in field units.
layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in vec4 vCell;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float sdfEdge; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
void main() {
    vec2 halfTexel = 0.5 / vec2(textureSize(uTexture, 0));
    float dist = median(texture(uTexture, clamp(vTexCoord, vCell.xy + halfTexel, vCell.zw - halfTexel)).rgb);
    float px = pc.sdfEdge > 0.0 ? pc.sdfEdge : fwidth(dist) + 1e-4;
    float w = min(0.5 * px, 0.45);
    float t = max(0.5 - 0.1 * px, 0.05);
    float alpha = smoothstep(t - w, t + w, dist);
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
