#version 450
// Stroke pipeline: expands line segments to screen-space quads in the vertex shader.
// Each vertex carries both endpoints (aP0, aP1) and side/end selectors.
// halfWidth is in the same coordinate space as the projection matrix.
layout(location = 0) in vec2 aP0;
layout(location = 1) in vec2 aP1;
layout(location = 2) in vec2 aParams;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float halfWidth; } pc;
void main() {
    vec2 pos = mix(aP0, aP1, aParams.y);
    vec2 dir = aP1 - aP0;
    float len = length(dir);
    vec2 normal = len > 0.0001 ? vec2(-dir.y, dir.x) / len : vec2(0.0, 1.0);
    pos += normal * aParams.x * pc.halfWidth;
    gl_Position = pc.proj * vec4(pos, 0.0, 1.0);
}
