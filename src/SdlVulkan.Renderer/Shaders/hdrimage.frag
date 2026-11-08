#version 450
// HDR image fragment shader: samples a float texture of scene-linear light (1.0 = 203 cd/m2, straight alpha),
// converts its primaries to BT.709 and writes either an SDR or an scRGB pixel.
//
// Every line of arithmetic below is a transcription of the CPU functions in HdrColor.cs (HdrColor.ToSdrEncoded and
// HdrColor.ToScRgb, with ToneMap, SrgbInverseEotf, ClampToPeak and NitsScale), which HdrColorTests prove and
// HdrImageRenderTests hold this shader to within one 8-bit step. Change them together.
//
// The output mode is a SPECIALISATION CONSTANT chosen when the pipeline is created, never a runtime branch: the
// driver compiles the other mode away, so neither pays for the other.
//
// Own push block (128 bytes, the guaranteed minimum), because the shared 84-byte block has no room for a matrix.
// Vertex stage (tex.vert) reads proj only. This pipeline has a layout of its own (VulkanDevice.HdrImagePipelineLayout)
// over the same single-sampler descriptor set layout, and nothing else is bound through it.
layout(constant_id = 0) const int OUTPUT_SCRGB = 0; // 0: tone-map to SDR and sRGB-encode; 1: scRGB clamped to the peak

layout(location = 0) in vec2 vTexCoord;
layout(push_constant) uniform PC {
    mat4 proj;
    vec4 row0;   // xyz: primaries -> BT.709, row 0 (row-major as in HdrColor.RgbToRgbMatrix)
    vec4 row1;
    vec4 row2;
    vec4 hdr;    // x: source peak (scene-linear, peak cd/m2 / 203); y: NitsScale (203/80); z: display peak in scRGB
} pc;
layout(set = 0, binding = 0) uniform sampler2D uTexture;
layout(location = 0) out vec4 FragColor;

const float TONE_KNEE = 0.75; // HdrColor.ToneKnee

// HdrColor.ToneMap
float toneMap(float x, float peak) {
    if (x <= 0.0) return 0.0;
    if (peak <= 1.0 || x <= TONE_KNEE) return min(x, 1.0);
    float u = (x - TONE_KNEE) / (1.0 - TONE_KNEE);
    float w = (peak - TONE_KNEE) / (1.0 - TONE_KNEE);
    float g = u * (1.0 + u / (w * w)) / (1.0 + u);
    return min(TONE_KNEE + (1.0 - TONE_KNEE) * g, 1.0);
}

// HdrColor.SrgbInverseEotf
float srgbEncode(float l) {
    l = clamp(l, 0.0, 1.0);
    return l <= 0.0031308 ? l * 12.92 : 1.055 * pow(l, 1.0 / 2.4) - 0.055;
}

void main() {
    vec4 texel = texture(uTexture, vTexCoord);
    // HdrColor.Apply
    vec3 lin = vec3(dot(pc.row0.xyz, texel.rgb), dot(pc.row1.xyz, texel.rgb), dot(pc.row2.xyz, texel.rgb));
    if (OUTPUT_SCRGB == 1) {
        // HdrColor.ToScRgb: NitsScale, then ClampToPeak (the top only: negatives are out-of-BT.709 colour).
        FragColor = vec4(min(lin * pc.hdr.y, vec3(pc.hdr.z)), texel.a);
    } else {
        // HdrColor.ToSdrEncoded
        FragColor = vec4(srgbEncode(toneMap(lin.r, pc.hdr.x)),
                         srgbEncode(toneMap(lin.g, pc.hdr.x)),
                         srgbEncode(toneMap(lin.b, pc.hdr.x)), texel.a);
    }
}
