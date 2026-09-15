using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// That the device can say how much memory this process may actually use, rather than only how large
/// the heaps are.
///
/// <para>The distinction is invisible on a discrete GPU and decisive on an integrated one. A heap's
/// <c>size</c> is its capacity, and where the GPU's memory IS system memory that capacity is all of
/// RAM -- so a residency budget built on it reads "plenty" at the exact moment the machine has begun
/// paging, which is when a budget was supposed to intervene. <c>heapBudget</c> is the driver's own
/// estimate of what this process may use given everything else running.</para>
///
/// <para><c>VK_EXT_memory_budget</c> is optional, and lavapipe does not offer it, so the contract
/// under test is two-sided: answer soundly when the extension is there, and refuse cleanly when it is
/// not. A caller must never read a zero budget as "no memory left".</para>
///
/// <para>This also guards the instance version, which is how the whole thing was found. An instance
/// created without <c>pApplicationInfo</c> is Vulkan 1.0 by spec, and a 1.0 instance answers a
/// core-1.1 <c>*2</c> query through the 1.0 entry point: the base struct fills, the pNext chain is
/// ignored, and every chained struct reads back zeroed. That looks exactly like a driver without the
/// feature. Here the extension is advertised AND enabled, so a zero budget means the instance has
/// regressed to 1.0 rather than that the driver declined.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class MemoryBudgetTests(OffscreenGpuFixture gpu, ITestOutputHelper output)
{
    [Fact]
    public void TheDeviceEitherReportsARealBudgetOrRefusesToGuess()
    {
        var context = gpu.Context;
        Assert.SkipWhen(context is null, "no Vulkan ICD on this host");
        var device = context!.GraphicsDevice;

        var answered = device.TryGetDeviceMemoryBudget(out var budget, out var usage);
        output.WriteLine($"extension={device.MemoryBudgetAvailable} answered={answered} "
                         + $"budget={budget / (1024 * 1024)} MB usage={usage / (1024 * 1024)} MB");

        if (!device.MemoryBudgetAvailable)
        {
            answered.ShouldBeFalse("a device without the extension must refuse, not report zero");
            budget.ShouldBe(0);
            usage.ShouldBe(0);
            return;
        }

        answered.ShouldBeTrue();
        budget.ShouldBeGreaterThan(0);
        usage.ShouldBeGreaterThanOrEqualTo(0);

        // This process has a device and an offscreen target, so it is using something -- but far less
        // than it is allowed. A usage at or above the budget here would mean the two are being read
        // from the wrong heaps, or swapped.
        usage.ShouldBeLessThan(budget);
    }
}
