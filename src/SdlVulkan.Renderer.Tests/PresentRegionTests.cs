using System;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// What a present names as changed (<c>VulkanContext.PresentRegion.cs</c>, <c>VK_KHR_incremental_present</c>), without a
/// device. A presentation engine given a rectangle moves that and keeps the rest of what it showed, as Mesa's X11 software
/// present does (it sends only the rectangle's rows to the X server). Named too little, the window keeps stale pixels;
/// named too much, the present costs what it did before.
/// </summary>
public sealed class PresentRegionTests
{
    private const int Size = 16;

    private static SwapchainDamage NewSwapchain()
    {
        var changed = new SwapchainDamage();
        changed.Reset(1);
        return changed;
    }

    // THE property. Whatever the app repaints, and whichever frames never reach a present: after every present, the
    // window the engine holds is the app's frame, every cell of it.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AfterEveryPresentTheWindowIsTheAppsFrame(int seed)
    {
        var random = new Random(seed);
        var changed = NewSwapchain();
        var frame = new int[Size, Size];   // the app's frame, each cell the number of the frame that last wrote it
        var window = new int[Size, Size];  // what the presentation engine shows
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                window[y, x] = -1;         // nothing shown before the first present

        for (var n = 1; n <= 400; n++)
        {
            if (random.Next(10) == 0)
            {
                changed.MarkFull();
                Paint(frame, n, 0, 0, Size, Size);
            }
            else
            {
                var x0 = random.Next(Size);
                var y0 = random.Next(Size);
                var w = 1 + random.Next(Size - x0);
                var h = 1 + random.Next(Size - y0);
                changed.Add(x0 + 0.25f, y0 + 0.5f, w - 0.5f, h - 0.75f);   // a fractional rectangle, rounded out by the take
                Paint(frame, n, x0, y0, w, h);
            }

            // One frame in four never reaches a present (a dropped submit, an out-of-date swapchain): it takes nothing,
            // and what it declared is the next present's.
            if (random.Next(4) == 0)
            {
                continue;
            }

            if (VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out var rect))
            {
                Copy(frame, window, rect.offset.x, rect.offset.y, (int)rect.extent.width, (int)rect.extent.height);
            }
            else
            {
                Copy(frame, window, 0, 0, Size, Size);
            }

            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                    window[y, x].ShouldBe(frame[y, x], $"cell ({x}, {y}) after frame {n}");
        }
    }

    [Fact]
    public void ANewSwapchainsFirstPresentIsWhole()
    {
        var changed = NewSwapchain();
        changed.Add(2f, 2f, 3f, 3f);

        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _).ShouldBeFalse("it has shown nothing yet");
    }

    [Fact]
    public void AClocksTickNamesTheClocksRectangle()
    {
        var changed = NewSwapchain();
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _);

        changed.Add(10.4f, 0f, 4.2f, 3f);

        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out var rect).ShouldBeTrue();
        rect.offset.x.ShouldBe(10);
        rect.offset.y.ShouldBe(0);
        rect.extent.width.ShouldBe(5u, "the far edge, 14.6, rounded up as the frame's own region is");
        rect.extent.height.ShouldBe(3u);
        rect.layer.ShouldBe(0u);
    }

    [Fact]
    public void AFrameThatNeverPresentedLeavesItsDamageToTheNext()
    {
        var changed = NewSwapchain();
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _);

        changed.Add(1f, 1f, 2f, 2f);   // dropped: no present takes it
        changed.Add(10f, 8f, 2f, 2f);

        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out var rect).ShouldBeTrue();
        (rect.offset.x, rect.offset.y, rect.extent.width, rect.extent.height).ShouldBe((1, 1, 11u, 9u));
    }

    [Fact]
    public void AFrameDeclaredFullOrDeclaringNothingIsWhole()
    {
        var changed = NewSwapchain();
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _);

        changed.Add(1f, 1f, 2f, 2f);
        changed.MarkFull();
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _).ShouldBeFalse("full wins over a rectangle");

        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _)
            .ShouldBeFalse("a frame that declared nothing is whole, as it is to the frame's own damage");
    }

    [Fact]
    public void ARectangleIsClampedToTheImageAndOneOutsideItIsWhole()
    {
        var changed = NewSwapchain();
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _);

        changed.Add(12f, -3f, 10f, 5f);
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out var rect).ShouldBeTrue();
        (rect.offset.x, rect.offset.y, rect.extent.width, rect.extent.height).ShouldBe((12, 0, 4u, 2u));

        changed.Add(20f, 20f, 4f, 4f);
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _).ShouldBeFalse("nothing of it is in the image");
    }

    [Fact]
    public void WhereRectanglesAreNotAllowedThePresentIsWholeAndStillTakes()
    {
        var changed = NewSwapchain();
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out _);
        changed.Add(1f, 1f, 2f, 2f);

        VulkanContext.TakeChangedRegion(changed, allowed: false, Size, Size, out _).ShouldBeFalse();

        changed.Add(5f, 5f, 1f, 1f);
        VulkanContext.TakeChangedRegion(changed, allowed: true, Size, Size, out var rect).ShouldBeTrue();
        (rect.offset.x, rect.offset.y, rect.extent.width, rect.extent.height)
            .ShouldBe((5, 5, 1u, 1u), "the whole present showed the earlier rectangle, so only what came after is owed");
    }

    private static void Paint(int[,] cells, int value, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
                cells[y, x] = value;
    }

    private static void Copy(int[,] from, int[,] to, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
                to[y, x] = from[y, x];
    }
}
