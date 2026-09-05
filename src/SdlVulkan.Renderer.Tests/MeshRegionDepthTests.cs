using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The mesh region's contract: INSIDE it visibility is decided by geometry, and OUTSIDE it the frame
/// goes on being painter's-order — a mesh is more draws in the same pass, not a picture composited in.
/// </summary>
/// <remarks>
/// <para>Every other draw in this renderer is painter's-order — the last draw wins — so the first two
/// tests pin the one property that makes a region different, and both draw the far quad LAST. With the
/// depth attachment doing its job the near quad still shows; with depth disabled the later draw covers
/// it, which is exactly the failure a test written the obvious way (near drawn last) would pass
/// straight through. <b>Mutation-checked:</b> <c>depthTestEnable = false</c> in <see cref="VkMeshPipeline"/>
/// turns the first test red and leaves the second green, so the second alone proves nothing.</para>
/// <para>The next two pin the boundary: a 2D fill after the region covers the model, and a model
/// covers a fill before it. That is what "inline" buys — no blit, no intermediate, the model sits in
/// the frame's own order — and it depends on the 2D pipelines carrying a depth-stencil state that
/// TESTS NOTHING, which is the one thing every pre-baked pipeline had to gain for the pass to carry a
/// depth attachment at all.</para>
/// <para>Then the clear: a second region on the same rect must not be tested against the first's
/// depth. Removing the <c>vkCmdClearAttachments</c> from <see cref="VkRenderer.BeginMeshRegion"/> turns
/// that test red and nothing else, since a single region is cleared by the pass anyway.</para>
/// <para>The rect tests pin the mapping — clip [-1,1]² lands on the rect and nowhere else, including a
/// rect half outside the frame, which is why the mapping is folded into the matrix rather than set as
/// a viewport. And the cached-layer test runs the same bracket inside the layer pass, which is the
/// pass a document viewer actually draws its pages in: every pass has the attachment, not just the
/// swapchain's.</para>
/// </remarks>
[Collection("OffscreenGpu")]
public sealed class MeshRegionDepthTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Centre = (int)Size / 2;
    private const int Quarter = (int)Size / 4;
    private const int ThreeQuarters = 3 * (int)Size / 4;

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 Red = new(255, 0, 0, 255);
    private static readonly RGBAColor32 Green = new(0, 255, 0, 255);
    private static readonly RGBAColor32 Blue = new(0, 0, 255, 255);
    private static readonly Vector3 Light = new(0f, 0f, 1f);

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
    // interleaved position(3) + normal(3). With the normal along the light the lambert term is 1, so
    // the quad comes back in its exact colour.
    private static float[] Quad(float z) =>
    [
        -1f, -1f, z,  0f, 0f, 1f,
         1f, -1f, z,  0f, 0f, 1f,
         1f,  1f, z,  0f, 0f, 1f,

        -1f, -1f, z,  0f, 0f, 1f,
         1f,  1f, z,  0f, 0f, 1f,
        -1f,  1f, z,  0f, 0f, 1f,
    ];

    private static readonly float[] Near = Quad(0.25f);
    private static readonly float[] Far = Quad(0.75f);

    [Fact]
    public void ANearerSurfaceSurvivesAFartherOneDrawnAfterIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var rgba = RenderFrame(ctx, renderer =>
        {
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.DrawMesh(Far, Identity, Blue, Light);
            renderer.EndMeshRegion();
        });
        ChannelsAt(rgba, Centre, Centre).ShouldBe((255, 0, 0),
            "the near quad must win over the far quad drawn after it — that is the depth test");
    }

    /// <summary>
    /// The converse, which fixes the meaning of the first: with the FAR quad drawn first the near one
    /// covers it, so red is not simply what a region always produces.
    /// </summary>
    [Fact]
    public void ANearerSurfaceDrawnLastAlsoWins()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var rgba = RenderFrame(ctx, renderer =>
        {
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            renderer.DrawMesh(Far, Identity, Blue, Light);
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.EndMeshRegion();
        });
        ChannelsAt(rgba, Centre, Centre).ShouldBe((255, 0, 0));
    }

    [Fact]
    public void ATwoDDrawAfterTheRegionPaintsOverTheModel()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var rgba = RenderFrame(ctx, renderer =>
        {
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.EndMeshRegion();
            // Nearer than anything, as far as depth is concerned — and depth must not be consulted.
            renderer.FillRectangle(new RectInt(new PointInt((int)Size, (int)Size), new PointInt(0, 0)), Green);
        });
        ChannelsAt(rgba, Centre, Centre).ShouldBe((0, 255, 0),
            "a 2D draw is painter's-order: it covers the model whatever depth the model wrote");
    }

    [Fact]
    public void AModelPaintsOverWhatWasDrawnBeforeIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var rgba = RenderFrame(ctx, renderer =>
        {
            renderer.FillRectangle(new RectInt(new PointInt((int)Size, (int)Size), new PointInt(0, 0)), Green);
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            // The FAR quad, deliberately: it still wins against the region's cleared depth, and the
            // fill before it wrote no depth for it to lose against.
            renderer.DrawMesh(Far, Identity, Blue, Light);
            renderer.EndMeshRegion();
        });
        ChannelsAt(rgba, Centre, Centre).ShouldBe((0, 0, 255),
            "the model goes over what was drawn before it; a 2D draw writes no depth");
    }

    [Fact]
    public void EachRegionClearsTheDepthUnderIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var rgba = RenderFrame(ctx, renderer =>
        {
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.EndMeshRegion();

            // A second model on the same pixels. Without the per-region clear its far quad would be
            // tested against the first model's near depth and lose everywhere.
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            renderer.DrawMesh(Far, Identity, Blue, Light);
            renderer.EndMeshRegion();
        });
        ChannelsAt(rgba, Centre, Centre).ShouldBe((0, 0, 255),
            "opening a region clears the depth under it, so each model's depth is its own");
    }

    [Fact]
    public void TheRegionRectPlacesAndConfinesTheModel()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        // The left half. A full-clip-space quad must fill exactly that half.
        var rgba = RenderFrame(ctx, renderer =>
        {
            renderer.BeginMeshRegion(0f, 0f, Size / 2f, Size).ShouldBeTrue();
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.EndMeshRegion();
        });
        ChannelsAt(rgba, Quarter, Centre).ShouldBe((255, 0, 0), "inside the region");
        ChannelsAt(rgba, Centre - 1, Centre).ShouldBe((255, 0, 0), "the region's last column");
        ChannelsAt(rgba, Centre, Centre).ShouldBe((0, 0, 0), "the first column past the region");
        ChannelsAt(rgba, ThreeQuarters, Centre).ShouldBe((0, 0, 0), "outside the region");
    }

    [Fact]
    public void ARegionMayHangOffTheFrameAndIsRefusedOnlyWhenItMissesIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var rgba = RenderFrame(ctx, renderer =>
        {
            // Entirely off the frame, and degenerate: nothing to clear or clip, so nothing opens. The
            // caller reads the false and draws no meshes — which the EndMeshRegion after a refusal
            // must tolerate too.
            renderer.BeginMeshRegion(Size + 10f, 0f, 10f, 10f).ShouldBeFalse("a rect off the frame");
            renderer.EndMeshRegion();
            renderer.BeginMeshRegion(0f, 0f, 0f, Size).ShouldBeFalse("an empty rect");
            renderer.EndMeshRegion();

            // Half off the left edge: the visible half of the quad is the frame's left half. The
            // clear and the scissor are clamped to the frame; the mapping is not, or the quad would be
            // squashed into the visible part instead of cut by it.
            renderer.BeginMeshRegion(-(Size / 2f), 0f, Size, Size).ShouldBeTrue("a rect half on the frame");
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.EndMeshRegion();
        });
        ChannelsAt(rgba, Quarter, Centre).ShouldBe((255, 0, 0), "the on-frame half of the region");
        ChannelsAt(rgba, ThreeQuarters, Centre).ShouldBe((0, 0, 0), "past the region's right edge");
    }

    [Fact]
    public void ARegionInsideTheCachedLayerPassIsDepthTestedToo()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);
        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();

        // The layer is recorded before the main pass opens, exactly as a viewer records its page
        // content; the model goes into it like any other page content would.
        renderer.OnPreRenderPass += _ =>
        {
            renderer.BeginCachedLayer(Size, Size, Black).ShouldBeTrue();
            renderer.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            renderer.DrawMesh(Near, Identity, Red, Light);
            renderer.DrawMesh(Far, Identity, Blue, Light);
            renderer.EndMeshRegion();
            renderer.EndCachedLayer();
        };

        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        renderer.DrawTexture(renderer.CachedLayerDescriptorSet(renderer.CachedLayerSlot), 0f, 0f, Size, Size);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        ChannelsAt(rgba, Centre, Centre).ShouldBe((255, 0, 0),
            "the cached layer's pass carries its own depth attachment, so the near quad wins there too");

        // The layer targets belong to the shared fixture context and outlive this renderer, and a slot
        // rendered here stays marked rendered: CachedLayerTests asserts on a slot it has NOT rendered,
        // and read this test's leftovers as its own on the lane that happened to run this one first.
        renderer.ReleaseCachedLayerTargets();
    }

    /// <summary>
    /// The same frame shape a viewer records — a fill, a model, a fill over it, and the model again
    /// inside a cached layer that is then blitted — under the validation layer, at 4x MSAA, over more
    /// frames than are in flight. This is the test that sees what a driver does not report: a pipeline
    /// bound into a pass it is not compatible with, a missing depth-stencil state, a clear rect outside
    /// the render area, a depth write unordered against the previous frame's. MSAA on purpose — it is
    /// the three-attachment shape, and the resolve the single-sample pass never performs.
    /// </summary>
    [Fact]
    public async Task AFrameWithMeshesIsSilentUnderTheValidationLayerAtFourTimesMsaa()
    {
        if (!ValidatedOffscreen.TryCreate(Size, Size, out var ctx, out var messenger, out var api, out var skip,
                VkSampleCountFlags.Count4))
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        var wedged = false;
        try
        {
            var run = Task.Run(ValidatedSequence(ctx!), TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(run, Task.Delay(Deadman, TestContext.Current.CancellationToken)) == run;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: the sequence did not finish within {Deadman.TotalSeconds:0}s, possible GPU wedge. " +
                            $"Validation messages so far:\n{ValidatedOffscreen.DumpMessages()}");
            }
            await run;

            var offences = ValidatedOffscreen.Messages
                .Where(m => ValidatedOffscreen.IsError(m) || ValidatedOffscreen.IsSyncHazard(m))
                .ToArray();
            Assert.True(offences.Length == 0,
                $"the validation layer reported {offences.Length} error(s)/hazard(s) over frames drawing meshes inline:\n" +
                string.Join("\n\n", offences));
        }
        finally
        {
            if (!wedged)
            {
                ValidatedOffscreen.Destroy(ctx, messenger, api);
            }
        }
    }

    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(60);

    private static Action ValidatedSequence(VulkanContext ctx) => () =>
    {
        using var renderer = new VkRenderer(ctx, Size, Size);
        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();
        renderer.OnPreRenderPass += _ =>
        {
            if (!renderer.BeginCachedLayer(Size, Size, Black)) return;
            if (renderer.BeginMeshRegion(Quarter, Quarter, Centre, Centre))
            {
                renderer.DrawMesh(Near, Identity, Red, Light);
                renderer.EndMeshRegion();
            }
            renderer.EndCachedLayer();
        };

        for (var frame = 0; frame < 2 * VulkanContext.MaxFramesInFlight + 1; frame++)
        {
            if (!renderer.BeginOffscreenFrame(Black)) continue;
            renderer.DrawTexture(renderer.CachedLayerDescriptorSet(renderer.CachedLayerSlot), 0f, 0f, Size, Size);
            renderer.FillRectangle(new RectInt(new PointInt((int)Size, Quarter), new PointInt(0, 0)), Green);
            if (renderer.BeginMeshRegion(0f, Centre, Size, Centre))
            {
                renderer.DrawMesh(Far, Identity, Blue, Light);
                renderer.DrawMesh(Near, Identity, Red, Light);
                renderer.EndMeshRegion();
            }
            renderer.FillRectangle(new RectInt(new PointInt((int)Size, (int)Size), new PointInt(0, ThreeQuarters)), Green);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }
    };

    /// <summary>One offscreen frame of <see cref="Size"/>², cleared to black, with <paramref name="draw"/>
    /// recorded into its main pass; returns the readback.</summary>
    private static byte[] RenderFrame(VulkanContext ctx, Action<VkRenderer> draw)
    {
        ctx.ResizeOffscreen(Size, Size);
        // The offscreen context belongs to the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Size, Size);

        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        rgba.Length.ShouldBe((int)(Size * Size * 4));
        return rgba;
    }

    private static (int R, int G, int B) ChannelsAt(byte[] rgba, int x, int y)
    {
        var i = (y * (int)Size + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }
}
