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
}
