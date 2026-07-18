#version 450
layout(location = 0) in vec2 vTexCoord;
layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;
void main() {
    vec4 texel = texture(uTexture, vTexCoord);
    // Color glyphs (emoji, COLR) have their own RGB; monochrome glyphs are white (1,1,1) with varying alpha.
    // Detect color glyphs by checking if RGB deviates from white.
    float isColor = 1.0 - step(0.99, min(texel.r, min(texel.g, texel.b)));
    vec3 rgb = mix(pc.color.rgb, texel.rgb, isColor);
    FragColor = vec4(rgb, pc.color.a * texel.a);
}
