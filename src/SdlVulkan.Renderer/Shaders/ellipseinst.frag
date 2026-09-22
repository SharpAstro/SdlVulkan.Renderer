#version 450
// Fragment side of the instanced ellipse pipeline. The predicate is ellipse.frag's, and is folded
// into a SINGLE discard for the same reason: Mesa llvmpipe mis-compiles a conditional second
// discard in some MSAA paths. What differs is where the ring and the colour come from -- the
// INSTANCE, not the push block -- so one draw carries many ellipses, each with its own hole and
// its own colour.
layout(location = 0) in vec2 vLocal;
layout(location = 1) flat in float vInnerRadius;
layout(location = 2) flat in vec4 vColor;

// Unread here, but declared to match the vertex stage's block exactly -- see ellipseinst.vert.
layout(push_constant) uniform PC { mat4 proj; vec4 color; float innerRadius; } pc;

layout(location = 0) out vec4 FragColor;

void main() {
    float dist = dot(vLocal, vLocal);
    float innerSq = vInnerRadius * vInnerRadius;
    // Outside the unit disc OR inside the hole -> discard, in one statement.
    if (dist > 1.0 || dist < innerSq) discard;
    FragColor = vColor;
}
