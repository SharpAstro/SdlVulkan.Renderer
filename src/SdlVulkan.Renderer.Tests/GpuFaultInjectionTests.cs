#if DEBUG
using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The DEBUG fault switch (<see cref="GpuFaultInjection"/>): a faked submit failure reaches every submit
/// site on the device and leaves the state a real one leaves, so the paths that answer a wedge can be
/// driven on a healthy GPU.
/// <para>
/// Offscreen only, because that is what runs without a window. The swapchain path's answer (the
/// three-rejection streak, the event loop's recovery and hand-off) is exercised live through the
/// inspector's <c>gpu_fault</c> tool, since it lives in <see cref="SdlEventLoop"/>.
/// </para>
/// DEBUG-only, like the switch itself, so CI's Release leg does not build these.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class GpuFaultInjectionTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 16;
    private const uint Height = 16;

    private static readonly RGBAColor32 Backdrop = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 Ink = new RGBAColor32(255, 0, 0, 255);

    /// <summary>Fills the whole target with <see cref="Ink"/> over <see cref="Backdrop"/> and waits for it.
    /// <paramref name="arm"/> runs once the renderer exists, so whatever it uploads while being built cannot
    /// spend a counted fault meant for the frame.</summary>
    private static void RenderInk(VulkanContext ctx, Action? arm = null)
    {
        using var renderer = new VkRenderer(ctx, Width, Height);
        arm?.Invoke();
        renderer.BeginOffscreenFrame(Backdrop).ShouldBeTrue();
        renderer.FillRectangle(new RectInt(new PointInt((int)Width, (int)Height), new PointInt(0, 0)), Ink);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }

    private static void ShouldBeInk(VulkanContext ctx)
    {
        var pixels = ctx.ReadbackOffscreenRgba();
        var centre = (int)((Height / 2 * Width + Width / 2) * 4);
        new RGBAColor32(pixels[centre], pixels[centre + 1], pixels[centre + 2], pixels[centre + 3]).ShouldBe(Ink);
    }

    private VulkanContext? SharedContext()
    {
        if (gpu.Context is not { } ctx) return null;
        ctx.ResizeOffscreen(Width, Height);
        ctx.FaultInjection.Clear();
        return ctx;
    }

    [Fact]
    public void ATransientRejectionIsRetriedAndThePageStillRenders()
    {
        if (SharedContext() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var fault = ctx.FaultInjection;
        try
        {
            var before = fault.FakedResults;
            // The Adreno's measured transient: two rejections, then the third submit takes.
            RenderInk(ctx, arm: () => fault.RejectSubmits(2));

            (fault.FakedResults - before).ShouldBe(2);
            fault.IsArmed.ShouldBeFalse();
            // The two faked rejections sent nothing, so the ink proves the third attempt was a real submit.
            ShouldBeInk(ctx);
        }
        finally
        {
            fault.Clear();
        }
    }

    [Fact]
    public async Task APersistentRejectionFailsTheFrameAndLeavesNothingToWaitFor()
    {
        if (SharedContext() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var fault = ctx.FaultInjection;
        try
        {
            var before = fault.FakedResults;
            // The 2026-09-22 shape: the device refuses every submit, for as long as anyone keeps trying.
            var ex = Should.Throw<VkException>(() => RenderInk(ctx, arm: () => fault.RejectSubmits()));
            ex.Result.ShouldBe(VkResult.ErrorInitializationFailed);
            // The offscreen path retries a rejection, and gives up after its stated number of attempts
            // rather than grinding forever, handing the caller the driver's own result.
            (fault.FakedResults - before).ShouldBe(VulkanContext.OffscreenSubmitAttempts);
            ctx.SubmissionLedger.ShouldContain("FAULT INJECTED: rejecting every submit");

            fault.Clear();

            // The failed frame's fence was reset for a submit that never happened. If anything still
            // believed that submit was in flight, the next frame would wait on the fence forever, which is
            // how a rejected submit used to become a hung export. Bounded, so that regression is a named
            // failure rather than a stalled run.
            await Task.Run(() => RenderInk(ctx), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            ShouldBeInk(ctx);
        }
        finally
        {
            fault.Clear();
        }
    }

    [Fact]
    public void ARejectedOneShotUploadGoesThroughTheSameSeam()
    {
        if (SharedContext() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var fault = ctx.FaultInjection;
        try
        {
            fault.RejectSubmits(1);

            // A one-shot does not retry: it is not the deliverable, and its caller decides what a failed
            // upload means. A submit site that bypassed the seam would succeed here instead.
            var ex = Should.Throw<VkException>(() => ctx.ExecuteOneShot(static _ => { }));
            ex.Result.ShouldBe(VkResult.ErrorInitializationFailed);

            // The fault is spent, and the device carries on taking work.
            fault.IsArmed.ShouldBeFalse();
            ctx.ExecuteOneShot(static _ => { });
        }
        finally
        {
            fault.Clear();
        }
    }

    // Separate from the test so the test class need not be unsafe, which would forbid the await above.
    private static unsafe VulkanContext? TryCreateOwnContext()
    {
        try
        {
            vkInitialize().CheckResult();
            VkApplicationInfo appInfo = new() { apiVersion = SdlVulkanWindow.InstanceApiVersion() };
            VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, Width, Height);
        }
        catch (Exception)
        {
            return null; // no ICD on this host
        }
    }

    /// <summary>
    /// Its own context, because a loss is one-way on the context that saw it, as it must be for a real one:
    /// on the shared fixture it would leave every later test reporting a lost device.
    /// </summary>
    [Fact]
    public void ALostDeviceIsReportedAsALossAndStaysLost()
    {
        if (TryCreateOwnContext() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        using (ctx)
        {
            var fault = ctx.FaultInjection;
            ctx.DeviceLost.ShouldBeFalse();

            Should.Throw<VkException>(() => RenderInk(ctx, arm: fault.LoseDevice)).Result.ShouldBe(VkResult.ErrorDeviceLost);
            // A loss is not retried: only a rejection is worth a second attempt.
            fault.FakedResults.ShouldBe(1);
            ctx.DeviceLost.ShouldBeTrue();
            ctx.SubmissionLedger.ShouldContain("DEVICE_LOST seen");
            ctx.SubmissionLedger.ShouldContain("FAULT INJECTED: device lost");

            // Sticky, as a real loss is: the next frame is refused the same way.
            Should.Throw<VkException>(() => RenderInk(ctx)).Result.ShouldBe(VkResult.ErrorDeviceLost);

            // Clearing disarms the device, but the context that saw the loss keeps saying so.
            fault.Clear();
            ctx.DeviceLost.ShouldBeTrue();
        }
    }
}

/// <summary>The fault's own bookkeeping, which needs no GPU.</summary>
public sealed class GpuFaultInjectionStateTests
{
    [Fact]
    public void ACountedRejectionIsConsumedOnePerSubmit()
    {
        var fault = new GpuFaultInjection();
        fault.RejectSubmits(2);

        fault.TryFakeSubmit(out var first).ShouldBeTrue();
        first.ShouldBe(VkResult.ErrorInitializationFailed);
        fault.RejectsRemaining.ShouldBe(1);
        fault.TryFakeSubmit(out _).ShouldBeTrue();

        fault.TryFakeSubmit(out _).ShouldBeFalse();
        fault.IsArmed.ShouldBeFalse();
        fault.FakedResults.ShouldBe(2);
        fault.Describe().ShouldBeNull();
    }

    [Fact]
    public void AnUnlimitedRejectionLastsUntilCleared()
    {
        var fault = new GpuFaultInjection();
        fault.RejectSubmits();

        for (var i = 0; i < 100; i++)
            fault.TryFakeSubmit(out _).ShouldBeTrue();

        fault.RejectsRemaining.ShouldBeNull();
        fault.Describe().ShouldBe("FAULT INJECTED: rejecting every submit");

        fault.Clear();
        fault.TryFakeSubmit(out _).ShouldBeFalse();
    }

    [Fact]
    public void ALostDeviceOutranksAPendingRejection()
    {
        var fault = new GpuFaultInjection();
        fault.RejectSubmits(5);
        fault.LoseDevice();

        fault.TryFakeSubmit(out var result).ShouldBeTrue();
        result.ShouldBe(VkResult.ErrorDeviceLost);
        // Answered without spending the count.
        fault.RejectsRemaining.ShouldBe(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveCountIsRefusedRatherThanReadAsUnlimited(int count)
    {
        var fault = new GpuFaultInjection();
        Should.Throw<ArgumentOutOfRangeException>(() => fault.RejectSubmits(count));
        fault.IsArmed.ShouldBeFalse();
    }
}
#endif
