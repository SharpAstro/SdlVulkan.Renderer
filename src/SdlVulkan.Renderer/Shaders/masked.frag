#version 450
// Textured fragment whose ALPHA is masked by a second, single-channel texture sampled at the same
// UV. The colour is untouched, so a mask removes part of a texture without tinting what remains --
// which is what separates this from drawing the same quad twice with a blend.
//
// The mask is coverage, not a stencil: 1 keeps the texel, 0 removes it, and the values between are
// a partial alpha, so an antialiased edge in the mask stays antialiased on screen.
//
// It exists because the alternative is baking the mask into the texture's own alpha, and that
// requires the texture's pixels on the CPU at the moment the mask is known. Where the mask is
// decided globally -- across a whole mosaic of textures, say -- that forces every one of them to be
// held in memory at once, or decoded a second time. Sampling a mask a fraction of the texture's
// size costs neither.
layout(location = 0) in vec2 vTexCoord;
layout(push_constant) uniform PC { mat4 proj; vec4 color; } pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(set = 0, binding = 1) uniform sampler2D uMask;
layout(location = 0) out vec4 FragColor;
void main() {
    vec4 c = texture(uTexture, vTexCoord);
    c.a *= texture(uMask, vTexCoord).r;
    FragColor = c;
}
