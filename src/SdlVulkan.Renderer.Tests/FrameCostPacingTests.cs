using System;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// When a window on a software rasteriser may draw again, by what its drawing has cost
/// (<see cref="FrameCostPacing"/>): the limit, and the bound that keeps the limit from stalling the window.
/// </summary>
public sealed class FrameCostPacingTests
{
    private const long Hz = 1000;
    private static readonly long MaxHold = 100;

    private static long Due(long start, long end, double processorMs, int processors = 4, double share = 0.5)
        => FrameCostPacing.DueTimestamp(start, end, TimeSpan.FromMilliseconds(processorMs), processors, share, MaxHold, Hz);

    // 100 ms of processor on a 4-processor machine at half the machine is 50 ms of wall: 20 frames a second at most.
    [Fact]
    public void AFramesRestIsItsProcessorTimeOverTheShare() => Due(start: 1000, end: 1030, processorMs: 100).ShouldBe(1050);

    // A frame a GPU drew used next to no processor: nothing is added to the display's own interval.
    [Fact]
    public void ACheapFrameIsNotHeld() => Due(start: 1000, end: 1003, processorMs: 2).ShouldBe(1001);

    // The whole process's time is counted, so a busy process would otherwise never be let draw.
    [Fact]
    public void TheRestNeverOutlastsItsBoundAfterTheFrame() => Due(start: 1000, end: 1040, processorMs: 5000).ShouldBe(1140);

    [Theory]
    [InlineData(1, 1200)]  // one processor: half of it
    [InlineData(2, 1100)]
    [InlineData(8, 1025)]
    public void TheShareIsOfTheMachine(int processors, long due)
        => FrameCostPacing.DueTimestamp(1000, 1010, TimeSpan.FromMilliseconds(100), processors, 0.5, 1000, Hz).ShouldBe(due);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void NoShareIsNoLimit(double share) => Due(start: 1000, end: 1030, processorMs: 100, share: share).ShouldBe(1000);

    // A gesture drawing as fast as it is let: each frame costs 100 ms of processor and takes 30 ms of wall. Over ten
    // seconds the process uses half the machine and no more, and no rest is longer than the bound.
    [Fact]
    public void AGestureIsHeldToItsShareOfTheMachine()
    {
        long now = 0;
        double processor = 0;
        long longestRest = 0;
        var frames = 0;
        while (now < 10_000)
        {
            var start = now;
            var end = start + 30;
            processor += 100;
            var due = Due(start, end, processorMs: 100);
            longestRest = Math.Max(longestRest, due - end);
            now = Math.Max(due, end);
            frames++;
        }

        (processor / (now * 4.0)).ShouldBe(0.5, 0.01);
        frames.ShouldBeInRange(198, 202);
        longestRest.ShouldBeLessThanOrEqualTo(MaxHold);
    }
}
