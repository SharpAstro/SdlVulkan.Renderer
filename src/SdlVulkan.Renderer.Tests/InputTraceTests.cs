using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The input line of <c>SDLVK_FRAME_LOG</c> (<see cref="InputTrace"/>): events counted by where they came from, a touch-synthesised
/// mouse apart from a real one, and how long they waited in SDL's queue, summed once a second.
/// </summary>
public sealed class InputTraceTests
{
    private const uint TouchMouseId = uint.MaxValue;
    private const uint PenMouseId = uint.MaxValue - 1;
    private const long TicksPerSecond = 1000;

    // The source as an int: a public theory cannot take the internal enum as a parameter.
    [Theory]
    [InlineData(TouchMouseId, (int)InputTrace.Source.TouchMouse)]
    [InlineData(PenMouseId, (int)InputTrace.Source.PenMouse)]
    [InlineData(0u, (int)InputTrace.Source.Mouse)]
    [InlineData(3u, (int)InputTrace.Source.Mouse)]
    public void AMouseEventIsCountedByTheMouseThatSentIt(uint which, int source)
        => InputTrace.MouseSource(which, TouchMouseId, PenMouseId).ShouldBe((InputTrace.Source)source);

    [Fact]
    public void ASecondOfInputIsSummedBySourceWithItsQueueAge()
    {
        var trace = new InputTrace();
        trace.TryTakeSummary(now: 1000, TicksPerSecond, out _).ShouldBeFalse("the first ask only starts the clock");

        trace.Count(InputTrace.Source.Mouse, ageMs: 2);
        trace.Count(InputTrace.Source.TouchMouse, ageMs: 10);
        trace.Count(InputTrace.Source.TouchMouse, ageMs: 30);
        trace.Count(InputTrace.Source.Finger, ageMs: -1);
        trace.TryTakeSummary(now: 1500, TicksPerSecond, out _).ShouldBeFalse("not a second yet");

        trace.TryTakeSummary(now: 2000, TicksPerSecond, out var s).ShouldBeTrue();
        s.Mouse.ShouldBe(1);
        s.TouchMouse.ShouldBe(2);
        s.Finger.ShouldBe(1);
        s.AgeAvgMs.ShouldBe(14.0, "the age of the events whose age was known");
        s.AgeMaxMs.ShouldBe(30.0);
        s.Seconds.ShouldBe(1.0);

        trace.TryTakeSummary(now: 3000, TicksPerSecond, out _).ShouldBeFalse("a quiet second says nothing");
    }
}
