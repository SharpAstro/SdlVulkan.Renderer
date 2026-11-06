#version 450
// Slug text pipeline vertex stage. Per vertex: the position in the caller's space (the quad's corner, already
// transformed by CPU: baseline origin, font size, rotation, horizontal scale), the EM-SPACE SAMPLE COORDINATE at that
// corner (y up, the unit the glyph blob is in) and the word offset of the glyph's blob in the store's buffer. The
// quad is the glyph's em box padded by about a pixel on every side, so the edge pixels the glyph only touches are
// still drawn (VkRenderer.WriteSlugQuad). slug.frag interpolates the em coordinate and takes the coverage of the
// outline at each pixel from it; nothing here depends on the glyph's shape.
//
// The blob offset is the same on all six vertices and flat. A blob that is not FormatVersion 1 is refused here
// as well as in slug.frag: the quad is placed behind the near plane, so it is clipped and costs no fragment.
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aEm;
layout(location = 2) in uint aBlob;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float pad; } pc;
layout(set = 0, binding = 0) readonly buffer Glyphs { int w[]; } g;
layout(location = 0) out vec2 vEm;
layout(location = 1) flat out uint vBlob;
void main() {
    vEm = aEm;
    vBlob = aBlob;
    if (g.w[aBlob] != 1) {
        gl_Position = vec4(0.0, 0.0, -1.0, 1.0);
        return;
    }
    gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
}
