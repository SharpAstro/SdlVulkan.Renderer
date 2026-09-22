#version 450
// Fragment side of the ellipse pipeline: the pixel-distance rule DIR.Lib's Renderer.DrawEllipse
// states, evaluated with the GPU's own gradient. vLocal is this fragment's coordinate under the
// inverse of the affine map the quad carries, so r = |vLocal| is 1 on the boundary; the
// screen-space derivative of r is the same |grad r| the CPU default computes analytically, and
// dividing by it turns a local distance into PIXELS whatever the ellipse's rotation, scale or
// shear. That is what makes strokeWidth a pixel width at every point of every ellipse, where a
// hole given as a fraction of the semi-diameter could only be one for a circle.
//
// Coverage is a half-plane ramp: a fill covers clamp(0.5 - d, 0, 1) and a stroke of width w covers
// clamp(0.5 + w/2 - |d|, 0, 1), one anti-aliased pixel either side of the edge. A strokeWidth of 0
// is the fill, which is what lets one pipeline serve both entry points.
//
// ONE discard, as before: Mesa llvmpipe mis-compiles a conditional second discard in some MSAA
// paths, so every branch folds into a single coverage value and a single test on it.
layout(location = 0) in vec2 vLocal;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float strokeWidth; } pc;
layout(location = 0) out vec4 FragColor;
void main() {
    float r = length(vLocal);
    float g = max(length(vec2(dFdx(r), dFdy(r))), 1e-6);
    float d = (r - 1.0) / g;
    float coverage = pc.strokeWidth > 0.0
        ? clamp(0.5 + pc.strokeWidth * 0.5 - abs(d), 0.0, 1.0)
        : clamp(0.5 - d, 0.0, 1.0);
    if (coverage <= 0.0) discard;
    FragColor = vec4(pc.color.rgb, pc.color.a * coverage);
}
