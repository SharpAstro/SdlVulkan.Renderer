#version 450
// MTSDF text pipeline for LARGE text, 64 px/em and up (VkSdfFontAtlas.SingleSampleMinPx): one
// sample at the pixel centre, blended over half a pixel. sdf.frag, the small-text shader, takes
// four samples a pixel so that a stroke thinner than a pixel keeps its ink; at this size the
// thinnest stroke a text face draws spans about two pixels, one sample estimates coverage as
// well, and four would only cost (sdf.frag's header has the numbers, and the reason this is a
// pipeline of its own rather than a branch there).
//
// The sample is clamped into the glyph's own cell (vCell), half a texel in, for the reason
// sdf.frag gives: the texels around a cell are whatever the atlas page held before. The sdfEdge
// push constant is the half-band in distance units, half a screen pixel here.
layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in vec4 vCell;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float sdfEdge; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
void main() {
    vec2 halfTexel = 0.5 / vec2(textureSize(uTexture, 0));
    float dist = median(texture(uTexture, clamp(vTexCoord, vCell.xy + halfTexel, vCell.zw - halfTexel)).rgb);
    float w = pc.sdfEdge > 0.0 ? pc.sdfEdge : fwidth(dist) * 0.5 + 1e-4;
    float alpha = smoothstep(0.5 - w, 0.5 + w, dist);
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
