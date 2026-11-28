using Shouldly;
using Xunit;
using static SdlVulkan.Renderer.LoopTrimDecision;

namespace SdlVulkan.Renderer.Tests;

public class LoopTrimDecisionTests
{
    [Theory]
    [InlineData(Kind.Hidden)]
    [InlineData(Kind.Minimized)]
    public void HiddenOrMinimized_WithOptIn_Trims(Kind kind) =>
        Decide(kind, android: false, optIn: true, alreadyTrimmed: false).ShouldBe(LoopTrimAction.Trim);

    [Theory]
    [InlineData(Kind.Hidden)]
    [InlineData(Kind.Minimized)]
    public void HiddenOrMinimized_WithoutOptIn_DoesNothing(Kind kind) =>
        Decide(kind, android: false, optIn: false, alreadyTrimmed: false).ShouldBe(LoopTrimAction.None);

    [Fact]
    public void AlreadyTrimmed_IsNotTrimmedTwice() =>
        Decide(Kind.Minimized, false, true, alreadyTrimmed: true).ShouldBe(LoopTrimAction.None);

    [Theory]
    [InlineData(Kind.Shown)]
    [InlineData(Kind.Restored)]
    public void ShownOrRestored_WhenTrimmed_Resumes_EvenIfOptInTurnedOff(Kind kind)
    {
        Decide(kind, false, true, true).ShouldBe(LoopTrimAction.Resume);
        Decide(kind, false, false, true).ShouldBe(LoopTrimAction.Resume);
    }

    [Fact]
    public void ShownWhenNotTrimmed_DoesNothing() =>
        Decide(Kind.Shown, false, true, false).ShouldBe(LoopTrimAction.None);

    [Theory]
    [InlineData(Kind.Hidden)]
    [InlineData(Kind.Minimized)]
    [InlineData(Kind.Shown)]
    [InlineData(Kind.Restored)]
    public void Android_NeverTrimsOrResumes(Kind kind) =>
        Decide(kind, android: true, optIn: true, alreadyTrimmed: true).ShouldBe(LoopTrimAction.None);

    [Fact]
    public void OtherEvents_DoNothing() =>
        Decide(Kind.Other, false, true, true).ShouldBe(LoopTrimAction.None);
}
