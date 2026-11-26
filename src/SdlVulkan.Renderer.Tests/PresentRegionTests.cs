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

    [Theory]
    [InlineData(true, true, true, null, true)]     // software rasteriser that offers it: on by default
    [InlineData(true, false, true, null, false)]   // a hardware device: off by default
    [InlineData(true, false, true, "1", true)]     // ... asked for
    [InlineData(true, true, true, "0", false)]     // refused everywhere
    [InlineData(true, false, false, "1", false)]   // not offered
    [InlineData(false, true, true, "1", false)]    // an offscreen device never presents
    public void IncrementalPresentIsOnForASoftwareRasteriserAndAskedForElsewhere(bool presents, bool software, bool offered,
        string? setting, bool expected)
        => VulkanDevice.ChooseIncrementalPresent(presents, software, offered, setting).ShouldBe(expected);

    // The wiring, on the offscreen device (which never presents): what AddFrameDamage, MarkFullFrameDamage and a failed
    // present do to the record a present takes, and the conditions the present's rectangle is allowed under.
    private static void WithContext(OffscreenGpuFixture gpu, Action<VulkanContext> body)
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var (w, h, identity) = (ctx.SwapchainWidth, ctx.SwapchainHeight, ctx._swapchainTransformIsIdentity);
        ctx.SwapchainWidth = 64;
        ctx.SwapchainHeight = 64;
        ctx._swapchainTransformIsIdentity = true;
        try
        {
            ctx.MarkFullFrameDamage();
            ctx.TakeChangedRegionForPresent(true, out _);   // the swapchain's first present, whole: from here the record is steady
            body(ctx);
        }
        finally
        {
            ctx.MarkFullFrameDamage();
            ctx.TakeChangedRegionForPresent(true, out _);
            ctx.SwapchainWidth = w;
            ctx.SwapchainHeight = h;
            ctx._swapchainTransformIsIdentity = identity;
        }
    }

    [Collection("OffscreenGpu")]
    public sealed class Wiring(OffscreenGpuFixture gpu)
    {
        [Fact]
        public void AddFrameDamageFeedsThePresentRegion() => WithContext(gpu, ctx =>
        {
            ctx.AddFrameDamage(10f, 8f, 4f, 3f);
            ctx.TakeChangedRegionForPresent(true, out var rect).ShouldBeTrue();
            (rect.offset.x, rect.offset.y, rect.extent.width, rect.extent.height).ShouldBe((10, 8, 4u, 3u));
        });

        [Fact]
        public void MarkFullFrameDamageMakesThePresentWhole() => WithContext(gpu, ctx =>
        {
            ctx.AddFrameDamage(10f, 8f, 4f, 3f);
            ctx.MarkFullFrameDamage();
            ctx.TakeChangedRegionForPresent(true, out _).ShouldBeFalse();
        });

        [Fact]
        public void ADeviceWithoutTheExtensionOrANonIdentityTransformIsWhole() => WithContext(gpu, ctx =>
        {
            ctx.AddFrameDamage(1f, 1f, 2f, 2f);
            ctx.TakeChangedRegionForPresent(false, out _).ShouldBeFalse("the device does not offer it");

            ctx._swapchainTransformIsIdentity = false;
            ctx.AddFrameDamage(1f, 1f, 2f, 2f);
            ctx.TakeChangedRegionForPresent(true, out _).ShouldBeFalse("a rotated swapchain's rectangle is not the drawn one");
        });

        [Fact]
        public void AnHdrSwapchainIsWhole() => WithContext(gpu, ctx =>
        {
            ctx.AddFrameDamage(1f, 1f, 2f, 2f);
            ctx._hdrActive = true;
            try { ctx.TakeChangedRegionForPresent(true, out _).ShouldBeFalse("the composite repaints in full"); }
            finally { ctx._hdrActive = false; }
        });

        [Fact]
        public unsafe void NamingThePresentSetsAndClearsLastPresentRegion() => WithContext(gpu, ctx =>
        {
            VkRectLayerKHR rect = default;
            VkPresentRegionKHR region = default;
            VkPresentRegionsKHR regions = default;
            var info = new VkPresentInfoKHR();

            ctx.AddFrameDamage(10f, 8f, 4f, 3f);
            ctx.NamePresentRegion(ref info, &rect, &region, &regions);
            // The offscreen device does not offer the extension, so the present is whole and the record is null.
            ctx.LastPresentRegion.ShouldBeNull();

            ctx.LastPresentRegion = new VkRect2D(0, 0, 1, 1);
            ctx.AddFrameDamage(10f, 8f, 4f, 3f);
            ctx.NamePresentRegion(ref info, &rect, &region, &regions);
            ctx.LastPresentRegion.ShouldBeNull("a present that names nothing leaves nothing recorded");
        });

        [Fact]
        public void AFailedPresentOwesTheNextOneInFull() => WithContext(gpu, ctx =>
        {
            ctx.AddFrameDamage(1f, 1f, 2f, 2f);
            ctx.TakeChangedRegionForPresent(true, out _).ShouldBeTrue();   // the present that then failed took it
            ctx.NotePresentResult(VkResult.ErrorSurfaceLostKHR);

            ctx.AddFrameDamage(20f, 20f, 2f, 2f);
            ctx.TakeChangedRegionForPresent(true, out _).ShouldBeFalse("what the failed present owed may not have been shown");
        });

        [Theory]
        [InlineData(VkResult.Success)]
        [InlineData(VkResult.SuboptimalKHR)]
        public void ASuccessfulPresentKeepsTheNextNarrow(VkResult result) => WithContext(gpu, ctx =>
        {
            ctx.AddFrameDamage(1f, 1f, 2f, 2f);
            ctx.TakeChangedRegionForPresent(true, out _).ShouldBeTrue();
            ctx.NotePresentResult(result);

            ctx.AddFrameDamage(20f, 20f, 2f, 2f);
            ctx.TakeChangedRegionForPresent(true, out _).ShouldBeTrue();
        });
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
