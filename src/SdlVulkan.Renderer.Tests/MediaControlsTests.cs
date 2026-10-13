using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Which presses of the system's media controls reach the app (<see cref="MediaControls"/>). The controls themselves
/// need a window and the Windows shell; this is the part that decides what a press means.
/// </summary>
public class MediaControlsTests
{
    [Theory]
    [InlineData(0, MediaButton.Play)]
    [InlineData(1, MediaButton.Pause)]
    [InlineData(2, MediaButton.Stop)]
    [InlineData(6, MediaButton.Next)]
    [InlineData(7, MediaButton.Previous)]
    public void A_button_the_controls_offer_reaches_the_app(int systemButton, MediaButton expected)
        => MediaControls.FromSystemButton(systemButton).ShouldBe(expected);

    [Theory]
    [InlineData(3)] // record
    [InlineData(4)] // fast forward
    [InlineData(5)] // rewind
    [InlineData(8)] // channel up
    [InlineData(9)] // channel down
    public void A_button_never_offered_is_dropped(int systemButton)
        => MediaControls.FromSystemButton(systemButton).ShouldBeNull();
}
