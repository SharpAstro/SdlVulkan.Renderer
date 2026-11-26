using System;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="BetweenFramesCollector"/> collects generation 0 after a frame only once the threshold is allocated since
/// the last collection it saw, and stands down when the runtime collected on its own in between.
/// </summary>
[Collection("GcSensitive")]
public sealed class BetweenFramesCollectorTests
{
    private static volatile byte[]? _sink;

    private static void Allocate(long bytes)
    {
        for (long done = 0; done < bytes; done += 64 * 1024) _sink = new byte[64 * 1024 - 64];
    }

    [Fact]
    public void UnderTheThresholdNothingIsCollected()
    {
        var collector = new BetweenFramesCollector(thresholdBytes: 64L * 1024 * 1024);
        collector.AfterFrame().ShouldBeFalse();
        collector.Collections.ShouldBe(0);
    }

    [Fact]
    public void PastTheThresholdAFrameCollectsAndCounts()
    {
        // A threshold far under the runtime's own budget, so the runtime does not collect first.
        var collector = new BetweenFramesCollector(thresholdBytes: 256 * 1024);
        var beforeAllocating = GC.CollectionCount(0);
        Allocate(512 * 1024);
        var gen0 = GC.CollectionCount(0);
        var collected = collector.AfterFrame();
        if (!collected)
        {
            // The only reason not to collect past the threshold: the runtime collected on its own while allocating,
            // and the collector stands down for that frame, which is the other half of its contract.
            gen0.ShouldBeGreaterThan(beforeAllocating);
            collector.Collections.ShouldBe(0);
            return;
        }
        collector.Collections.ShouldBe(1);
        GC.CollectionCount(0).ShouldBeGreaterThan(gen0);
        // Not asserted: AllocatedSinceCollection right after. It counts the whole process, and tests running in
        // parallel allocate past any bound between the collection and the read (seen on the Windows runner).
    }

    [Fact]
    public void ACollectionByTheRuntimeRestartsTheCount()
    {
        var collector = new BetweenFramesCollector(thresholdBytes: 256 * 1024);
        Allocate(512 * 1024);
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        collector.AfterFrame().ShouldBeFalse("the runtime already collected since the last frame");
        collector.Collections.ShouldBe(0);
    }
}
