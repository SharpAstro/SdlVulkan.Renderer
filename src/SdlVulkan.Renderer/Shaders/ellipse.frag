#version 450
// Fragment-side: SINGLE-discard combined predicate. The original had two
// sequential `if (...) discard;` statements; Mesa llvmpipe is known to mis-
// compile a conditional-on-uniform second discard in some MSAA paths and the
// CI dump points squarely at this test. Folding into one discard keeps the
// logic identical -- pc.innerRadius=0 in fill mode makes innerRadius*innerRadius
// exactly 0, and `dist >= 0` always holds for a dot-product distance, so the
// inner-radius branch is unreachable in fill mode without an explicit guard.
layout(location = 0) in vec2 vLocal;
layout(push_constant) uniform PC { mat4 proj; vec4 color; float innerRadius; } pc;
layout(location = 0) out vec4 FragColor;
void main() {
    float dist = dot(vLocal, vLocal);
    float innerSq = pc.innerRadius * pc.innerRadius;
    // Outside the unit disc OR inside the inner ring -> discard. Single
    // statement avoids the llvmpipe double-discard-with-MSAA bug class.
    if (dist > 1.0 || dist < innerSq) discard;
    FragColor = pc.color;
}
