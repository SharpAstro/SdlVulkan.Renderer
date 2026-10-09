#version 450
// Persistent MTSDF text (sdfinst.vert): sdf.frag and sdflarge.frag in one shader, chosen per GLYPH by its
// size on screen, because one draw here holds glyphs of many sizes. The arithmetic of each branch is that
// shader's, verbatim; see them for why each number is what it is. The choice is the batch path's
// (VkSdfFontAtlas.IsSingleSample): one sample from 64 px a em, where the thinnest stroke spans about two
// pixels, two below it. vEdge is flat across a glyph's two triangles, so the branch is uniform within
// every 2x2 quad and the derivatives, taken before it anyway, stay defined.
layout(location = 0) in vec2 vTexCoord;
layout(location = 1) flat in vec4 vCell;
layout(location = 2) flat in vec2 vEdge;   // x: field units per screen pixel, y: em in screen pixels
layout(push_constant) uniform PC { mat4 proj; vec4 color; float scale; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
float median(vec3 v) { return max(min(v.r, v.g), min(max(v.r, v.g), v.b)); }
void main() {
    vec2 dx = dFdx(vTexCoord);
    vec2 dy = dFdy(vTexCoord);
    float px = vEdge.x;
    float t = max(0.5 - 0.025 * px, 0.05);
    vec2 halfTexel = 0.5 / vec2(textureSize(uTexture, 0));
    vec2 lo = vCell.xy + halfTexel;
    vec2 hi = vCell.zw - halfTexel;
    float alpha;
    if (vEdge.y >= 64.0)
    {
        // sdflarge.frag
        float dist = median(texture(uTexture, clamp(vTexCoord, lo, hi)).rgb);
        float w = min(0.5 * px, 0.45);
        alpha = smoothstep(t - w, t + w, dist);
    }
    else
    {
        // sdf.frag
        float ws = min(0.3536 * px, t - 0.01);
        alpha = 0.5 * (
            smoothstep(t - ws, t + ws, median(texture(uTexture, clamp(vTexCoord - 0.25 * (dx + dy), lo, hi)).rgb)) +
            smoothstep(t - ws, t + ws, median(texture(uTexture, clamp(vTexCoord + 0.25 * (dx + dy), lo, hi)).rgb)));
    }
    if (alpha < 0.005) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * alpha);
}
