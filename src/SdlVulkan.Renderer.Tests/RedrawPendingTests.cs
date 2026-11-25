using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="SdlWindowView.RedrawPending"/> is what an app reads to learn that the frame about to be drawn has a cause it
/// did not raise itself, so it must not narrow that frame's damage to a cause of its own (a clock's tick). The flag behind
/// it was internal: an app could ask for a frame and could not see that one was already owed.
/// </summary>
public class RedrawPendingTests
{
    // The primary constructor stores the window and renderer without touching them, so a view needs no live window here.
    private static SdlWindowView NewView() => new SdlWindowView(null!, null!);

    [Fact]
    public void ANewWindowIsOwedItsFirstFrame() => NewView().RedrawPending.ShouldBeTrue();

    [Fact]
    public void ARequestIsSeenUntilTheLoopDrawsIt()
    {
        var view = NewView();

        // What the loop does as it commits to a frame.
        view.NeedsRedraw = false;
        view.RedrawPending.ShouldBeFalse("a drawn window owes nothing");

        view.RequestRedraw();
        view.RedrawPending.ShouldBeTrue();
    }

    [Fact]
    public void AFrameOwedToAMoveAloneIsKnownAsSuch()
    {
        var view = NewView();
        view.NeedsRedraw = false;

        view.ArmForPointerMove();
        view.RedrawPending.ShouldBeTrue();
        view.RedrawPendingForPointerMoveAlone.ShouldBeTrue();

        // More moves change nothing.
        view.ArmForPointerMove();
        view.RedrawPendingForPointerMoveAlone.ShouldBeTrue();
    }

    // Either order: the frame is owed to more than a move, so it is not one an app may narrow to what the move changed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnyOtherCauseTakesTheMoveAloneAway(bool moveFirst)
    {
        var view = NewView();
        view.NeedsRedraw = false;

        if (moveFirst)
        {
            view.ArmForPointerMove();
            view.RequestRedraw();
        }
        else
        {
            view.RequestRedraw();
            view.ArmForPointerMove();
        }

        view.RedrawPending.ShouldBeTrue();
        view.RedrawPendingForPointerMoveAlone.ShouldBeFalse();
    }

    // A frame the app's own check asked for, held by pacing past the next check, is still the check's own: the app
    // knows what it asked it for. It used to read as a cause of the loop's, which an app that narrows draws in full.
    [Fact]
    public void AFrameTheCheckAskedForIsKnownAsTheAppsOwn()
    {
        var view = NewView();
        view.NeedsRedraw = false;

        view.ArmForCheck();
        view.RedrawPending.ShouldBeTrue();
        view.RedrawPendingForCheckAndPointerMovesAlone.ShouldBeTrue();
        view.RedrawPendingForPointerMoveAlone.ShouldBeFalse("the check asked, not a move");

        // A move a handler took changes neither answer, in either order.
        view.ArmForPointerMove();
        view.RedrawPendingForCheckAndPointerMovesAlone.ShouldBeTrue();

        view.NeedsRedraw = false;
        view.ArmForPointerMove();
        view.ArmForCheck();
        view.RedrawPendingForCheckAndPointerMovesAlone.ShouldBeTrue();
        view.RedrawPendingForPointerMoveAlone.ShouldBeTrue("a check that agrees to a move's frame leaves it a move's");
    }

    // Anything the loop raises itself is a cause the app cannot name, before or after its own.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACauseOfTheLoopsTakesTheAppsOwnAway(bool checkFirst)
    {
        var view = NewView();
        view.NeedsRedraw = false;

        if (checkFirst)
        {
            view.ArmForCheck();
            view.RequestRedraw();
        }
        else
        {
            view.RequestRedraw();
            view.ArmForCheck();
        }

        view.RedrawPendingForCheckAndPointerMovesAlone.ShouldBeFalse();

        // And a drawn frame starts over.
        view.NeedsRedraw = false;
        view.RedrawPendingForCheckAndPointerMovesAlone.ShouldBeFalse("nothing is owed");
        view.ArmForCheck();
        view.RedrawPendingForCheckAndPointerMovesAlone.ShouldBeTrue();
    }

    [Fact]
    public void ANewWindowsFirstFrameIsNotTheAppsOwn() => NewView().RedrawPendingForCheckAndPointerMovesAlone.ShouldBeFalse();

    [Fact]
    public void ADrawnFrameStartsOver()
    {
        var view = NewView();
        view.NeedsRedraw = false;
        view.ArmForPointerMove();

        view.NeedsRedraw = false;
        view.RedrawPendingForPointerMoveAlone.ShouldBeFalse();
        view.RedrawPending.ShouldBeFalse();
    }
}
