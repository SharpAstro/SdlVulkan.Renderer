#version 450
// Rounded-box fill: ONE quad, distance-field evaluated per fragment.
//
// The rounded-box parameters (half extents + corner radius) ride on VERTEX ATTRIBUTES, not on the
// push-constant block. They are constant across the quad, so interpolating them reproduces the same
// value at every fragment, and the shared 84-byte push block (mat4 proj + vec4 color + float
// innerRadius) stays byte-for-byte identical to every other pipeline's. That matters: the layout is
// shared via Surface.PipelineLayout and pushed with a single 84-byte vkCmdPushConstants call, and
// ellipse.vert documents what happens when a per-stage block disagrees with the pushed range --
// llvmpipe / Mesa validates strictly and can SEGV inside the shader compiler. Growing the block for
// one pipeline would mean growing it for all of them.
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aLocal;    // offset from the rect centre, in pixels
layout(location = 2) in vec2 aHalf;     // half extents, in pixels
layout(location = 3) in float aRadius;  // corner radius, in pixels
layout(push_constant) uniform PC { mat4 proj; vec4 color; float innerRadius; } pc;
layout(location = 0) out vec2 vLocal;
layout(location = 1) out vec2 vHalf;
layout(location = 2) out float vRadius;
void main() {
    gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
    vLocal = aLocal;
    vHalf = aHalf;
    vRadius = aRadius;
}
