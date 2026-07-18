#version 450
// Vertex and fragment push-constant blocks declare an IDENTICAL layout. The
// vertex stage doesn't read innerRadius but it's still part of the block so the
// 84-byte vkCmdPushConstants call (which targets Vertex|Fragment stage flags)
// covers exactly what each shader's PC block declares. Mismatched per-stage block
// sizes pass on hardware drivers but llvmpipe / Mesa validates more strictly and
// can SEGV inside the shader compiler when the actual push range exceeds the
// declared block on any stage referenced by the push call. See CI segfault on
// ubuntu-latest VkRendererPrimitiveTests.DrawEllipse_RingStroke.
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aLocalPos;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float innerRadius; } pc;
layout(location = 0) out vec2 vLocal;
void main() {
    gl_Position = pc.proj * vec4(aPos, 0.0, 1.0);
    vLocal = aLocalPos;
}
