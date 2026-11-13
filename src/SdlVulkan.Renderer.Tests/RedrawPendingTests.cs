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
