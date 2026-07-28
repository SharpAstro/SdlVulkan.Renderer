#version 450
// Signed distance to a rounded box, evaluated in PIXELS: vLocal is the offset from the rect centre
// in pixels, so the coverage ramp below is a true one-pixel feather with no fwidth / derivative
// needed (and therefore no dependence on how the driver groups quads).
//
// One quad covers the whole shape, so nothing overlaps and a translucent fill blends exactly once.
// That is the property the CPU fallback in DIR.Lib has to work for -- it emits non-overlapping
// horizontal spans precisely because a cross-plus-four-corner-ellipses decomposition double-blends
// and darkens the corners. Here it is free.
layout(location = 0) in vec2 vLocal;
layout(location = 1) in vec2 vHalf;
layout(location = 2) in float vRadius;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float innerRadius; } pc;
layout(location = 0) out vec4 FragColor;
void main() {
    // Standard rounded-box SDF. The min(max(q.x, q.y), 0.0) term carries the correct NEGATIVE
    // distance for interior fragments; without it the interior reads 0 and the feather below would
    // halve the alpha of the whole fill rather than just its edge.
    vec2 q = abs(vLocal) - (vHalf - vec2(vRadius));
    float d = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - vRadius;

    // SINGLE discard -- see ellipse.frag: llvmpipe can mis-compile a second conditional discard
    // under MSAA, so the outside test stays one statement.
    if (d > 0.5) discard;

    float coverage = clamp(0.5 - d, 0.0, 1.0);
    FragColor = vec4(pc.color.rgb, pc.color.a * coverage);
}
