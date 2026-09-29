#version 450
// MTSDF text pipeline vertex stage: the textured layout (vec2 pos + vec2 uv) plus the glyph's CELL in
// the atlas page (u0, v0, u1, v1), the same for all six vertices of a quad. sdf.frag takes several
// samples per pixel and clamps each one into this rectangle: a sample that left the cell would read
// texels no glyph wrote, and those are not "outside the glyph" but whatever the page held before --
// uninitialised device memory on a new page, a glyph evicted from a recycled one. See sdf.frag.
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aTexCoord;
layout(location = 2) in vec4 aCell;
layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
layout(location = 0) out vec2 vTexCoord;
layout(location = 1) flat out vec4 vCell;
void main() {
    gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
    vTexCoord = aTexCoord;
    vCell = aCell;
}
