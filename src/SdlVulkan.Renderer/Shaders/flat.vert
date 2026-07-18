#version 450
layout(location = 0) in vec2 aPos;
layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
void main() {
    gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
}
