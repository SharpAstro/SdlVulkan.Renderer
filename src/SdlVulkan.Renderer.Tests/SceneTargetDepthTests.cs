using System.Numerics;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The scene target's contract: visibility is decided by GEOMETRY, not by draw order.
/// </summary>
/// <remarks>
/// <para>Every other pass in this renderer is painter's-order — the last draw wins — so the one
/// property worth pinning is the one that makes this pass different. Both tests draw the far quad
/// LAST. With the depth attachment doing its job the near quad still shows; with depth disabled the
/// later draw covers it, which is exactly the failure a test written the obvious way (near drawn
/// last) would pass straight through.</para>
/// <para><b>Mutation-checked:</b> setting <c>depthTestEnable = false</c> in
/// <see cref="VkMeshPipeline"/> turns the first test red — the centre reads the far quad's blue
/// instead of the near quad's red — and leaves the second one green, since with no depth test the
/// draw order it asserts is the only thing left deciding. That asymmetry is the point: the second
/// test alone proves nothing.</para>
/// </remarks>
[Collection("OffscreenGpu")]
public sealed class SceneTargetDepthTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Centre = (int)Size / 2;

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 Red = new(255, 0, 0, 255);
    private static readonly RGBAColor32 Blue = new(0, 0, 255, 255);

    /// <summary>
    /// Identity mvp: clip space directly, so a quad's Z IS its depth and the arithmetic under test is
    /// the depth comparison rather than a projection.
    /// </summary>
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    // A full-clip-space quad at a fixed depth, normal facing the camera. Two triangles, six vertices,
    // interleaved position(3) + normal(3).
    private static float[] Quad(float z) =>
    [
        -1f, -1f, z,  0f, 0f, 1f,
         1f, -1f, z,  0f, 0f, 1f,
         1f,  1f, z,  0f, 0f, 1f,

        -1f, -1f, z,  0f, 0f, 1f,
         1f,  1f, z,  0f, 0f, 1f,
        -1f,  1f, z,  0f, 0f, 1f,
    ];

    [Fact]
    public void ANearerSurfaceSurvivesAFartherOneDrawnAfterIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var pixel = RenderTwoQuads(ctx, nearFirst: true);
        pixel.ShouldNotBeNull("the device offers no usable depth format");
        Dominant(pixel!.Value).ShouldBe('R',
            "the near quad must win over the far quad drawn after it — that is the depth test");
    }

    /// <summary>
    /// The converse, which fixes the meaning of the first: with the FAR quad drawn first the near one
    /// covers it, so red is not simply what this pass always produces.
    /// </summary>
    [Fact]
    public void ANearerSurfaceDrawnLastAlsoWins()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var pixel = RenderTwoQuads(ctx, nearFirst: false);
        pixel.ShouldNotBeNull("the device offers no usable depth format");
        Dominant(pixel!.Value).ShouldBe('R');
    }

    [Fact]
    public void CapacityIsFixedAndASubRectMayNotExceedIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);

        if (!renderer.EnsureSceneTargets(Size, Size))
        {
            Assert.Skip("no usable depth format on this device");
            return;
        }
        renderer.SceneTargetReady.ShouldBeTrue();
        renderer.SceneTargetSlotCount.ShouldBe(VulkanContext.MaxFramesInFlight,
            "one target per frame in flight, or a re-render races the frame still sampling it");

        renderer.EnsureSceneTargets(Size / 2, Size / 2).ShouldBeTrue();
        renderer.EnsureSceneTargets(Size * 2, Size).ShouldBeFalse(
            "growing capacity needs ReleaseSceneTargets first, which drains before freeing");

        var refused = false;
        renderer.OnPreRenderPass += _ => refused = !renderer.BeginScene(Size * 2, Size, Black);
        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        refused.ShouldBeTrue("an oversize render area must be refused, not begun against a smaller framebuffer");

        renderer.ReleaseSceneTargets();
        renderer.SceneTargetReady.ShouldBeFalse();
    }

    /// <summary>
    /// Renders a red quad at z=0.25 and a blue one at z=0.75 into the scene target, blits the result
    /// over the whole frame, and returns the centre pixel. Null if the device has no depth format.
    /// </summary>
    private static (byte R, byte G, byte B)? RenderTwoQuads(VulkanContext ctx, bool nearFirst)
    {
        ctx.ResizeOffscreen(Size, Size);
        // The offscreen context belongs to the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Size, Size);

        if (!renderer.EnsureSceneTargets(Size, Size)) return null;

        var near = Quad(0.25f);
        var far = Quad(0.75f);
        var light = new Vector3(0f, 0f, 1f);

        // Recorded before the main render pass opens, because render passes cannot nest.
        renderer.OnPreRenderPass += _ =>
        {
            if (!renderer.BeginScene(Size, Size, Black)) return;
            if (nearFirst)
            {
                renderer.DrawMesh(near, Identity, Red, light);
                renderer.DrawMesh(far, Identity, Blue, light);
            }
            else
            {
                renderer.DrawMesh(far, Identity, Blue, light);
                renderer.DrawMesh(near, Identity, Red, light);
            }
            renderer.EndScene();
        };

        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        renderer.DrawTexture(renderer.SceneTargetDescriptorSet(renderer.SceneTargetSlot), 0f, 0f, Size, Size);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        rgba.Length.ShouldBe((int)(Size * Size * 4));
        var i = (Centre * (int)Size + Centre) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    /// <summary>
    /// Which channel the pixel is made of. The shader shades by a lambert term, so neither quad comes
    /// back at full 255 and an exact comparison would pin the lighting maths rather than the depth
    /// test; which colour dominates is the question actually being asked.
    /// </summary>
    private static char Dominant((byte R, byte G, byte B) p)
        => p.R > p.B ? 'R' : p.B > p.R ? 'B' : '?';
}
