#version 450
// Fragment side of the instanced ellipse pipeline. The rule is ellipse.frag's, the pixel-distance
// rule DIR.Lib's Renderer.DrawEllipse states: r = |vLocal| is 1 on the boundary, its screen-space
// derivative turns a local distance into pixels for any affine placement, and coverage is a
// half-plane ramp one pixel wide. What differs is where the stroke and the colour come from -- the
// INSTANCE, not the push block -- so one draw carries many ellipses, each filled or stroked its
// own width in its own colour.
//
// ONE discard, for the same reason as ellipse.frag: Mesa llvmpipe mis-compiles a conditional
// second discard in some MSAA paths.
layout(location = 0) in vec2 vLocal;
layout(location = 1) flat in float vStrokeWidth;
layout(location = 2) flat in vec4 vColor;

// Unread here, but declared to match the vertex stage's block exactly -- see ellipseinst.vert.
layout(push_constant) uniform PC { mat4 proj; vec4 color; float strokeWidth; } pc;

layout(location = 0) out vec4 FragColor;

void main() {
    float r = length(vLocal);
    float g = max(length(vec2(dFdx(r), dFdy(r))), 1e-6);
    float d = (r - 1.0) / g;
    float coverage = vStrokeWidth > 0.0
        ? clamp(0.5 + vStrokeWidth * 0.5 - abs(d), 0.0, 1.0)
        : clamp(0.5 - d, 0.0, 1.0);
    if (coverage <= 0.0) discard;
    FragColor = vec4(vColor.rgb, vColor.a * coverage);
}
