#version 450
layout(push_constant) uniform PC {
    mat4 mvp;
    vec4 color;
    vec4 lightDir;
    vec4 specular;  // rgb = highlight colour, a = Blinn-Phong exponent; a <= 0 draws no highlight
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
    vec3 rgb = pc.color.rgb * shade;
    // Blinn-Phong highlight for a light that travels with the eye (a headlamp): the half vector between
    // the light and the eye is then the light direction itself, so n.h is the lambert term. Added, not
    // multiplied, so a dark surface still catches the light; a light set apart from the eye wants the
    // eye direction pushed too.
    if (pc.specular.a > 0.0)
        rgb += pc.specular.rgb * pow(lambert, pc.specular.a);
    FragColor = vec4(min(rgb, vec3(1.0)), pc.color.a);
}
