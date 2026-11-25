namespace SdlVulkan.Renderer;

/// <summary>
/// One quad of <see cref="VkRenderer.DrawTexturedQuadRegions"/>: four corners in screen pixels and the
/// texture rectangle they map. The corners mean what they mean for
/// <see cref="VkRenderer.DrawTexturedQuadRegion"/>: (X0,Y0) the image origin at (U0,V0), (X1,Y1) the right
/// edge at (U1,V0), (X2,Y2) the bottom edge at (U0,V1), (X3,Y3) the far corner at (U1,V1).
/// </summary>
public readonly record struct TexturedQuad(
    float X0, float Y0, float X1, float Y1, float X2, float Y2, float X3, float Y3,
    float U0, float V0, float U1, float V1);
