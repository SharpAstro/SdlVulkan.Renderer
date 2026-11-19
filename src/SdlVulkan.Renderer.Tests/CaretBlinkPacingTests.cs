using System;
using DIR.Lib;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>The caret blink's frame rule on a fake clock: no SDL, no GPU, no waiting.</summary>
public class CaretBlinkPacingTests
{
    private static (WindowUiSettings ui, FakeTimeProvider clock) FocusedAtBoundary()
    {
        var ui = new WindowUiSettings();
        ui.Focus.Current.ShouldBeNull();
        FocusField(ui);
        var clock = new FakeTimeProvider();
        // Start exactly on a phase boundary so offsets below are in whole milliseconds.
        clock.Advance(TimeSpan.FromMilliseconds(CaretBlinkPacing.MillisecondsToNextChange(clock) % CaretBlink.PhaseMilliseconds));
        return (ui, clock);
    }

    private static void FocusField(WindowUiSettings ui) => ui.Focus.Focus(new TextInputState());

    [Fact]
    public void FocusedFieldOwesAFrameAtEachBoundaryAndNotBetween()
    {
        var (ui, clock) = FocusedAtBoundary();
        long painted = CaretBlinkPacing.NeverPainted;
        CaretBlinkPacing.FrameOwed(ui, painted, clock).ShouldBeTrue();
        painted = CaretBlinkPacing.Step(ui, clock);

        clock.Advance(TimeSpan.FromMilliseconds(CaretBlink.PhaseMilliseconds - 1));
        CaretBlinkPacing.FrameOwed(ui, painted, clock).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        CaretBlinkPacing.FrameOwed(ui, painted, clock).ShouldBeTrue();
        painted = CaretBlinkPacing.Step(ui, clock);

        clock.Advance(TimeSpan.FromMilliseconds(CaretBlink.PhaseMilliseconds - 1));
        CaretBlinkPacing.FrameOwed(ui, painted, clock).ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        CaretBlinkPacing.FrameOwed(ui, painted, clock).ShouldBeTrue();
    }

    [Fact]
    public void PhaseSetOnTheSettingsBlinksOnThenOff()
    {
        var (ui, clock) = FocusedAtBoundary();
        var first = CaretBlinkPacing.Step(ui, clock);
        ui.CaretPhase.ShouldBe(first);
        var visible = CaretBlink.IsVisible(ui.CaretPhase);
        clock.Advance(TimeSpan.FromMilliseconds(CaretBlink.PhaseMilliseconds));
        CaretBlinkPacing.Step(ui, clock);
        CaretBlink.IsVisible(ui.CaretPhase).ShouldBe(!visible);
    }

    [Fact]
    public void WaitIsTheTimeToTheNextChange()
    {
        var (_, clock) = FocusedAtBoundary();
        CaretBlinkPacing.MillisecondsToNextChange(clock).ShouldBe(CaretBlink.PhaseMilliseconds);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        CaretBlinkPacing.MillisecondsToNextChange(clock).ShouldBe(CaretBlink.PhaseMilliseconds - 100);
        clock.Advance(TimeSpan.FromMilliseconds(429));
        CaretBlinkPacing.MillisecondsToNextChange(clock).ShouldBe(1);
    }

    [Fact]
    public void NoFocusedFieldOrNoUiNeverOwesAFrame()
    {
        var clock = new FakeTimeProvider();
        var ui = new WindowUiSettings();
        for (var i = 0; i < 5; i++)
        {
            CaretBlinkPacing.HasFocusedField(ui).ShouldBeFalse();
            CaretBlinkPacing.FrameOwed(ui, CaretBlinkPacing.NeverPainted, clock).ShouldBeFalse();
            CaretBlinkPacing.FrameOwed(null, CaretBlinkPacing.NeverPainted, clock).ShouldBeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(CaretBlink.PhaseMilliseconds));
        }
    }
}
