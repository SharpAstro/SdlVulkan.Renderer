using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// When the loop asks a window's redraw check between frames (<see cref="RedrawCheckPacing"/>): at most about once a refresh,
/// however many wakes a burst of input makes, and on every one of the loop's 16 ms idle wakes.
/// </summary>
public sealed class RedrawCheckPacingTests
{
    // A microsecond clock.
    private const long TicksPerSecond = 1_000_000;

    private static long Refresh(double hz) => (long)(TicksPerSecond / hz);

    private static long Ms(double ms) => (long)(ms * TicksPerSecond / 1000);

    [Fact]
    public void TheFirstAskIsDue() => RedrawCheckPacing.Due(now: 5, nextCheckDue: 0).ShouldBeTrue();

    [Theory]
    [InlineData(60.0, 14_999)]   // 90 percent of 16.7 ms
    [InlineData(66.0, 13_635)]   // a Remote Desktop session's display
    [InlineData(144.0, 6_249)]
    [InlineData(30.0, 15_000)]   // 90 percent would be 30 ms, two idle wakes: held to the cap
    public void TheSpacingIsNinetyPercentOfARefreshAndNoMoreThan15Ms(double hz, long spacing)
        => RedrawCheckPacing.NextDue(0, Refresh(hz), TicksPerSecond).ShouldBe(spacing);

    [Fact]
    public void ABurstOfWakesBetweenFramesIsAskedAboutOnceARefresh()
    {
        // A thousand wakes in a second (input at 1 kHz), on a 60 Hz display.
        long due = 0;
        var asked = 0;
        for (var ms = 1; ms <= 1000; ms++)
        {
            if (RedrawCheckPacing.Due(Ms(ms), due))
            {
                asked++;
                due = RedrawCheckPacing.NextDue(Ms(ms), Refresh(60), TicksPerSecond);
            }
        }

        asked.ShouldBeInRange(60, 70);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(30.0)]
    public void EveryIdleWakeIsAskedEvenOneATimerTickEarly(double hz)
    {
        // The loop's idle wake is 16 ms, and on Windows a wait can come back most of a millisecond early: every wake must
        // still be asked, or an external change waits for a second one.
        long due = 0;
        long now = 0;
        for (var wake = 0; wake < 100; wake++)
        {
            now += wake % 2 == 0 ? Ms(16) : Ms(15.2);
            RedrawCheckPacing.Due(now, due).ShouldBeTrue($"wake {wake} at {now / 1000.0:F1} ms ({hz} Hz)");
            due = RedrawCheckPacing.NextDue(now, Refresh(hz), TicksPerSecond);
        }
    }

    // A host that states how long its window can go unasked is left alone for that long: sixty asks a second of a window
    // nothing is happening to were sixty wakes of the render thread for an answer that was "no" every time.
    [Fact]
    public void AnIdleWindowIsNextAskedWhenItsHostSays()
        => RedrawCheckPacing.NextDue(0, Refresh(60), TicksPerSecond, idleTicks: Ms(250), ticksSinceInput: long.MaxValue)
            .ShouldBe(Ms(250));

    [Fact]
    public void AHostThatStatesNoIntervalIsAskedAtTheRefreshsPaceAsBefore()
        => RedrawCheckPacing.NextDue(0, Refresh(60), TicksPerSecond, idleTicks: 0, ticksSinceInput: long.MaxValue)
            .ShouldBe(RedrawCheckPacing.NextDue(0, Refresh(60), TicksPerSecond));

    // What input starts often falls due a moment later (a tooltip's delay, a double click's window), so for a second
    // after input the window is asked at the refresh's pace whatever its host stated.
    [Theory]
    [InlineData(0, false)]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(5000, true)]
    public void ForASecondAfterInputTheHostsIntervalWaits(int msSinceInput, bool idle)
    {
        var due = RedrawCheckPacing.NextDue(0, Refresh(60), TicksPerSecond, idleTicks: Ms(250), ticksSinceInput: Ms(msSinceInput));

        due.ShouldBe(idle ? Ms(250) : RedrawCheckPacing.Spacing(Refresh(60), TicksPerSecond));
    }

    // The loop's per-pass hook is a host's proof of life, so no interval a host states leaves it unasked past a second.
    [Fact]
    public void NoIntervalIsLongerThanASecond()
        => RedrawCheckPacing.NextDue(0, Refresh(60), TicksPerSecond, idleTicks: Ms(60_000), ticksSinceInput: long.MaxValue)
            .ShouldBe(Ms(1000));

    // An event or a wake means something may have changed: the check is due a paced spacing after it was last asked,
    // however far the idle interval had put it, and never later than it already was.
    [Fact]
    public void AnEventPullsAnIdleCheckIn()
    {
        var spacing = RedrawCheckPacing.Spacing(Refresh(60), TicksPerSecond);

        // Asked at 100 ms, idle until 350 ms; an event at 200 ms makes it due at once (115 ms is past).
        RedrawCheckPacing.PulledIn(nextCheckDue: Ms(350), lastCheck: Ms(100), Refresh(60), TicksPerSecond).ShouldBe(Ms(100) + spacing);
        RedrawCheckPacing.Due(Ms(200), Ms(100) + spacing).ShouldBeTrue();

        // Asked a moment ago: an event does not ask it again inside the spacing, as a burst of input never did.
        RedrawCheckPacing.Due(Ms(105), RedrawCheckPacing.PulledIn(Ms(350), Ms(100), Refresh(60), TicksPerSecond)).ShouldBeFalse();

        // Already due sooner: left as it is.
        RedrawCheckPacing.PulledIn(nextCheckDue: Ms(101), lastCheck: Ms(100), Refresh(60), TicksPerSecond).ShouldBe(Ms(101));
    }

    // The loop's wait with nothing owed: until the earliest check falls due, never under its base 16 ms nor over a second.
    [Theory]
    [InlineData(-5, 16)]
    [InlineData(10, 16)]
    [InlineData(250, 250)]
    [InlineData(5000, 1000)]
    public void TheIdleWaitRunsUntilTheNextCheckFallsDue(int msUntilCheck, int waitMs)
        => RedrawCheckPacing.IdleWaitMilliseconds(Ms(msUntilCheck), TicksPerSecond).ShouldBe(waitMs);

    // Four asks a second where there were sixty, for a host that says a quarter of a second.
    [Fact]
    public void AnIdleSecondIsAFewAsksNotSixty()
    {
        long due = 0;
        var asked = 0;
        for (var now = 0L; now < TicksPerSecond; now += Ms(1))
        {
            if (RedrawCheckPacing.Due(now, due))
            {
                asked++;
                due = RedrawCheckPacing.NextDue(now, Refresh(60), TicksPerSecond, idleTicks: Ms(250), ticksSinceInput: long.MaxValue);
            }
        }

        asked.ShouldBe(4);
    }
}
