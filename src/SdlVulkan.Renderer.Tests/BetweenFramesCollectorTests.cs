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
        // Read before the collector is made, so a collection between the two still counts as the runtime's own.
        var beforeAllocating = GC.CollectionCount(0);
        var collector = new BetweenFramesCollector(thresholdBytes: 256 * 1024);
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

    /// <summary>A process whose allocations and collections the test moves by hand.</summary>
    private sealed class FakeHeap
    {
        public long Allocated;
        public int Collections;
        public int Forced;

        public BetweenFramesCollector Collector(long threshold = BetweenFramesCollector.DefaultThresholdBytes)
            => new(threshold, () => Allocated, () => Collections, () => { Forced++; Collections++; });

        /// <summary>The runtime collecting on its own after <paramref name="bytes"/> more were allocated.</summary>
        public void RuntimeCollectsAfter(long bytes)
        {
            Allocated += bytes;
            Collections++;
        }
    }

    private const long MB = 1024 * 1024;

    [Fact]
    public void TheThresholdComesDownToThreeQuartersOfWhatTheRuntimeAllows()
    {
        // The PDF viewer's case: the runtime collected after 3.2 to 4.1 MB, under the 4 MB default, so the fixed
        // threshold never fired.
        var heap = new FakeHeap();
        var collector = heap.Collector();

        heap.RuntimeCollectsAfter(4 * MB);
        collector.AfterFrame().ShouldBeFalse();
        collector.ThresholdBytes.ShouldBe(3 * MB);

        heap.RuntimeCollectsAfter(2 * MB);
        collector.AfterFrame().ShouldBeFalse();
        // The average of 4 and 2 MB is 3 MB, three quarters of which is 2.25 MB.
        collector.ThresholdBytes.ShouldBe(3 * MB * 3 / 4);
    }

    [Fact]
    public void ALearnedThresholdCollectsBeforeTheRuntimeWould()
    {
        var heap = new FakeHeap();
        var collector = heap.Collector();
        heap.RuntimeCollectsAfter(4 * MB);
        collector.AfterFrame();

        heap.Allocated += 2 * MB;
        collector.AfterFrame().ShouldBeFalse("2 MB is under the learned 3 MB");
        heap.Allocated += 1 * MB;
        collector.AfterFrame().ShouldBeTrue();
        heap.Forced.ShouldBe(1);
        collector.Collections.ShouldBe(1);
        collector.AllocatedSinceCollection.ShouldBe(0);
    }

    [Fact]
    public void TheThresholdNeverRisesAboveTheCeiling()
    {
        var heap = new FakeHeap();
        var collector = heap.Collector(threshold: 4 * MB);

        heap.RuntimeCollectsAfter(40 * MB);
        collector.AfterFrame();

        collector.ThresholdBytes.ShouldBe(4 * MB);
    }

    [Fact]
    public void ACollectionAfterLittleSaysNothingAboutTheBudget()
    {
        // An induced collection, or one a large allocation started, comes after far less than the budget: counting it
        // would drag the threshold down to collecting every few frames.
        var heap = new FakeHeap();
        var collector = heap.Collector();

        heap.RuntimeCollectsAfter(MB / 4);
        collector.AfterFrame().ShouldBeFalse();

        collector.ThresholdBytes.ShouldBe(BetweenFramesCollector.DefaultThresholdBytes);
    }

    [Fact]
    public void TheThresholdNeverFallsBelowTheMinimum()
    {
        var heap = new FakeHeap();
        var collector = heap.Collector();

        heap.RuntimeCollectsAfter(BetweenFramesCollector.MinimumThresholdBytes);
        collector.AfterFrame();

        collector.ThresholdBytes.ShouldBe(BetweenFramesCollector.MinimumThresholdBytes);
    }

    [Fact]
    public void ItsOwnCollectionsTeachItNothing()
    {
        var heap = new FakeHeap();
        var collector = heap.Collector(threshold: 2 * MB);

        heap.Allocated += 2 * MB;
        collector.AfterFrame().ShouldBeTrue();
        collector.AfterFrame().ShouldBeFalse();

        collector.ThresholdBytes.ShouldBe(2 * MB);
    }

    [Fact]
    public void SeveralCollectionsBetweenTwoFramesShareWhatWasAllocated()
    {
        // An idle app presents no frames while its background threads allocate, and an app can set the collector on
        // the loop late: the first look then spans many of the runtime's collections, not one budget.
        var heap = new FakeHeap();
        var collector = heap.Collector();

        heap.RuntimeCollectsAfter(4 * MB);
        heap.RuntimeCollectsAfter(4 * MB);
        heap.RuntimeCollectsAfter(4 * MB);
        collector.AfterFrame();

        collector.ThresholdBytes.ShouldBe(3 * MB);
    }
}
