using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Where <see cref="SdlVulkanWindow.ResizeInPlace(int, int)"/> puts a window. The resize itself needs a desktop; this is
/// the geometry that decides where the window lands.
/// </summary>
public class WindowPlacementTests
{
    // A 1920x1080 display with a 48-px taskbar along the bottom.
    private static readonly WindowRect Usable = new(0, 0, 1920, 1032);

    [Fact]
    public void AWindowParkedBottomRightGrowsUpAndLeft()
        => WindowPlacement.Place(new WindowRect(1500, 760, 400, 250), 900, 750, Usable)
            .ShouldBe(new WindowRect(1000, 260, 900, 750));

    [Fact]
    public void AWindowTopLeftGrowsDownAndRight()
        => WindowPlacement.Place(new WindowRect(40, 30, 400, 250), 900, 750, Usable)
            .ShouldBe(new WindowRect(40, 30, 900, 750));

    [Fact]
    public void ShrinkingBackKeepsTheSameCorner()
        => WindowPlacement.Place(new WindowRect(1000, 260, 900, 750), 400, 250, Usable)
            .ShouldBe(new WindowRect(1500, 760, 400, 250));

    [Fact]
    public void AWindowTopRightGrowsDownAndLeft()
        => WindowPlacement.Place(new WindowRect(1700, 20, 200, 150), 900, 750, Usable)
            .ShouldBe(new WindowRect(1000, 20, 900, 750));

    [Fact]
    public void AWindowPartlyOffTheDisplayIsPulledBackOn()
        => WindowPlacement.Place(new WindowRect(-300, 900, 400, 250), 900, 750, Usable)
            .ShouldBe(new WindowRect(0, 282, 900, 750));

    [Fact]
    public void ASizeBiggerThanTheDisplayIsCappedToIt()
        => WindowPlacement.Place(new WindowRect(100, 100, 400, 250), 2500, 1400, Usable)
            .ShouldBe(new WindowRect(0, 0, 1920, 1032));

    [Fact]
    public void SecondMonitorCoordinatesWork()
        => WindowPlacement.Place(new WindowRect(3500, 800, 400, 250), 900, 750, new WindowRect(1920, 0, 2560, 1400))
            .ShouldBe(new WindowRect(3000, 300, 900, 750));
}
