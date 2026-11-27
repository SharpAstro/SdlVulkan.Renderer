using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>The swapchain image count choice, against fake surface limits (no GPU).</summary>
public sealed class SwapchainImageCountTests
{
    [Theory]
    [InlineData(2u, 0u, false, 3u)]   // default: min + 1, no maximum
    [InlineData(2u, 8u, false, 3u)]
    [InlineData(2u, 0u, true, 2u)]    // fewest: the minimum
    [InlineData(3u, 3u, false, 3u)]   // clamped to the maximum
    [InlineData(1u, 1u, false, 1u)]
    [InlineData(3u, 3u, true, 3u)]
    public void Count_is_min_plus_one_or_min_clamped_to_max(uint min, uint max, bool fewest, uint expected)
        => VulkanContext.ChooseSwapchainImageCount(min, max, fewest).ShouldBe(expected);
}
