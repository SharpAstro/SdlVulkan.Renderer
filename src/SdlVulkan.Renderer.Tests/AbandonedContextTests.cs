using System;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The abandoned-context contract, which exists because two independently-correct bounded waits
/// combined into a crash.
/// <para>
/// SdlEventLoop stops waiting for a sacrificial recovery task after GpuWedgeRecoveryDeadlineMs and
/// declares the device abandoned, on the stated assumption that the task's thread stays blocked
/// forever. VulkanContext.TryDrainDevice makes that assumption false: it is bounded too, and forces
/// its teardown on timeout, so a recovery that is merely SLOW reliably wakes up and carries on
/// rebuilding the swapchain -- against a surface the host has since destroyed. Seen in the field on
/// an Adreno X1-85 as an access violation inside vkGetPhysicalDeviceSurfaceCapabilitiesKHR, reached
/// from CreateSwapchain, on a surface Dispose had already destroyed (the handle is not nulled, so it
/// is dangling rather than Null and no null check would have caught it).
/// </para>
/// <para>
/// <b>If this test kills the process (0xC0000005 on Windows, 139 on Linux) it is that bug returning,
/// not the lavapipe teardown flake</b> -- the flake fires at end of run, this fires mid-run. Verified
/// by deleting the checkpoints and watching the run die with exit code -1073741819, the same code the
/// field crash reported.
/// </para>
/// <para>
/// The other half of the fix -- Dispose LEAKING the device, surface and instance instead of freeing
/// them under a thread nobody can join -- is deliberately not pinned here. It only matters in the
/// true race (the task is inside the drain while the host tears down), and every deterministic
/// sequence a test can write is already made safe by the checkpoints below, so a test for it would
/// pass with the guard deleted. An assertion that survives the removal of its own subject is worse
/// than no assertion.
/// </para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed unsafe class AbandonedContextTests
{
    /// <summary>
    /// Its own context rather than the shared fixture's, because abandoning is one-way: it would
    /// poison every other test in the collection, whose teardown would then silently stop freeing
    /// anything. Never disposed -- that IS the contract, so this adds one instance CREATE and zero
    /// instance DESTROYS to the run, and the documented lavapipe flake is a teardown segfault.
    /// </summary>
    private static VulkanContext? TryCreateOwnContext()
    {
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out var instance).CheckResult();
            return VulkanContext.CreateOffscreen(instance, 64, 64);
        }
        catch (Exception)
        {
            return null; // no ICD on this host -- skip, like every other offscreen test
        }
    }

    [Fact]
    public void RecoveryStopsAtItsCheckpointOnceTheLoopHasAbandonedIt()
    {
        var ctx = TryCreateOwnContext();
        Assert.SkipWhen(ctx is null, "No Vulkan ICD available on this host.");

        Assert.False(ctx!.IsAbandoned);
        ctx.Abandon();
        Assert.True(ctx.IsAbandoned);

        // The production sequence: the loop blew its deadline and abandoned this context WHILE a
        // recovery was in flight, and the recovery now wakes up and finishes its job. Reaching
        // CreateSwapchain is the crash; returning is the fix. An offscreen context carries no
        // surface, so the rebuild would fault on a null handle here where the field faulted on a
        // dangling one -- a different pointer, the same use of a swapchain the caller no longer owns.
        ctx.RecoverFromGpuError(64, 64);

        // Reached only if recovery returned. One-way, so a second call is still refused.
        Assert.True(ctx.IsAbandoned);
        ctx.RecoverFromGpuError(64, 64);
    }
}
