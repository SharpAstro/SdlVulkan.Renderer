using System;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// That a frame which runs out of vertex ring loses its draws only ONCE: the ring grows at the next
/// frame start and the same writes then fit.
///
/// <para>The ring used to be a fixed allocation per frame in flight, so a consumer that could not
/// afford a dropped draw asked for its worst case up front, for every window, before anything was
/// drawn. Growth turns that into: start small, pay for what a frame actually needs, once.</para>
///
/// <para>Own context with a 4 KB ring, rather than the shared fixture's 4 MB one, so the overflow is
/// cheap to provoke. In the offscreen collection so it never runs beside another GPU test.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed unsafe class VertexRingGrowthTests(OffscreenGpuFixture gpu)
{
    private const uint RingBytes = 4096;

    private static VulkanContext? TinyRingContext()
    {
        try
        {
            vkInitialize().CheckResult();
            VkApplicationInfo appInfo = new() { apiVersion = SdlVulkanWindow.InstanceApiVersion() };
            VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, 64, 64, vertexBufferSize: RingBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [Fact]
    public void AFrameThatOverflowsGrowsTheRingForTheNextOne()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = TinyRingContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");

        // 8 KB of vertices into a 4 KB ring: dropped, and the frame says so.
        var big = new float[2048];
        var cmd = ctx!.BeginOffscreenFrame();
        ctx.VertexRingCapacityBytes.ShouldBe(RingBytes);
        ctx.WriteVertices(big).ShouldBe(uint.MaxValue);
        ctx.VertexRingOverflowed.ShouldBeTrue();
        ctx.VertexRingOverflowFrames.ShouldBe(1);
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        // The next frame owns the OTHER slot, which grows on its turn; the writes now fit.
        cmd = ctx.BeginOffscreenFrame();
        ctx.VertexRingOverflowed.ShouldBeFalse();
        ctx.VertexRingCapacityBytes.ShouldBeGreaterThanOrEqualTo((uint)(big.Length * sizeof(float)));
        ctx.WriteVertices(big).ShouldNotBe(uint.MaxValue);
        ctx.VertexRingOverflowed.ShouldBeFalse();
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        // And the slot that overflowed grows when its own turn comes back around.
        cmd = ctx.BeginOffscreenFrame();
        ctx.VertexRingCapacityBytes.ShouldBeGreaterThanOrEqualTo((uint)(big.Length * sizeof(float)));
        ctx.WriteVertices(big).ShouldNotBe(uint.MaxValue);
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        ctx.VertexRingPeakBytes.ShouldBe((uint)(big.Length * sizeof(float)));
        ctx.VertexRingOverflowFrames.ShouldBe(1, "only the first frame dropped anything");
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public void TheDemandIsTheWholeFramesNotWhereItFirstRanOut()
    {
        Assert.SkipWhen(gpu.Context is null, "no Vulkan ICD on this host");
        using var ctx = TinyRingContext();
        Assert.SkipWhen(ctx is null, "no Vulkan ICD on this host");

        // Three 3 KB writes: the first fits, the second and third do not. The frame needed 9 KB, and a
        // ring grown only to what the SECOND write asked for would drop the third next time too.
        var chunk = new float[768];
        var cmd = ctx!.BeginOffscreenFrame();
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.WriteVertices(chunk).ShouldBe(uint.MaxValue);
        ctx.WriteVertices(chunk).ShouldBe(uint.MaxValue);
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);

        cmd = ctx.BeginOffscreenFrame();
        ctx.VertexRingCapacityBytes.ShouldBeGreaterThanOrEqualTo(3u * 768 * sizeof(float));
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.WriteVertices(chunk).ShouldNotBe(uint.MaxValue);
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);
        ctx.WaitOffscreenFrameComplete();
    }
}
