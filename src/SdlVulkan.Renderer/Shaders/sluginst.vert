#version 450
// Persistent Slug text: ONE INSTANCE per glyph, laid out once in the caller's own space (a page's points) and drawn at
// any zoom through the projection, as sdfinst.vert does for MTSDF. The quad's six vertices come from gl_VertexIndex, so
// a glyph is 44 bytes rather than six 20-byte vertices (VkRenderer.SlugInstanceFloats).
//
// FRAGMENT STAGE: there is none of its own. The outputs below are exactly the inputs of slug.frag (the em coordinate,
// location 0, and the blob word offset, flat, location 1) and the push block is the same, so the instanced pipeline is
// built from THIS vertex stage and slug.frag's SPIR-V. That is the one copy of the coverage code; a second file with
// the same text could only drift from it.
//
// Per instance: the quad's top-left corner and its two edge vectors in the caller's space (the writing direction and
// down, rotation and horizontal scale already applied, so both come free), the glyph's TIGHT em box as
// (left, top, right, bottom) with em y up, which is also the quad's em coordinate at its corners, and the word offset
// of the glyph's blob in the store's buffer. The axes span the tight box.
//
// PADDING is done here, because it is a distance on the screen (about a pixel, so an edge pixel the outline only touches
// is still drawn, as VkRenderer.WriteSlugQuad pads the batch path's quad) and the layout is made before the zoom is
// known. pc.scale is the zoom, in the push block's last float (the slot slug.vert calls pad): caller units a pixel
// spans are 1 / scale, as a fraction of an axis that is that over the axis's length, and the em coordinate is carried
// by the same fraction, so the quad grows by a pixel on every side whatever the glyph's size, rotation or stretch.
//
// An instance of zeros (a glyph the store did not have when the layout was made) has axes of zero length: its quad has
// no area, covers no pixel and costs no fragment.
layout(location = 0) in vec2 aOrigin;
layout(location = 1) in vec2 aAxisU;
layout(location = 2) in vec2 aAxisV;
layout(location = 3) in vec4 aEm;     // left, top, right, bottom (em, y up)
layout(location = 4) in uint aBlob;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float scale; } pc;
layout(set = 0, binding = 0) readonly buffer Glyphs { int w[]; } g;
layout(location = 0) out vec2 vEm;
layout(location = 1) flat out uint vBlob;
const vec2 kCorners[6] = vec2[6](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));
const float kPadPx = 1.0;
void main() {
    vBlob = aBlob;
    vEm = vec2(0.0);
    // A blob that is not FormatVersion 1 is refused as slug.vert refuses it: behind the near plane, clipped.
    if (g.w[aBlob] != 1) {
        gl_Position = vec4(0.0, 0.0, -1.0, 1.0);
        return;
    }
    vec2 c = kCorners[gl_VertexIndex];
    float lenU = length(aAxisU);
    float lenV = length(aAxisV);
    float padCaller = kPadPx / max(pc.scale, 1e-6);
    vec2 pad = vec2(lenU > 0.0 ? padCaller / lenU : 0.0, lenV > 0.0 ? padCaller / lenV : 0.0);
    vec2 t = mix(-pad, 1.0 + pad, c);
    vEm = aEm.xy + (aEm.zw - aEm.xy) * t;
    gl_Position = pc.proj * vec4(aOrigin + aAxisU * t.x + aAxisV * t.y, 0.0, 1.0);
}
