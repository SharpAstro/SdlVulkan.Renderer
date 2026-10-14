using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The size a host is told to lay out to after a resize (<see cref="SdlEventLoop.OnResize"/>) is the size the swapchain
/// was built at, not the window's. The two differ when the surface states a <c>currentExtent</c> other than the pixel
/// size SDL last reported (tianwen #1574: a window maximised to the work area). The event loop needs a desktop; this is
/// the decision it reports from.
/// </summary>
public class SwapchainSizeTests
{
    private static VkSurfaceCapabilitiesKHR Caps(uint currentW, uint currentH) => new()
    {
        currentExtent = new VkExtent2D(currentW, currentH),
        minImageExtent = new VkExtent2D(1, 1),
        maxImageExtent = new VkExtent2D(16384, 16384),
    };

    [Fact]
    public void ASurfaceThatStatesItsExtentWinsOverTheWindowSize()
    {
        var extent = VulkanContext.ChooseSwapchainExtent(Caps(1920, 1200), 1920, 1233);

        extent.width.ShouldBe(1920u);
        extent.height.ShouldBe(1200u);
    }

    [Fact]
    public void TheReportedSizeIsTheSwapchainsWhenTheSurfaceDisagreesWithTheWindow()
    {
        // The window says 1920x1233; the surface built the swapchain at 1920x1200.
        var extent = VulkanContext.ChooseSwapchainExtent(Caps(1920, 1200), 1920, 1233);

        VkRenderer.SizeAfterSwapchainBuild(extent.width, extent.height, 1920, 1233)
            .ShouldBe((extent.width, extent.height));
    }

    [Fact]
    public void AnUndefinedCurrentExtentTakesTheRequestedSizeClamped()
    {
        var extent = VulkanContext.ChooseSwapchainExtent(Caps(uint.MaxValue, uint.MaxValue), 20000, 900);

        extent.width.ShouldBe(16384u);
        extent.height.ShouldBe(900u);
        VkRenderer.SizeAfterSwapchainBuild(extent.width, extent.height, 20000, 900).ShouldBe((16384u, 900u));
    }

    [Fact]
    public void WithoutASwapchainTheRequestedSizeStands()
        => VkRenderer.SizeAfterSwapchainBuild(0, 0, 800, 600).ShouldBe((800u, 600u));
}
