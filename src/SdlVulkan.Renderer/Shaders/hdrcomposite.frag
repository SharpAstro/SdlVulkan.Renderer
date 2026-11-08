#version 450
// HDR composite fragment shader: reads one pixel of the SDR layer (the frame as every ordinary pipeline drew it,
// sRGB-encoded and premultiplied by alpha, in a B8G8R8A8Unorm image) and writes it to the scRGB target as linear
// light scaled to the display's SDR white, premultiplied, for blending One / OneMinusSrcAlpha over whatever the
// composite pass has put beneath it (the queued HDR images).
//
// Only the HDR path runs this; the SDR path has no layer and no composite. The sRGB decode is the inverse of
// HdrColor.SrgbInverseEotf, i.e. HdrColor.SrgbEotf, and HdrCompositeTests hold this shader to it within one 8-bit step.
//
// The layer holds premultiplied colour (the pipelines blend colour with SrcAlpha/OneMinusSrcAlpha and alpha with
// One/OneMinusSrcAlpha), so the transfer function must be applied to the straight colour: divide by alpha, decode,
// scale, multiply by alpha again. At alpha 1, which is every pixel of an opaque frame, that is just decode and scale.
layout(push_constant) uniform PC {
    vec4 p;   // x: SDR white in scRGB units (1.0 = 80 cd/m2)
} pc;
layout(set = 0, binding = 0) uniform sampler2D uLayer;
layout(location = 0) out vec4 FragColor;

// HdrColor.SrgbEotf
float srgbDecode(float e) {
    return e <= 0.04045 ? e / 12.92 : pow((e + 0.055) / 1.055, 2.4);
}

void main() {
    // The layer and the target are the same size and this draw covers the target, so the pixel is fetched, not filtered.
    vec4 t = texelFetch(uLayer, ivec2(gl_FragCoord.xy), 0);
    vec3 straight = t.a > 0.0 ? t.rgb / t.a : vec3(0.0);
    vec3 lin = vec3(srgbDecode(straight.r), srgbDecode(straight.g), srgbDecode(straight.b)) * pc.p.x;
    FragColor = vec4(lin * t.a, t.a);
}
