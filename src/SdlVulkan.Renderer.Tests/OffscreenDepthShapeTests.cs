using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// An offscreen target allocates its depth image only once something on it draws a mesh: it starts on the
/// depth-less variant of the render-pass shape, and a mesh region takes it to the depth shape, up front
/// (<see cref="VulkanContext.RequireMeshDepth"/>) or on the frame after the first
/// <see cref="VkRenderer.BeginMeshRegion"/> that found none.
/// </summary>
/// <remarks>
/// <para>Own contexts, in the offscreen collection so they never run beside another GPU test: the shared
/// fixture's context is taken to the depth shape by <see cref="MeshRegionDepthTests"/> and keeps it, so what
/// it has depends on which class ran first.</para>
/// <para>The depth image is read through <c>OffscreenDepthImage</c>, a null handle being the whole claim:
/// the saving is the allocation, and 2D output must not change with it, which the identity test pins on one
/// context drawn both ways.</para>
/// </remarks>
[Collection("OffscreenGpu")]
public sealed class OffscreenDepthShapeTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Centre = (int)Size / 2;

    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 Red = new(255, 0, 0, 255);
    private static readonly RGBAColor32 Blue = new(0, 0, 255, 255);
    private static readonly RGBAColor32 Ink = new(230, 230, 235, 255);
    private static readonly RGBAColor32 Accent = new(80, 160, 240, 255);
    private static readonly Vector3 Light = new(0f, 0f, 1f);

    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

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

    private static unsafe VulkanContext? NewContext(VkSampleCountFlags samples, bool meshDepth = false)
    {
        try
        {
            vkInitialize().CheckResult();
            VkApplicationInfo appInfo = new() { apiVersion = SdlVulkanWindow.InstanceApiVersion() };
            VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, Size, Size, msaaSamples: samples, meshDepth: meshDepth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] Frame(VkRenderer renderer, VulkanContext ctx, Action<VkRenderer> draw)
    {
        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return ctx.ReadbackOffscreenRgba();
    }

    // What a viewer's chrome and page content are made of, minus text (which needs the atlas to settle):
    // boxes, a rounded box, an ellipse, lines, and triangles that take their edges from the sample count.
    private static void Scene(VkRenderer r)
    {
        r.FillRectangle(new RectInt(new PointInt(60, 12), new PointInt(4, 4)), Accent);
        r.FillRoundedRectangle(new RectInt(new PointInt(40, 40), new PointInt(8, 30)), Ink, 6f);
        r.FillEllipse(new RectInt(new PointInt(60, 60), new PointInt(30, 34)), Accent);
        r.DrawLine(6, 8, 56, 30, Ink, 2);
        r.DrawLine(8.5f, 50.25f, 30.75f, 58.5f, Ink, 1);
        r.DrawTriangles([4f, 4f, 58f, 20f, 10f, 24f], Red);
    }

    private static int ChannelRed(byte[] rgba, int x, int y) => rgba[(y * (int)Size + x) * 4];

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void ATargetThatNeverOpensAMeshRegionHasNoDepthImage(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);

        ctx!.OffscreenDepthImage.ShouldBe(VkImage.Null, "nothing has asked for depth yet");
        for (var frame = 0; frame < 3; frame++)
            Frame(renderer, ctx, Scene);
        ctx.OffscreenHasDepth.ShouldBeFalse();
        ctx.OffscreenDepthImage.ShouldBe(VkImage.Null, "2D frames never need a depth image");

        ctx.ResizeOffscreen(Size / 2, Size / 2);
        ctx.OffscreenDepthImage.ShouldBe(VkImage.Null, "a resize keeps the depth-less shape");
        ctx.OffscreenHasDepth.ShouldBeFalse();
    }

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void AnExplicitOptInHasDepthFromTheStartAndAcrossAResize(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples, meshDepth: true);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");

        ctx!.OffscreenHasDepth.ShouldBeTrue();
        ctx.OffscreenDepthImage.ShouldNotBe(VkImage.Null);
        ctx.ResizeOffscreen(Size / 2, Size / 2);
        ctx.OffscreenDepthImage.ShouldNotBe(VkImage.Null, "a resize keeps the depth shape too");
    }

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void TwoDOutputIsPixelIdenticalWithAndWithoutDepth(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);

        var withoutDepth = Frame(renderer, ctx!, Scene);
        ctx!.OffscreenHasDepth.ShouldBeFalse();

        ctx.RequireMeshDepth();
        var withDepth = Frame(renderer, ctx, Scene);
        ctx.OffscreenHasDepth.ShouldBeTrue("the request lands at the next frame, which is the one just drawn");

        withoutDepth.Count(b => b != 0).ShouldBeGreaterThan(1000, "the scene has to draw for the comparison to mean anything");
        withDepth.ShouldBe(withoutDepth, "depth must not change a pixel of 2D drawing, which tests nothing");
    }

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void TheLazySwitchDrawsTheMeshOnTheFrameAfterTheFirstMeshRegion(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);

        var first = Frame(renderer, ctx!, r =>
        {
            r.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeFalse("there is no depth image to test against yet");
            r.DrawMesh(Near, Identity, Red, Light);
            r.EndMeshRegion();
        });
        ChannelRed(first, Centre, Centre).ShouldBe(0, "a region on a depth-less frame draws nothing");
        ctx!.OffscreenDepthImage.ShouldBe(VkImage.Null, "the frame in hand was recorded without depth");

        // The far quad is drawn last: the near one showing is the depth test, not draw order.
        var second = Frame(renderer, ctx, r =>
        {
            ctx.OffscreenHasDepth.ShouldBeTrue("the asking frame's request is honoured at this frame's begin");
            r.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            r.DrawMesh(Near, Identity, Red, Light);
            r.DrawMesh(Far, Identity, Blue, Light);
            r.EndMeshRegion();
        });
        ctx.OffscreenDepthImage.ShouldNotBe(VkImage.Null);
        ChannelRed(second, Centre, Centre).ShouldBe(255, "the near quad wins over the far one drawn after it");
    }

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void AnUpFrontRequestDrawsTheMeshOnTheFirstFrame(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);

        renderer.RequireMeshDepth();
        var rgba = Frame(renderer, ctx!, r =>
        {
            r.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            r.DrawMesh(Near, Identity, Red, Light);
            r.DrawMesh(Far, Identity, Blue, Light);
            r.EndMeshRegion();
        });
        ChannelRed(rgba, Centre, Centre).ShouldBe(255);
    }

    /// <summary>
    /// A target on the depth-less shape hosts a cached layer, which is on the depth-less shape too: the
    /// layer draws with the depth-less pipelines, the main pass blits it with the same ones, and neither
    /// target has a depth image.
    /// </summary>
    [Fact]
    public void ACachedLayerOnADepthLessTargetStillDraws()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(VkSampleCountFlags.Count4);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);
        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();
        renderer.OnPreRenderPass = _ =>
        {
            renderer.BeginCachedLayer(Size, Size, Black).ShouldBeTrue();
            renderer.FillRectangle(new RectInt(new PointInt(48, 48), new PointInt(16, 16)), Red);
            renderer.EndCachedLayer();
        };

        var rgba = Frame(renderer, ctx!, r =>
            r.DrawTexture(r.CachedLayerDescriptorSet(r.CachedLayerSlot), 0f, 0f, Size, Size));
        renderer.OnPreRenderPass = null;

        ctx!.OffscreenHasDepth.ShouldBeFalse();
        for (var slot = 0; slot < VulkanContext.MaxFramesInFlight; slot++)
            ctx.CachedLayerDepthImage(slot).ShouldBe(VkImage.Null, "a layer that draws no mesh has no depth image");
        ChannelRed(rgba, 32, 32).ShouldBe(255);
        ChannelRed(rgba, 4, 4).ShouldBe(0);
    }

    // A frame whose cached layer is recorded by `layer` and blitted over the whole target.
    private static byte[] LayerFrame(VkRenderer renderer, VulkanContext ctx, Action<VkRenderer> layer)
    {
        renderer.OnPreRenderPass = _ =>
        {
            renderer.BeginCachedLayer(Size, Size, Black).ShouldBeTrue();
            layer(renderer);
            renderer.EndCachedLayer();
        };
        try
        {
            return Frame(renderer, ctx, r =>
                r.DrawTexture(r.CachedLayerDescriptorSet(r.CachedLayerSlot), 0f, 0f, Size, Size));
        }
        finally
        {
            renderer.OnPreRenderPass = null;
        }
    }

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void ACachedLayerTakesDepthOnlyOnceAMeshRegionAsksForIt(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);
        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();

        // The layer's 2D content, before any mesh: no slot has depth, and what it draws is the baseline the
        // depth shape must reproduce.
        var baseline = new byte[VulkanContext.MaxFramesInFlight][];
        for (var frame = 0; frame < VulkanContext.MaxFramesInFlight; frame++)
            baseline[frame] = LayerFrame(renderer, ctx!, Scene);
        for (var slot = 0; slot < VulkanContext.MaxFramesInFlight; slot++)
            ctx!.CachedLayerDepthImage(slot).ShouldBe(VkImage.Null);
        baseline[0].Count(b => b != 0).ShouldBeGreaterThan(1000);
        renderer.TakeMeshDepthRedraw().ShouldBeFalse("nothing was refused");

        // A region inside the layer is refused, draws nothing, and is reported for a redraw.
        var refused = LayerFrame(renderer, ctx!, r =>
        {
            r.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeFalse("the layer has no depth image to test against yet");
            r.DrawMesh(Near, Identity, Red, Light);
            r.EndMeshRegion();
        });
        ChannelRed(refused, Centre, Centre).ShouldBe(0, "a region in a depth-less layer draws nothing");
        renderer.TakeMeshDepthRedraw().ShouldBeTrue("the refusal asks for another frame");
        renderer.TakeMeshDepthRedraw().ShouldBeFalse("and only once");
        ctx!.OffscreenHasDepth.ShouldBeFalse("the layer asked for depth, not the main target");

        // Each slot takes depth at its own turn; from then on the near quad wins over the far one drawn after.
        for (var frame = 0; frame < VulkanContext.MaxFramesInFlight; frame++)
        {
            var rgba = LayerFrame(renderer, ctx, r =>
            {
                r.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
                r.DrawMesh(Near, Identity, Red, Light);
                r.DrawMesh(Far, Identity, Blue, Light);
                r.EndMeshRegion();
            });
            ChannelRed(rgba, Centre, Centre).ShouldBe(255, "the near quad wins over the far one drawn after it");
        }
        for (var slot = 0; slot < VulkanContext.MaxFramesInFlight; slot++)
            ctx.CachedLayerDepthImage(slot).ShouldNotBe(VkImage.Null);
        ctx.OffscreenHasDepth.ShouldBeFalse("the main target never drew a mesh");

        // 2D drawing in a layer that has depth comes out as it did without.
        for (var frame = 0; frame < VulkanContext.MaxFramesInFlight; frame++)
            LayerFrame(renderer, ctx, Scene).ShouldBe(baseline[0], "depth must not change a pixel of 2D drawing");
    }

    [Fact]
    public void AnUpFrontRequestGivesACachedLayerDepthFromItsFirstPass()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(VkSampleCountFlags.Count4);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);
        renderer.RequireMeshDepth(MeshDepthTargets.CachedLayer);
        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();

        var rgba = LayerFrame(renderer, ctx!, r =>
        {
            r.BeginMeshRegion(0f, 0f, Size, Size).ShouldBeTrue();
            r.DrawMesh(Near, Identity, Red, Light);
            r.DrawMesh(Far, Identity, Blue, Light);
            r.EndMeshRegion();
        });
        ChannelRed(rgba, Centre, Centre).ShouldBe(255);
        ctx!.OffscreenHasDepth.ShouldBeFalse("only the layer was named");
    }

    // One thumbnail capture of CaptureSize squared, cleared to `clear`, recorded by `draw`; the RGBA that comes
    // back (a few frames later, when the capture's fence index is waited again).
    private const uint CaptureSize = 16;

    private static byte[] Capture(VkRenderer renderer, VulkanContext ctx, RGBAColor32 clear, Action<VkRenderer> draw)
    {
        var record = true;
        renderer.OnPreRenderPass = _ =>
        {
            if (!record) return;
            record = false;
            renderer.BeginThumbnailCapture(CaptureSize, CaptureSize, clear).ShouldBeTrue();
            draw(renderer);
            renderer.EndThumbnailCapture();
        };
        try
        {
            for (var frame = 0; frame < 8; frame++)
            {
                Frame(renderer, ctx, _ => { });
                if (renderer.TryGetThumbnailCapture(out var rgba, out _, out _)) return rgba;
            }
        }
        finally
        {
            renderer.OnPreRenderPass = null;
        }
        throw new Xunit.Sdk.XunitException("the capture never came back");
    }

    private static int CaptureRed(byte[] rgba, int x, int y) => rgba[(y * (int)CaptureSize + x) * 4];

    [Theory]
    [InlineData(VkSampleCountFlags.Count1)]
    [InlineData(VkSampleCountFlags.Count4)]
    public void AThumbnailCaptureTakesDepthOnlyOnceAMeshRegionAsksForIt(VkSampleCountFlags samples)
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = NewContext(samples);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Size, Size);
        renderer.EnsureThumbnailTarget(CaptureSize, CaptureSize).ShouldBeTrue();
        const int mid = (int)CaptureSize / 2;

        // 2D into the capture: no depth image, and the picture is the baseline for after the switch.
        var scene = (VkRenderer r) =>
        {
            r.FillRectangle(new RectInt(new PointInt(12, 12), new PointInt(2, 2)), Accent);
            r.FillEllipse(new RectInt(new PointInt(14, 8), new PointInt(2, 2)), Ink);
            r.DrawTriangles([1f, 1f, 15f, 4f, 3f, 14f], Red);
        };
        var baseline = Capture(renderer, ctx!, Black, scene);
        ctx!.ThumbnailDepthImage.ShouldBe(VkImage.Null, "a capture that draws no mesh has no depth image");
        baseline.Count(b => b != 0).ShouldBeGreaterThan(200);

        var refused = Capture(renderer, ctx, Black, r =>
        {
            r.BeginMeshRegion(0f, 0f, CaptureSize, CaptureSize).ShouldBeFalse("the capture has no depth image yet");
            r.DrawMesh(Near, Identity, Red, Light);
            r.EndMeshRegion();
        });
        CaptureRed(refused, mid, mid).ShouldBe(0, "a region in a depth-less capture draws nothing");
        renderer.TakeMeshDepthRedraw().ShouldBeTrue();
        ctx.ThumbnailDepthImage.ShouldBe(VkImage.Null, "the pass in hand was recorded without depth");

        var drawn = Capture(renderer, ctx, Black, r =>
        {
            r.BeginMeshRegion(0f, 0f, CaptureSize, CaptureSize).ShouldBeTrue();
            r.DrawMesh(Near, Identity, Red, Light);
            r.DrawMesh(Far, Identity, Blue, Light);
            r.EndMeshRegion();
        });
        ctx.ThumbnailDepthImage.ShouldNotBe(VkImage.Null);
        CaptureRed(drawn, mid, mid).ShouldBe(255, "the near quad wins over the far one drawn after it");
        ctx.OffscreenHasDepth.ShouldBeFalse("the main target never drew a mesh");

        Capture(renderer, ctx, Black, scene).ShouldBe(baseline, "depth must not change a pixel of 2D drawing");
    }

    /// <summary>
    /// The shape changes under the validation layer, at 4x MSAA with the main pass alternating between one
    /// sample and four: a framebuffer built for the wrong shape, a pipeline drawn in a pass of the other
    /// shape or sample count, or a switch that frees what a frame in flight still names would each show
    /// as an error here and nowhere else.
    /// </summary>
    [Fact]
    public async Task ChangingShapeMidRunIsSilentUnderTheValidationLayer()
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
            var run = Task.Run(() =>
            {
                using var renderer = new VkRenderer(ctx!, Size, Size);
                var frames = 2 * VulkanContext.MaxFramesInFlight;
                for (var frame = 0; frame < 3 * frames; frame++)
                {
                    // 2D only for a while, then a region that asks for depth (and loses its mesh), then
                    // meshes; single-sampled and multisampled frames alternate throughout. The wait is
                    // only on frames that change shape, so the rest overlap in flight as they do live.
                    renderer.SingleSampleMainPass = frame % 2 == 0;
                    renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
                    Scene(renderer);
                    if (frame >= frames && renderer.BeginMeshRegion(0f, Centre, Size, Centre))
                    {
                        renderer.DrawMesh(Far, Identity, Blue, Light);
                        renderer.DrawMesh(Near, Identity, Red, Light);
                        renderer.EndMeshRegion();
                    }
                    renderer.EndOffscreenFrame();
                    if (frame == frames - 1 || frame == frames) ctx!.WaitOffscreenFrameComplete();
                    // Through the renderer, not the context: it resizes both, so the damage region the next
                    // frame starts with is the new target's and a clear stays inside the render area.
                    if (frame == 2 * frames) renderer.ResizeOffscreen(Size - 8, Size - 8);
                }
                ctx!.WaitOffscreenFrameComplete();
                ctx.OffscreenHasDepth.ShouldBeTrue();
            }, TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)) == run;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: the frames did not finish within 60s, possible GPU wedge. " +
                            $"Validation messages so far:\n{ValidatedOffscreen.DumpMessages()}");
            }
            await run;

            var offences = ValidatedOffscreen.Messages
                .Where(m => ValidatedOffscreen.IsError(m) || ValidatedOffscreen.IsSyncHazard(m))
                .ToArray();
            Assert.True(offences.Length == 0,
                $"the validation layer reported {offences.Length} error(s)/hazard(s) while the target changed shape:\n" +
                string.Join("\n\n", offences));
        }
        finally
        {
            if (!wedged)
                ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }
}
