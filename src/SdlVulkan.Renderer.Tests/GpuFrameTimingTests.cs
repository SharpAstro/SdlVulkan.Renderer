using System.Linq;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// GPU frame timing (VulkanContext.GpuTiming.cs): a frame's GPU time is measured with timestamp
/// queries, attributed to the frame that produced it, and broken down by named sections that belong to
/// that frame alone.
/// <para>
/// These pin the bookkeeping, not the numbers. Whether a GPU's in-pass timestamps mean anything is a
/// property of the GPU (see the note in the source on tiling GPUs), so the assertions are only that each
/// value is a finite, non-negative duration reported for the right frame under the right name.
/// </para>
/// Tests skip when Vulkan isn't loadable on the host or its queue has no timestamp support.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class GpuFrameTimingTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 32;
    private const uint Height = 32;

    private static readonly RGBAColor32 Backdrop = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 Ink = new RGBAColor32(255, 255, 255, 255);

    private VulkanContext? TimedContext()
    {
        if (gpu.Context is not { } ctx || !ctx.GpuTimingSupported)
        {
            return null;
        }

        ctx.ResizeOffscreen(Width, Height);
        return ctx;
    }

    private static void RenderFrame(VulkanContext ctx, System.Action<VkRenderer> draw)
    {
        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Width, Height);
        renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public void AFrameReportsItsOwnGpuTimeAndSectionsOnceItCompletes()
    {
        if (TimedContext() is not { } ctx)
        {
            Assert.Skip("Vulkan timestamps not available on this host");
            return;
        }

        var before = ctx.LastGpuFrameOrdinal;
        RenderFrame(ctx, r =>
        {
            ctx.BeginGpuSection("fill");
            r.FillRectangle(new RectInt(new PointInt(24, 24), new PointInt(4, 4)), Ink);
            ctx.BeginGpuSection("ellipse");
            r.FillEllipse(new RectInt(new PointInt(28, 20), new PointInt(4, 12)), Ink);
            ctx.EndGpuSection();
        });

        // The shared fixture has rendered frames before this one, so "a time exists" proves nothing;
        // the ordinal moving forward is what says the time is THIS frame's.
        ctx.LastGpuFrameOrdinal.ShouldBeGreaterThan(before);
        double.IsFinite(ctx.LastGpuFrameMs).ShouldBeTrue();
        ctx.LastGpuFrameMs.ShouldBeGreaterThanOrEqualTo(0);
        ctx.PeakGpuFrameMs.ShouldBeGreaterThanOrEqualTo(ctx.LastGpuFrameMs);

        var sections = ctx.LastGpuSections.ToArray();
        sections.Select(s => s.Name).ShouldBe(new[] { "fill", "ellipse" });
        foreach (var section in sections)
        {
            double.IsFinite(section.Milliseconds).ShouldBeTrue(section.Name);
            section.Milliseconds.ShouldBeGreaterThanOrEqualTo(0, section.Name);
        }
    }

    [Fact]
    public void AFrameWithoutSectionsDoesNotInheritThePreviousFramesSections()
    {
        if (TimedContext() is not { } ctx)
        {
            Assert.Skip("Vulkan timestamps not available on this host");
            return;
        }

        RenderFrame(ctx, r =>
        {
            ctx.BeginGpuSection("first frame only");
            r.FillRectangle(new RectInt(new PointInt(8, 8), new PointInt(0, 0)), Ink);
        });
        ctx.LastGpuSections.Length.ShouldBe(1);

        RenderFrame(ctx, r => r.FillRectangle(new RectInt(new PointInt(8, 8), new PointInt(0, 0)), Ink));

        ctx.LastGpuSections.Length.ShouldBe(0);
        double.IsFinite(ctx.LastGpuFrameMs).ShouldBeTrue();
    }

    [Fact]
    public void SectionsPastTheCapAreIgnoredAndASectionOutsideAFrameIsANoOp()
    {
        if (TimedContext() is not { } ctx)
        {
            Assert.Skip("Vulkan timestamps not available on this host");
            return;
        }

        // Between frames: nothing is being recorded, so there is nothing to write a timestamp into.
        ctx.BeginGpuSection("between frames");
        ctx.EndGpuSection();

        RenderFrame(ctx, _ =>
        {
            for (var i = 0; i < VulkanContext.MaxGpuSections + 4; i++)
            {
                ctx.BeginGpuSection("s");
            }
        });

        ctx.LastGpuSections.Length.ShouldBe(VulkanContext.MaxGpuSections);
    }

    [Fact]
    public void ACachedLayerPassIsTimedAsItsOwnSection()
    {
        if (TimedContext() is not { } ctx)
        {
            Assert.Skip("Vulkan timestamps not available on this host");
            return;
        }

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Width, Height);
        renderer.EnsureCachedLayerTargets(Width, Height).ShouldBeTrue();
        try
        {
            // A consumer draws its layer from the pre-render-pass hook, as the cached layer requires.
            renderer.OnPreRenderPass = _ =>
            {
                renderer.BeginCachedLayer(Width, Height, Backdrop).ShouldBeTrue();
                renderer.FillRectangle(new RectInt(new PointInt(24, 24), new PointInt(4, 4)), Ink);
                renderer.EndCachedLayer();
            };
            renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
            ctx.BeginGpuSection("main pass");
            renderer.FillRectangle(new RectInt(new PointInt(8, 8), new PointInt(0, 0)), Ink);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();

            // The layer's section is opened and closed by the pass itself, outside it, so it is there
            // without the consumer asking and is closed before anything the consumer times after it.
            ctx.LastGpuSections.ToArray().Select(s => s.Name)
                .ShouldBe(new[] { VulkanContext.CachedLayerGpuSection, "main pass" });
        }
        finally
        {
            renderer.OnPreRenderPass = null;
            renderer.ReleaseCachedLayerTargets();
        }
    }

    [Fact]
    public void TheHooksTimeIsTheConsumersOwnAndBelongsToItsFrame()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }
        ctx.ResizeOffscreen(Width, Height);

        // The offscreen context is owned by the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Width, Height);
        renderer.OnPreFlush = () => System.Threading.Thread.Sleep(20);
        renderer.OnPreRenderPass = _ => System.Threading.Thread.Sleep(30);
        renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
        // A sleep can wake a little before its due time on a coarse timer, never by a third of it.
        renderer.LastPreFlushMs.ShouldBeGreaterThan(15);
        renderer.LastPreRenderPassMs.ShouldBeGreaterThan(25);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        // A frame whose hooks are unset reports zero, not the time the last frame's hooks took.
        renderer.OnPreFlush = null;
        renderer.OnPreRenderPass = null;
        renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
        renderer.LastPreFlushMs.ShouldBe(0);
        renderer.LastPreRenderPassMs.ShouldBe(0);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }
}
