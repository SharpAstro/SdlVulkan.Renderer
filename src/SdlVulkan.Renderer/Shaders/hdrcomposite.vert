#version 450
// HDR composite vertex shader: one oversized triangle that covers the whole target, no vertex buffer.
// The composite pass (VulkanContext.Hdr.cs) draws it once to lay the SDR layer over the scRGB target.
void main() {
    vec2 uv = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}
