#version 450
// Stroke pipeline: expands line segments to screen-space quads in the vertex shader.
// ONE INSTANCE per segment carries its two endpoints (aP0, aP1); the six vertices of the
// segment's quad come from gl_VertexIndex, so the endpoints are stored once, not six times
// (16 bytes a segment against 144). halfWidth is in the projection matrix's coordinate space.
//
// Drawn with more than six vertices, an instance also gets a round cap at each end: a fan of
// kCapTriangles triangles around each endpoint, facing away from the segment, from vertex six on.
// Nothing about a cap is stored, and the caller decides per draw whether there are any by the vertex
// count, so a stroke whose caps would be smaller than a pixel costs no more than it did, and one drawn
// large gets them at the radius it is drawn at. Two segments that share a point both cap it, and the
// two half-discs cover the round join between them, so a round-joined polyline needs no joins either.
// A zero-length segment's caps face opposite ways and make a disc.
layout(location = 0) in vec2 aP0;   // per-instance: segment start
layout(location = 1) in vec2 aP1;   // per-instance: segment end
layout(push_constant) uniform PC { mat4 proj; vec4 color; float halfWidth; } pc;

// The six quad corners as (side, endT): side picks the edge (offset +/-1 x halfWidth along the
// segment normal), endT interpolates start -> end. Two triangles, wound exactly as the six
// vertices the CPU used to emit, so the rasterised result is byte-for-byte the same.
const vec2 kCorners[6] = vec2[6](
    vec2(-1.0, 0.0),
    vec2( 1.0, 0.0),
    vec2( 1.0, 1.0),
    vec2(-1.0, 0.0),
    vec2( 1.0, 1.0),
    vec2(-1.0, 1.0)
);

// Triangles in each cap. VkRenderer.StrokeRoundCapTriangles states the same number, since the draw's
// vertex count is 6 + 2 * 3 * kCapTriangles.
const int kCapTriangles = 8;
const float kPi = 3.14159265358979;

void main() {
    vec2 dir = aP1 - aP0;
    float len = length(dir);
    vec2 normal = len > 0.0001 ? vec2(-dir.y, dir.x) / len : vec2(0.0, 1.0);
    vec2 pos;
    if (gl_VertexIndex < 6) {
        vec2 corner = kCorners[gl_VertexIndex];
        pos = mix(aP0, aP1, corner.y);
        pos += normal * corner.x * pc.halfWidth;
    } else {
        int v = gl_VertexIndex - 6;
        bool atEnd = v >= kCapTriangles * 3;
        int k = v % (kCapTriangles * 3);
        int tri = k / 3;
        int corner = k % 3;
        vec2 centre = atEnd ? aP1 : aP0;
        if (corner == 0) {
            pos = centre;
        } else {
            // The direction pointing out of the segment at this end, and the normal beside it: the fan
            // sweeps from one side of the line, through straight out, to the other side.
            vec2 tangent = vec2(normal.y, -normal.x);
            vec2 outward = atEnd ? tangent : -tangent;
            vec2 side = vec2(-outward.y, outward.x);
            float a = -0.5 * kPi + kPi * float(tri + corner - 1) / float(kCapTriangles);
            pos = centre + (outward * cos(a) + side * sin(a)) * pc.halfWidth;
        }
    }
    gl_Position = pc.proj * vec4(pos, 0.0, 1.0);
}
