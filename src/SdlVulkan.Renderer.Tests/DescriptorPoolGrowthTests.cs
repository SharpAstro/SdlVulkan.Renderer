using System.Collections.Generic;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Descriptor sets are a growable supply, not a fixed budget.
///
/// <para>
/// A pool was created once with <c>maxSets 512</c> and treated as a hard ceiling, so a document
/// carrying a few thousand small images ran the device out — and because the glyph atlas draws from
/// the same pool, the allocation that got refused could just as easily be TEXT as an image. Pools
/// cannot be resized, so growth is by chaining: when the current pool is spent, add another.
/// </para>
///
/// <para>
/// Both tests drive the public <see cref="VulkanContext.AllocateDescriptorSet"/> /
/// <see cref="VulkanContext.FreeDescriptorSet"/> pair rather than reaching into the device, so they
/// describe the contract a caller actually sees.
/// </para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class DescriptorPoolGrowthTests(OffscreenGpuFixture gpu)
{
    /// <summary>
    /// Several pools' worth, and deliberately not just over 512.
    /// <para>
    /// <c>maxSets</c> is a request, not a barrier every driver enforces exactly: measured against the
    /// single-pool build, allocation kept succeeding to <b>767</b> before it refused. A threshold
    /// just past 512 therefore sits inside the driver's slack and passes with or without pool
    /// chaining — worthless as a guard. This is far enough out that no plausible slack covers it,
    /// and it is roughly the real workload anyway: the page of a few thousand small images that
    /// exhausted the fixed pool in the first place.
    /// </para>
    /// </summary>
    private const int BeyondOnePool = 3000;

    [Fact]
    public void AllocateDescriptorSet_BeyondOnePool_KeepsHandingThemOut()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var sets = new List<VkDescriptorSet>(BeyondOnePool);
        try
        {
            for (var i = 0; i < BeyondOnePool; i++)
            {
                var set = ctx.AllocateDescriptorSet();
                Assert.True(set != VkDescriptorSet.Null, $"allocation {i} returned a null set");
                sets.Add(set);
            }

            // Distinctness is the real assertion. A pool that quietly reissued the same handle would
            // satisfy a bare count, and every texture would then sample whatever image was bound last.
            Assert.Equal(BeyondOnePool, new HashSet<VkDescriptorSet>(sets).Count);
        }
        finally
        {
            foreach (var set in sets) ctx.FreeDescriptorSet(set);
        }
    }

    [Fact]
    public void FreeDescriptorSet_ThenReallocate_DoesNotAddAnotherPool()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        // Cross into a second pool first, so the pool actually being observed below is one this test
        // created rather than whatever earlier tests in the collection left current.
        var first = new List<VkDescriptorSet>(BeyondOnePool);
        for (var i = 0; i < BeyondOnePool; i++) first.Add(ctx.AllocateDescriptorSet());
        foreach (var set in first) ctx.FreeDescriptorSet(set);

        var poolAfterGrowth = ctx.DescriptorPool;

        // Same demand again, now entirely from the recycle stack. Sets are freed to us, not to the
        // driver: every set in the device shares the one combined-image-sampler layout, so a returned
        // set is just re-pointed at another image. Churn must therefore cost no new pools — the pool
        // count should track PEAK live sets, not total allocations over time.
        var second = new List<VkDescriptorSet>(BeyondOnePool);
        try
        {
            for (var i = 0; i < BeyondOnePool; i++)
            {
                var set = ctx.AllocateDescriptorSet();
                Assert.True(set != VkDescriptorSet.Null, $"reallocation {i} returned a null set");
                second.Add(set);
            }

            Assert.Equal(poolAfterGrowth, ctx.DescriptorPool);
            Assert.Equal(BeyondOnePool, new HashSet<VkDescriptorSet>(second).Count);
            // Recycled, so the second round is the first round's handles back again.
            Assert.Equal(new HashSet<VkDescriptorSet>(first), new HashSet<VkDescriptorSet>(second));
        }
        finally
        {
            foreach (var set in second) ctx.FreeDescriptorSet(set);
        }
    }
}
