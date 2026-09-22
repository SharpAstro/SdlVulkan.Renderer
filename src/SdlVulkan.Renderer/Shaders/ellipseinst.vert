#version 450
// Instanced ellipse pipeline: ONE INSTANCE per ellipse, carrying its centre and the two semi-axis
// VECTORS that map the unit disc onto it. The axes are the images of local (1,0) and (0,1), so
// rotation, non-uniform scale and shear are all just what those two vectors happen to be -- there
// is no angle to pass and no trigonometry here.
//
// The quad's six vertices come from gl_VertexIndex exactly as in stroke.vert, so an ellipse costs
// 44 bytes instead of six vertices of it, and a page or a chart full of them is ONE draw call.
// Colour rides on the instance rather than the push block, which is the whole point: a single call
// can carry thousands of ellipses in as many colours.
layout(location = 0) in vec2 aCentre;       // per-instance
layout(location = 1) in vec2 aAxisU;        // per-instance: image of local (1,0)
layout(location = 2) in vec2 aAxisV;        // per-instance: image of local (0,1)
layout(location = 3) in float aStrokeWidth; // per-instance: stroke in PIXELS, 0 = filled
layout(location = 4) in vec4 aColor;        // per-instance

// Declared identically in both stages even though the fragment side reads none of it: the 84-byte
// vkCmdPushConstants call targets Vertex|Fragment, and a push range exceeding the block declared on
// a stage it names can SEGV inside Mesa's shader compiler. Same reasoning as ellipse.vert.
layout(push_constant) uniform PC { mat4 proj; vec4 color; float strokeWidth; } pc;

layout(location = 0) out vec2 vLocal;
layout(location = 1) flat out float vStrokeWidth;
layout(location = 2) flat out vec4 vColor;

const vec2 kCorners[6] = vec2[6](
    vec2(-1.0, -1.0),
    vec2( 1.0, -1.0),
    vec2( 1.0,  1.0),
    vec2(-1.0, -1.0),
    vec2( 1.0,  1.0),
    vec2(-1.0,  1.0)
);

void main() {
    // The footprint is the unit square grown by the stroke's outer half plus one pixel of edge,
    // in LOCAL units per axis, so the anti-aliased rim has somewhere to land. This is the rule on
    // DIR.Lib's Renderer.DrawEllipse and the same padding VkRenderer.EllipseQuad gives a single
    // ellipse, so an instance and a single draw of the same shape cover the same pixels.
    float pad = aStrokeWidth * 0.5 + 1.0;
    vec2 ext = vec2(1.0 + pad / max(length(aAxisU), 1e-6),
                    1.0 + pad / max(length(aAxisV), 1e-6));
    vec2 local = kCorners[gl_VertexIndex] * ext;
    vec2 pos = aCentre + local.x * aAxisU + local.y * aAxisV;
    gl_Position = pc.proj * vec4(pos, 0.0, 1.0);
    vLocal = local;
    vStrokeWidth = aStrokeWidth;
    vColor = aColor;
}
