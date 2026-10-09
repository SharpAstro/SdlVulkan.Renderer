#version 450
// Persistent MTSDF text: ONE INSTANCE per glyph, laid out once in the caller's own space (a page's
// points) and drawn at any zoom through the projection. The quad's six vertices come from
// gl_VertexIndex, as in stroke.vert, so a glyph is 48 bytes rather than six 32-byte vertices.
//
// Per instance: the quad's top-left corner and its two edge vectors (the writing direction and down,
// rotation and horizontal scale already applied), the glyph's cell in its atlas page, which is also the
// quad's texture rectangle, and two sizes in the caller's units: the em, and the distance-field units
// one such unit spans. With the zoom (pc.scale, in the slot the batch path uses for sdfEdge) those give
// the screen-pixel band and the on-screen size per GLYPH, where the batch path pushes them per draw, so
// one draw holds glyphs of any mix of sizes. See sdfinst.frag.
layout(location = 0) in vec2 aOrigin;
layout(location = 1) in vec2 aAxisU;
layout(location = 2) in vec2 aAxisV;
layout(location = 3) in vec4 aCell;
layout(location = 4) in vec2 aSize;   // x: em in caller units, y: field units per caller unit
layout(push_constant) uniform PC { mat4 proj; vec4 color; float scale; } pc;
layout(location = 0) out vec2 vTexCoord;
layout(location = 1) flat out vec4 vCell;
layout(location = 2) flat out vec2 vEdge;   // x: field units per screen pixel (sdfEdge), y: em in pixels
const vec2 kCorners[6] = vec2[6](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));
void main() {
    vec2 c = kCorners[gl_VertexIndex];
    gl_Position = pc.proj * vec4(aOrigin + aAxisU * c.x + aAxisV * c.y, 0.0, 1.0);
    vTexCoord = mix(aCell.xy, aCell.zw, c);
    vCell = aCell;
    // VkSdfFontAtlas.FieldUnitsPerPixel, per glyph: its clamp included.
    vEdge = vec2(clamp(aSize.y / pc.scale, 1e-3, 8.0), aSize.x * pc.scale);
}
