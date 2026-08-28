#version 450
// Stroke pipeline: expands line segments to screen-space quads in the vertex shader.
// ONE INSTANCE per segment carries its two endpoints (aP0, aP1); the six vertices of the
// segment's quad come from gl_VertexIndex, so the endpoints are stored once, not six times
// (16 bytes a segment against 144). halfWidth is in the projection matrix's coordinate space.
layout(location = 0) in vec2 aP0;   // per-instance: segment start
layout(location = 1) in vec2 aP1;   // per-instance: segment end
layout(push_constant) uniform PC { mat4 proj; vec4 color; float halfWidth; } pc;

// The six quad corners as (side, endT): side picks the edge (offset +/-1 x halfWidth along the
// segment normal), endT interpolates start -> end. Two triangles, wound exactly as the six
// vertices the CPU used to emit, so the rasterised result is byte-for-byte the same.
const vec2 kCorners[6] = vec2[6](
    vec2(-1.0, 0.0),
    vec2( 1.0, 0.0),
    vec2( 1.0, 1.0),
    vec2(-1.0, 0.0),
    vec2( 1.0, 1.0),
    vec2(-1.0, 1.0)
);

void main() {
    vec2 corner = kCorners[gl_VertexIndex];
    vec2 pos = mix(aP0, aP1, corner.y);
    vec2 dir = aP1 - aP0;
    float len = length(dir);
    vec2 normal = len > 0.0001 ? vec2(-dir.y, dir.x) / len : vec2(0.0, 1.0);
    pos += normal * corner.x * pc.halfWidth;
    gl_Position = pc.proj * vec4(pos, 0.0, 1.0);
}
