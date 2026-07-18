#version 450
// Pass-through fragment shader for textures -- no glyph color detection.
layout(location = 0) in vec2 vTexCoord;
layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
void main() {
    FragColor = texture(uTexture, vTexCoord);
}
