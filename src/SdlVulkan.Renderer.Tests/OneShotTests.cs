using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="VulkanDevice.ExecuteOneShot"/> waits for its work, but never without a bound, and never on a
/// device already known stuck: its unbounded <c>vkQueueWaitIdle</c> used to freeze the render thread on a
/// hung GPU before the frame loop's bounded fence wait could notice.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class OneShotTests(OffscreenGpuFixture gpu)
{
    [Fact]
    public void OneShotsCompleteOneAfterAnother()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        // The fence is reused, so the second must find it reset.
        var ran = 0;
        ctx.ExecuteOneShot(_ => ran++);
        ctx.ExecuteOneShot(_ => ran++);
        ran.ShouldBe(2);
    }

    [Fact]
    public void AOneShotOnADeviceKnownStuckFailsAtOnceWithoutSubmitting()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var device = ctx.GraphicsDevice;
        try
        {
            device.IsGpuStuck = true;
            var recorded = false;

            var ex = Should.Throw<VkException>(() => ctx.ExecuteOneShot(_ => recorded = true));

            // The same answer the bounded wait would give after its full timeout, given now.
            ex.Result.ShouldBe(VkResult.Timeout);
            recorded.ShouldBeFalse();
        }
        finally
        {
            device.IsGpuStuck = false;
        }

        // And the device takes work again once it is not.
        ctx.ExecuteOneShot(static _ => { });
    }
}
