using System;
using System.IO;
using System.Linq;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// A main pass the consumer asks to run single-sampled (<see cref="VkRenderer.SingleSampleMainPass"/>) on
/// a device that multisamples: the frame of chrome over a blitted layer it is for comes out the same as at
/// 4x, geometry that takes its edges from MSAA does not, and the cached layer recorded before it stays
/// multisampled.
/// <para>Own context at 4x (the shared fixture is single-sampled, where the switch has nothing to do), in
/// the offscreen collection so it never runs beside another GPU test.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class SingleSampleMainPassTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 160;
    private const uint Height = 96;

    private static readonly RGBAColor32 Backdrop = new(20, 24, 32, 255);
    private static readonly RGBAColor32 Ink = new(230, 230, 235, 255);
    private static readonly RGBAColor32 Accent = new(80, 160, 240, 255);

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static unsafe VulkanContext? MultisampledContext(VkSampleCountFlags samples = VkSampleCountFlags.Count4)
    {
        try
        {
            vkInitialize().CheckResult();
            VkApplicationInfo appInfo = new() { apiVersion = SdlVulkanWindow.InstanceApiVersion() };
            VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, Width, Height, msaaSamples: samples);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [Fact]
    public void ChromeComesOutTheSameSingleSampledAsAtFourTimes()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = MultisampledContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Width, Height);
        ctx!.SingleSampleMainPassAvailable.ShouldBeTrue();

        // What a frame of chrome is made of: boxes, a rounded box, an ellipse, lines at angles (icons are
        // drawn with them), and text.
        void Chrome(VkRenderer r)
        {
            r.FillRectangle(new RectInt(new PointInt(150, 12), new PointInt(10, 4)), Accent);
            r.FillRoundedRectangle(new RectInt(new PointInt(70, 40), new PointInt(10, 18)), Ink, 6f);
            r.FillEllipse(new RectInt(new PointInt(108, 44), new PointInt(80, 16)), Accent);
            r.DrawLine(120, 20, 150, 44, Ink, 2);
            r.DrawLine(124.5f, 52.25f, 152.75f, 60.5f, Ink, 1);
            r.DrawLine(130, 20, 130, 46, Accent, 3);
            r.DrawText("Single page 24%", FontPath, 14f,
                Ink, new RectInt(new PointInt((int)Width, (int)Height), new PointInt(0, 60)), TextAlign.Center, TextAlign.Center);
        }

        var multisampled = Settled(renderer, ctx, single: false, Chrome);
        ctx.LastFrameSingleSampled.ShouldBeFalse();
        var single = Settled(renderer, ctx, single: true, Chrome);
        ctx.LastFrameSingleSampled.ShouldBeTrue();

        Lit(multisampled).ShouldBeGreaterThan(400, "the frame has to draw something for the comparison to mean anything");
        MaxDifference(single, multisampled).ShouldBeLessThanOrEqualTo(2,
            "every primitive here antialiases in its shader, so the sample count must not show");
    }

    [Fact]
    public void TrianglesTakeTheirEdgesFromTheSampleCount()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = MultisampledContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Width, Height);

        // A thin sliver of a triangle: at 4x its edge pixels take partial coverage, at one sample each
        // pixel is in or out. If this came out the same, the "single-sampled" pass would not be one.
        ReadOnlySpan<float> sliver = [10f, 10f, 150f, 30f, 10f, 14f];
        var verts = sliver.ToArray();
        void Sliver(VkRenderer r) => r.DrawTriangles(verts, Ink);

        var multisampled = Settled(renderer, ctx!, single: false, Sliver);
        var single = Settled(renderer, ctx!, single: true, Sliver);
        Intermediate(multisampled).ShouldBeGreaterThan(Intermediate(single) * 3,
            "the 4x frame antialiases the sliver's edges and the single-sampled one does not");
    }

    [Fact]
    public void ALayerDrawnAtFourTimesBlitsTheSameIntoASingleSampledMainPass()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = MultisampledContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Width, Height);
        renderer.EnsureCachedLayerTargets(Width, Height).ShouldBeTrue();

        // The layer is content: triangles, which need the 4x it is drawn at. The main pass only blits it.
        ReadOnlySpan<float> fan = [8f, 8f, 150f, 20f, 40f, 88f, 150f, 20f, 120f, 90f, 40f, 88f];
        var verts = fan.ToArray();
        renderer.OnPreRenderPass = _ =>
        {
            renderer.BeginCachedLayer(Width, Height, Backdrop).ShouldBeTrue();
            renderer.DrawTriangles(verts, Accent);
            renderer.EndCachedLayer();
        };
        void Blit(VkRenderer r) => r.DrawTexture(r.CachedLayerDescriptorSet(r.CachedLayerSlot), 0, 0, Width, Height);

        var multisampled = Settled(renderer, ctx!, single: false, Blit);
        var single = Settled(renderer, ctx!, single: true, Blit);
        renderer.OnPreRenderPass = null;

        Intermediate(single).ShouldBeGreaterThan(0, "the layer's edges are antialiased, so its blit carries them");
        MaxDifference(single, multisampled).ShouldBeLessThanOrEqualTo(1,
            "the layer pass keeps its samples whatever the main pass does, and a blit is the same at either count");
    }

    [Fact]
    public void ASingleSampledDeviceHasNothingToSwitch()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = MultisampledContext(VkSampleCountFlags.Count1);
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Width, Height);

        ctx!.SingleSampleMainPassAvailable.ShouldBeFalse();
        renderer.SingleSampleMainPass = true;
        renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
        renderer.FillRectangle(new RectInt(new PointInt(20, 20), new PointInt(0, 0)), Ink);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        ctx.LastFrameSingleSampled.ShouldBeFalse();
    }

    [Fact]
    public async System.Threading.Tasks.Task SwitchingTheMainPassFrameByFrameIsSilentUnderTheValidationLayer()
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip,
                msaaSamples: VkSampleCountFlags.Count4))
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        var wedged = false;
        try
        {
            // A layer at 4x every frame, blitted into a main pass that alternates between one sample and
            // four, with a line, a mesh-free mix of the primitives and a resize between: a pipeline drawn in
            // a pass of the other sample count reads as VUID-vkCmdDraw-renderPass-02684.
            var run = System.Threading.Tasks.Task.Run(() =>
            {
                using var renderer = new VkRenderer(ctx!, Width, Height);
                renderer.EnsureCachedLayerTargets(Width, Height).ShouldBeTrue();
                WaitUntilSingleSampleReady(renderer);
                renderer.OnPreRenderPass = _ =>
                {
                    renderer.BeginCachedLayer(Width, Height, Backdrop).ShouldBeTrue();
                    renderer.DrawTriangles([8f, 8f, 150f, 20f, 40f, 88f], Accent);
                    renderer.EndCachedLayer();
                };
                for (var frame = 0; frame < 3 * VulkanContext.MaxFramesInFlight; frame++)
                {
                    renderer.SingleSampleMainPass = frame % 2 == 0;
                    renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
                    renderer.DrawTexture(renderer.CachedLayerDescriptorSet(renderer.CachedLayerSlot), 0, 0, Width, Height);
                    renderer.FillRoundedRectangle(new RectInt(new PointInt(70, 40), new PointInt(10, 18)), Ink, 6f);
                    renderer.DrawLine(120, 20, 150, 44, Ink, 2);
                    renderer.FillEllipse(new RectInt(new PointInt(108, 44), new PointInt(80, 16)), Accent);
                    renderer.EndOffscreenFrame();
                    if (frame == VulkanContext.MaxFramesInFlight)
                    {
                        ctx!.WaitOffscreenFrameComplete();
                        ctx.ResizeOffscreen(Width - 8, Height - 8);
                    }
                }
                ctx!.WaitOffscreenFrameComplete();
                renderer.OnPreRenderPass = null;
            }, TestContext.Current.CancellationToken);
            var finished = await System.Threading.Tasks.Task.WhenAny(run,
                System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken)) == run;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: the frames did not finish within 60s, possible GPU wedge. " +
                            $"Validation messages so far:\n{ValidatedOffscreen.DumpMessages()}");
            }
            await run;

            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0,
                $"the validation layer reported {errors.Length} error(s) while the main pass switched sample count:\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            if (!wedged)
                ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }

    // Draws the frame until the glyph atlas has everything it asked for, then once more, and reads it back.
    private static byte[] Settled(VkRenderer renderer, VulkanContext ctx, bool single, Action<VkRenderer> draw)
    {
        if (single) WaitUntilSingleSampleReady(renderer);
        renderer.SingleSampleMainPass = single;
        byte[] rgba = [];
        for (var attempt = 0; attempt < 40; attempt++)
        {
            renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
            draw(renderer);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            rgba = ctx.ReadbackOffscreenRgba();
            if (!renderer.FontAtlasDirty && attempt > 0) break;
            System.Threading.Thread.Sleep(10);
        }
        renderer.SingleSampleMainPass = false;
        return rgba;
    }

    // The single-sample pipelines are built off the render thread from the renderer's construction.
    private static void WaitUntilSingleSampleReady(VkRenderer renderer)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!renderer.SingleSampleMainPassReady && clock.Elapsed < TimeSpan.FromSeconds(30))
            System.Threading.Thread.Sleep(5);
        renderer.SingleSampleMainPassReady.ShouldBeTrue("the single-sample pipelines never came in");
    }

    private static int MaxDifference(byte[] a, byte[] b)
    {
        a.Length.ShouldBe(b.Length);
        var max = 0;
        for (var i = 0; i < a.Length; i++)
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    private static int Lit(byte[] rgba)
    {
        var lit = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] > Backdrop.Red + 24) lit++;
        return lit;
    }

    // Pixels strictly between the backdrop and the ink in the red channel: an antialiased edge.
    private static int Intermediate(byte[] rgba)
    {
        var n = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] > Backdrop.Red + 8 && rgba[i] < Math.Max(Ink.Red, Accent.Red) - 8 && rgba[i] != Accent.Red) n++;
        return n;
    }
}
