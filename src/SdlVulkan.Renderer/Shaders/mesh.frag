#version 450
layout(push_constant) uniform PC {
    mat4 mvp;
    vec4 color;
    vec4 lightDir;
} pc;
layout(location = 0) in vec3 vNormal;
layout(location = 0) out vec4 FragColor;
void main() {
    // Two-sided lambert with an ambient floor. abs() rather than max(,0) because tessellated CAD
    // meshes routinely disagree about winding between parts, and a back-facing normal should light
    // the surface rather than turn it black -- culling is off for the same reason.
    vec3 n = normalize(vNormal);
    float lambert = abs(dot(n, normalize(pc.lightDir.xyz)));
    float shade = 0.25 + 0.75 * lambert;
    FragColor = vec4(pc.color.rgb * shade, pc.color.a);
}
