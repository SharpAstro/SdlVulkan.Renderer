#version 450
// Depth-tested lit mesh, drawn inline in the same render pass as everything else (VkMeshPipeline,
// VkRenderer.DrawMesh). Unlike every other pipeline here the position is 3D and the pass's depth
// attachment decides visibility, so nothing about draw order matters among the mesh's triangles.
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(push_constant) uniform PC {
    mat4 mvp;
    vec4 color;
    vec4 lightDir;  // xyz = direction TO the light, world space, expected normalised; w unused
} pc;
layout(location = 0) out vec3 vNormal;
void main() {
    // The normal is passed through in model space and lit in model space, which is correct while the
    // model matrix has no non-uniform scale. A normal matrix belongs here the day one does.
    vNormal = aNormal;
    gl_Position = pc.mvp * vec4(aPos, 1.0);
}
