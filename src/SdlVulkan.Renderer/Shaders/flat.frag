#version 450
layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
layout(location = 0) out vec4 FragColor;
void main() {
    FragColor = pc.color;
}
