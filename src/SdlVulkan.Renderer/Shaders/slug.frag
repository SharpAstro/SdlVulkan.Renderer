#version 450
// Slug text pipeline fragment stage: the coverage of a glyph's OUTLINE at this pixel, computed from the glyph's
// blob in a storage buffer (DIR.Lib's SlugGlyphBlob; the layout is documented there and is what
// SlugCoverageOracle in the tests ports line by line). Eric Lengyel's Slug algorithm (patent dedicated to the
// public domain from 2026-03-17), following the structure of the pixel shader in his paper and in HarfBuzz's
// hb-gpu-fragment.glsl; written against this repo's own blob.
//
// What a pixel does. Two rays leave the sample, one along x through the glyph's horizontal band, one along y through
// its vertical band. Each band holds the curves its strip touches, as two lists sorted along the ray (ascending
// by smallest coordinate, descending by largest) and a split: a sample at or past the split walks the descending list
// and casts towards +, one before it walks the ascending list and casts towards -. Both give the winding number
// (a closed outline crosses a line as often one way as the other), the walk stops at the first curve wholly behind
// (ahead of) the pixel's footprint, and every curve it does reach costs one root solve. Per curve the signs of the
// three control points across the ray pick which of the quadratic's two roots count (a table lookup); each counting
// root adds saturate(root * pixelsAlongRay + 0.5), so a crossing more than half a pixel ahead counts fully, one
// more than half a pixel behind not at all and one through the pixel blends: an edge's coverage is analytic, and
// the signed sum is the winding number, which nonzero winding clamps to 1. The two rays are then combined weighted
// by how near their crossings came to the pixel centre.
//
// ANALYTIC, NOT MSAA. Coverage here is the shader's own; it does not read the sample count. On a single-sample main
// pass (SingleSampleMainPass) text is exactly as smooth as on a multisampled one, and on a multisampled pass the
// shader still runs once per pixel (no sample-rate shading), so MSAA adds nothing and costs nothing here.
//
// The pixel's footprint comes from the derivatives of the em coordinate, not from a font size: the length of
// (dFdx(em.x), dFdy(em.x)) is the em a pixel step spans along x however the glyph is rotated or stretched
// horizontally (and likewise for y), so pixelsAlongRay is 1 / that. (Not fwidth, the L1 length: it overstates the
// footprint of a rotated glyph by up to sqrt(2) and softens its edges.)
//
// OUTPUT is the SDF shaders' form: the colour and colour.a * coverage, which the pipeline's SrcAlpha /
// OneMinusSrcAlpha blend (alpha One / OneMinusSrcAlpha) turns into a premultiplied result over any background,
// including the alpha-0 clear of a transparent window.
layout(location = 0) in vec2 vEm;
layout(location = 1) flat in uint vBlob;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float pad; } pc;
layout(set = 0, binding = 0) readonly buffer Glyphs { int w[]; } g;
layout(location = 0) out vec4 FragColor;

const float EPS = 1.0 / 65536.0;

float sat(float v) { return clamp(v, 0.0, 1.0); }

// Lengyel's root-eligibility table: from the signs of the three points' across coordinates (a set bit is negative),
// bit 0 of the result says the first root counts and bit 8 the second. 0x2E74 packs the eight cases for each.
uint rootCode(float y1, float y2, float y3) {
    uint shift = (y1 < 0.0 ? 1u : 0u) | (y2 < 0.0 ? 2u : 0u) | (y3 < 0.0 ? 4u : 0u);
    return (0x2E74u >> shift) & 0x0101u;
}

// One curve (word offset c) against the ray through s = (u, v) in the ray's frame: u along the ray, v across it.
// horiz picks which blob coordinate is u. mirrored casts the ray towards -u (the curve's u negated, the sums
// negated back). Returns false when the curve is wholly behind (ahead of) the pixel's footprint along the ray:
// it, and in a sorted list every later one, adds nothing.
bool walkCurve(int c, bool horiz, float scale, vec2 s, float ppe, bool mirrored, inout float cov, inout float wgt) {
    int a = horiz ? 0 : 1;
    int b = horiz ? 1 : 0;
    vec2 p1 = vec2(float(g.w[c + a]), float(g.w[c + b])) * scale - s;
    vec2 p2 = vec2(float(g.w[c + 2 + a]), float(g.w[c + 2 + b])) * scale - s;
    vec2 p3 = vec2(float(g.w[c + 4 + a]), float(g.w[c + 4 + b])) * scale - s;
    if (mirrored) {
        p1.x = -p1.x; p2.x = -p2.x; p3.x = -p3.x;
    }

    if (max(max(p1.x, p2.x), p3.x) * ppe < -0.5) return false;

    uint code = rootCode(p1.y, p2.y, p3.y);
    if (code == 0u) return true;

    // Solve a t^2 - 2 b t + p1 = 0 in v, the across coordinate, then read u(t) at the roots.
    float au = p1.x - 2.0 * p2.x + p3.x;
    float av = p1.y - 2.0 * p2.y + p3.y;
    float bu = p1.x - p2.x;
    float bv = p1.y - p2.y;
    float t1, t2;
    if (abs(av) < EPS) {
        // Nearly linear (a line, or a quadratic whose control is its midpoint): t = p1 / (2 b).
        t1 = t2 = p1.y * (0.5 / bv);
    } else {
        float ra = 1.0 / av;
        float d = sqrt(max(bv * bv - av * p1.y, 0.0));
        t1 = (bv - d) * ra;
        t2 = (bv + d) * ra;
    }
    float r1 = ((au * t1 - bu * 2.0) * t1 + p1.x) * ppe;
    float r2 = ((au * t2 - bu * 2.0) * t2 + p1.x) * ppe;

    // The first root adds, the second subtracts; mirrored, the contributions are those of the ray the other way and
    // the sign is restored.
    if ((code & 1u) != 0u) {
        cov += mirrored ? -sat(r1 + 0.5) : sat(r1 + 0.5);
        wgt = max(wgt, sat(1.0 - abs(r1) * 2.0));
    }
    if (code > 1u) {
        cov -= mirrored ? -sat(r2 + 0.5) : sat(r2 + 0.5);
        wgt = max(wgt, sat(1.0 - abs(r2) * 2.0));
    }
    return true;
}

// One ray. The vertical ray is the horizontal one on the curves with x and y swapped; the swap is a reflection, so
// it flips the sign of every contribution and the vertical sum is negated to agree about the winding direction.
void axis(int blob, bool horiz, vec2 em, float ppe, out float cov, out float wgt) {
    cov = 0.0;
    wgt = 0.0;
    int upem = g.w[blob + 1];
    float scale = 1.0 / float(8 * upem); // blob units to em
    int hBands = g.w[blob + 3];
    int vBands = g.w[blob + 4];
    int bandOffset = g.w[blob + 5];

    // The sample in the ray's own frame: u along the ray, v across it.
    vec2 s = horiz ? em : em.yx;

    // The band: the glyph's extent across the ray in equal strips, clamped so a sample a pixel outside the glyph, where
    // the quad is padded to, still reads the end band.
    int bands = horiz ? hBands : vBands;
    int crossMin = horiz ? g.w[blob + 9] : g.w[blob + 8];
    int crossMax = horiz ? g.w[blob + 11] : g.w[blob + 10];
    float span = float(crossMax - crossMin);
    int band = span <= 0.0 ? 0
        : clamp(int(floor((s.y / scale - float(crossMin)) * float(bands) / span)), 0, bands - 1);

    int header = blob + bandOffset + (horiz ? band : hBands + band) * 4;
    int count = g.w[header];
    int descStart = blob + g.w[header + 1];
    int ascStart = blob + g.w[header + 2];
    float split = float(g.w[header + 3]);

    bool descending = s.x / scale >= split;
    int start = descending ? descStart : ascStart;
    for (int k = 0; k < count; k++) {
        int curve = blob + g.w[start + k];
        if (!walkCurve(curve, horiz, scale, s, ppe, !descending, cov, wgt)) break;
    }
    if (!horiz) cov = -cov;
}

void main() {
    int blob = int(vBlob);
    // A blob of a layout this shader does not know is not drawn.
    if (g.w[blob] != 1) discard;
    // The em a pixel spans along each axis; the pixels per em along a ray is its inverse. Each component's gradient
    // over the screen is (dFdx, dFdy) of it, and its LENGTH is the em one pixel step moves it, whichever way the glyph is
    // turned (it is 1 / size for a glyph of that size at any rotation). fwidth is the sum of the two magnitudes
    // instead, which is the same only at 0 and 90 degrees and a factor of up to sqrt(2) too wide at 45: the ramp of
    // every edge of rotated text came out that much softer than the pixel needs.
    vec2 ddx = dFdx(vEm);
    vec2 ddy = dFdy(vEm);
    float ppeX = 1.0 / max(length(vec2(ddx.x, ddy.x)), 1e-9);
    float ppeY = 1.0 / max(length(vec2(ddx.y, ddy.y)), 1e-9);

    float xcov, xwgt, ycov, ywgt;
    axis(blob, true, vEm, ppeX, xcov, xwgt);
    axis(blob, false, vEm, ppeY, ycov, ywgt);

    // Lengyel's CalcCoverage: the weighted mean of the two rays, but never below the lesser of the two, which is what
    // keeps a thin stem that one ray grazes from fading.
    float coverage = max(
        abs(xcov * xwgt + ycov * ywgt) / max(xwgt + ywgt, EPS),
        min(abs(xcov), abs(ycov)));
    coverage = sat(coverage);

    // Below half an 8-bit step the blend writes nothing; skip the fragment instead of blending it.
    if (coverage < 0.5 / 255.0) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * coverage);
}
