using System;
using System.IO;
using System.Linq;
using System.Text;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// That a frame which runs out of vertex ring loses NOTHING: the slot it ran out in moves to a bigger
/// buffer there and then, and the draws recorded before the move still read the old one.
///
/// <para>The ring used to be a fixed allocation per frame in flight, so a consumer that could not
/// afford a dropped draw asked for its worst case up front, for every window, before anything was
/// drawn. Growth at the next frame start turned that into: start small, pay for what a frame actually
/// needs. But the frame that ran out was still presented without its late draws, which on a dense sheet
/// was a frame with no page on it, so growth happens mid-frame now.</para>
///
/// <para>Own context with a 4 KB ring, rather than the shared fixture's 4 MB one, so the overflow is
/// cheap to provoke. In the offscreen collection so it never runs beside another GPU test.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class VertexRingGrowthTests(OffscreenGpuFixture gpu)
{
    private const uint RingBytes = 4096;
    private const uint Width = 128;
    private const uint Height = 64;

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static unsafe VulkanContext? TinyRingContext()
    {
        try
        {
            vkInitialize().CheckResult();
            VkApplicationInfo appInfo = new() { apiVersion = SdlVulkanWindow.InstanceApiVersion() };
            VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, Width, Height, vertexBufferSize: RingBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [Fact]
    public void AFrameThatOutgrowsTheRingGrowsItThereAndDropsNothing()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = TinyRingContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");

        // 8 KB of vertices into a 4 KB ring: the slot grows, and the write lands at the start of it.
        var big = new float[2048];
        var cmd = ctx!.BeginOffscreenFrame();
        ctx.VertexRingCapacityBytes.ShouldBe(RingBytes);
        ctx.WriteVertices(big).ShouldBe(0u);
        ctx.VertexRingOverflowed.ShouldBeFalse();
        ctx.VertexRingGrownMidFrame.ShouldBe(1);
        ctx.VertexRingCapacityBytes.ShouldBeGreaterThanOrEqualTo((uint)(big.Length * sizeof(float)));
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        // The next frame owns the OTHER slot, which takes the same demand at its frame start, where the
        // swap is free, so it never has to grow mid-frame.
        cmd = ctx.BeginOffscreenFrame();
        ctx.VertexRingCapacityBytes.ShouldBeGreaterThanOrEqualTo((uint)(big.Length * sizeof(float)));
        ctx.WriteVertices(big).ShouldNotBe(uint.MaxValue);
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        ctx.VertexRingGrownMidFrame.ShouldBe(1, "only the first frame had to grow mid-frame");
        ctx.VertexRingOverflowFrames.ShouldBe(0, "nothing was dropped");
        ctx.VertexRingPeakBytes.ShouldBe((uint)(big.Length * sizeof(float)));
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public void TheOtherSlotGrowsToTheWholeFramesDemand()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = TinyRingContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");

        // Three 3 KB writes: the first fits, the second moves the slot to a bigger buffer, the third
        // follows it there. The frame needed 9 KB, and the slot that did not grow has to take all of it,
        // not what the frame had asked for when it ran out.
        var chunk = new float[768];
        var cmd = ctx!.BeginOffscreenFrame();
        ctx.WriteVertices(chunk).ShouldBe(0u);
        ctx.WriteVertices(chunk).ShouldBe(0u, "the second write starts the new buffer");
        ctx.WriteVertices(chunk).ShouldBe((uint)(chunk.Length * sizeof(float)));
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        cmd = ctx.BeginOffscreenFrame();
        ctx.VertexRingCapacityBytes.ShouldBeGreaterThanOrEqualTo(3u * 768 * sizeof(float));
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        ctx.VertexRingGrownMidFrame.ShouldBe(1);
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public void DrawsOnBothSidesOfAMidFrameGrowthAllReachThePicture()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = TinyRingContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Width, Height);

        // A rectangle into the 4 KB ring, a polyline that does not fit (about 10 KB of quads), then
        // another rectangle into the bigger buffer. The first rectangle's draw was recorded against the
        // buffer the ring then left, which has to outlive the frame for it to reach the picture.
        var points = new (float X, float Y)[200];
        for (var i = 0; i < points.Length; i++)
            points[i] = (32 + 64 * i / (float)(points.Length - 1), 8 + (i % 2) * 48);
        byte[] Frame() => RenderToRgba(renderer, ctx!, r =>
        {
            r.FillRectangle(new RectInt(new PointInt(24, (int)Height), new PointInt(0, 0)), new RGBAColor32(255, 0, 0, 255));
            r.DrawPolyline(points, new RGBAColor32(0, 0, 255, 255), thickness: 2);
            r.FillRectangle(new RectInt(new PointInt((int)Width, (int)Height), new PointInt(104, 0)), new RGBAColor32(0, 255, 0, 255));
        });

        var grew = Frame();
        ctx!.VertexRingGrownMidFrame.ShouldBe(1, "the polyline should not have fitted");
        Frame();
        var fitted = Frame();

        ctx.VertexRingGrownMidFrame.ShouldBe(1);
        ctx.VertexRingOverflowFrames.ShouldBe(0);
        Pixel(grew, 8, 32).ShouldBe((255, 0, 0), "the draw before the growth is in the picture");
        Pixel(grew, 116, 32).ShouldBe((0, 255, 0), "the draw after the growth is in the picture");
        grew.ShouldBe(fitted, "a frame that grew draws what a frame that fitted draws");
    }

    [Fact]
    public void ABitmapGlyphBatchThatOutgrowsTheRingDrawsEveryGlyph()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = TinyRingContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");
        using var renderer = new VkRenderer(ctx!, Width, Height);

        // The bitmap glyph batch draws its glyphs as ONE range of the ring. 96 bytes a glyph, so the
        // 4 KB ring holds 42 of these 60 and the batch's range would straddle the move to a bigger
        // buffer, which it must notice and draw in two.
        const float size = 12f;
        renderer.OnPreFlush = () => renderer.PreWarmGlyph(FontPath, size, new Rune('M'));
        byte[] Frame() => RenderToRgba(renderer, ctx!, r =>
        {
            r.BeginGlyphBatch(new RGBAColor32(255, 255, 255, 255));
            for (var i = 0; i < 60; i++)
                r.AddBatchedGlyphAtBaseline(FontPath, size, new Rune('M'), -1, 2 + (i % 12) * 10, 12 + (i / 12) * 12);
            r.EndGlyphBatch();
        });

        // A glyph is drawable once the atlas has flushed it, so the batch writes nothing until then; the
        // first frame that draws it is the one whose batch outgrows the ring.
        byte[]? grew = null;
        for (var attempt = 0; attempt < 20 && grew is null; attempt++)
        {
            ctx!.VertexRingGrownMidFrame.ShouldBe(0, "nothing outgrows the ring before the batch draws");
            var frame = Frame();
            if (LitPixels(frame) > 0) grew = frame;
        }
        grew.ShouldNotBeNull("the glyph never became drawable, so the test proves nothing");
        ctx!.VertexRingGrownMidFrame.ShouldBe(1, "60 glyphs should not have fitted the 4 KB ring");

        Frame();
        var fitted = Frame();
        ctx.VertexRingGrownMidFrame.ShouldBe(1);
        grew.ShouldBe(fitted, "the frame that grew mid-batch draws every glyph a frame that fitted draws");
    }

    [Fact]
    public async System.Threading.Tasks.Task GrowingMidFrameIsSilentUnderTheValidationLayer()
    {
        if (!ValidatedOffscreen.TryCreate(Width, Height, out var ctx, out var messenger, out var api, out var skip,
                vertexBufferSize: RingBytes))
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        var wedged = false;
        try
        {
            // Each frame asks for four times what the last did, from 10 KB to 10 MB, so slots outgrow
            // their ring in the middle of a frame (the first, then the last two, a growth being rounded
            // up to whole megabytes), and the buffers they leave are retired behind frames still in
            // flight. A buffer freed before the frame that bound it had finished reads as "was destroyed".
            var run = System.Threading.Tasks.Task.Run(() =>
            {
                using var renderer = new VkRenderer(ctx!, Width, Height);
                for (var frame = 0; frame < 2 * VulkanContext.MaxFramesInFlight + 2; frame++)
                {
                    var points = new (float X, float Y)[200 << (2 * frame)];
                    for (var i = 0; i < points.Length; i++)
                        points[i] = (4 + 120 * i / (float)(points.Length - 1), 8 + (i % 2) * 48);
                    renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
                    renderer.FillRectangle(new RectInt(new PointInt(24, (int)Height), new PointInt(0, 0)), new RGBAColor32(255, 0, 0, 255));
                    renderer.DrawPolyline(points, new RGBAColor32(0, 0, 255, 255), thickness: 2);
                    renderer.FillRectangle(new RectInt(new PointInt((int)Width, (int)Height), new PointInt(104, 0)), new RGBAColor32(0, 255, 0, 255));
                    renderer.EndOffscreenFrame();
                }
                ctx!.WaitOffscreenFrameComplete();
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

            ctx!.VertexRingGrownMidFrame.ShouldBeGreaterThanOrEqualTo(2, "the sequence has to move the ring more than once");
            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0,
                $"the validation layer reported {errors.Length} error(s) while the ring grew mid-frame:\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            if (!wedged)
                ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }

    private static byte[] RenderToRgba(VkRenderer renderer, VulkanContext ctx, Action<VkRenderer> draw)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return ctx.ReadbackOffscreenRgba();
    }

    private static (int R, int G, int B) Pixel(byte[] rgba, int x, int y)
    {
        var i = (y * (int)Width + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    private static int LitPixels(byte[] rgba)
    {
        var lit = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] > 24) lit++;
        return lit;
    }
}
