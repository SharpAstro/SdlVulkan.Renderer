using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The held-button rule for SDL's window mouse-leave (issue #173), on a view with no window: a leave with
/// no button held is one <see cref="InputEvent.PointerLeft"/>; with a button held it waits for the release.
/// The loop's redraw is armed from the return values, so those are asserted here too.
/// </summary>
public class PointerLeaveTests
{
    private static SdlWindowView NewView(List<InputEvent> seen, bool handled = true)
    {
        var view = new SdlWindowView(null!, null!);
        view.OnPointerInput = evt => { seen.Add(evt); return handled; };
        return view;
    }

    // What SdlEventLoop does for a real press and release.
    private static void Press(SdlWindowView v, byte b)
    {
        v.NoteButtonDown(b);
        v.DispatchPointerDown(b, 1f, 1f, 1, InputModifier.None);
    }

    private static bool Release(SdlWindowView v, byte b)
    {
        v.NoteButtonUp(b);
        var up = v.DispatchPointerUp(b, 1f, 1f);
        return v.FlushPendingLeave() || up;
    }

    [Fact]
    public void LeaveWithNoButtonHeldIsOnePointerLeft()
    {
        var seen = new List<InputEvent>();
        var view = NewView(seen);

        view.DispatchPointerLeave().ShouldBeTrue();

        seen.ShouldHaveSingleItem().ShouldBeOfType<InputEvent.PointerLeft>();
    }

    [Fact]
    public void LeaveWithAButtonHeldIsWithheldThenSentOnceAfterTheRelease()
    {
        var seen = new List<InputEvent>();
        var view = NewView(seen);
        Press(view, 1);
        seen.Clear();

        view.DispatchPointerLeave().ShouldBeFalse();
        seen.ShouldBeEmpty();

        Release(view, 1);
        seen.Count.ShouldBe(2);
        seen[0].ShouldBeOfType<InputEvent.MouseUp>();
        seen[1].ShouldBeOfType<InputEvent.PointerLeft>();

        // Sent once: a further release flushes nothing.
        view.FlushPendingLeave().ShouldBeFalse();
        seen.Count.ShouldBe(2);
    }

    [Fact]
    public void TwoButtonsHeldNeedBothReleased()
    {
        var seen = new List<InputEvent>();
        var view = NewView(seen);
        Press(view, 1);
        Press(view, 3);
        seen.Clear();

        view.DispatchPointerLeave();
        Release(view, 1);
        seen.ShouldNotContain(e => e is InputEvent.PointerLeft);

        Release(view, 3);
        seen[^1].ShouldBeOfType<InputEvent.PointerLeft>();
        seen.FindAll(e => e is InputEvent.PointerLeft).Count.ShouldBe(1);
    }

    [Fact]
    public void EnterBeforeTheReleaseDropsTheLeave()
    {
        var seen = new List<InputEvent>();
        var view = NewView(seen);
        Press(view, 1);
        view.DispatchPointerLeave();

        view.NoteMouseEnter();
        Release(view, 1);

        seen.ShouldNotContain(e => e is InputEvent.PointerLeft);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeaveReportsWhatTheHandlerReturned(bool handled)
    {
        var view = NewView([], handled);

        view.DispatchPointerLeave().ShouldBe(handled);
    }

    [Fact]
    public void ReleaseFlushReportsWhatTheHandlerReturned()
    {
        var view = NewView([], handled: false);
        Press(view, 1);
        view.DispatchPointerLeave();

        Release(view, 1).ShouldBeFalse();
    }

    [Fact]
    public void InspectorStylePointerDispatchNeitherSendsNorFlushesALeave()
    {
        var seen = new List<InputEvent>();
        var view = NewView(seen);

        // DebugInspector calls the DispatchPointer* methods only: no NoteButton*, no leave.
        view.DispatchPointerMove(5f, 5f);
        view.DispatchPointerDown(1, 5f, 5f, 1, InputModifier.None);
        view.DispatchPointerUp(1, 5f, 5f);
        seen.ShouldNotContain(e => e is InputEvent.PointerLeft);

        // And a real leave withheld by a real hold is not flushed by a synthetic release.
        Press(view, 1);
        view.DispatchPointerLeave();
        seen.Clear();
        view.DispatchPointerUp(1, 5f, 5f);
        seen.ShouldNotContain(e => e is InputEvent.PointerLeft);
    }
}
