using System;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The retained frame's bookkeeping (<c>VulkanContext.Retained.cs</c>), without a device: one image the frame is
/// painted into, and what each swapchain image is given of it by a copy. The Vulkan half is a copy of a rectangle;
/// this is the half with an algorithm, and its failure is the kind <see cref="SwapchainDamageTests"/> describes:
/// stale pixels at particular frame counts, on the images that missed an update.
/// </summary>
public sealed class RetainedFrameTests
{
    private const int Size = 16;

    /// <summary>A surface of <see cref="Size"/> by <see cref="Size"/> cells, each holding the frame that last wrote it.</summary>
    private static int[,] Blank(int value)
    {
        var cells = new int[Size, Size];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                cells[y, x] = value;
        return cells;
    }

    private static void Copy(int[,] from, int[,] to, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
                to[y, x] = from[y, x];
    }

    // THE property. Whatever the app repaints, in whatever order the swapchain hands its images out, and whichever
    // frames never reach the GPU: the image presented is the app's frame, every cell of it.
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    public void TheImagePresentedIsTheWholeFrameWhateverWasRepainted(int seed, int imageCount)
    {
        var random = new Random(seed);
        var retainedDamage = new SwapchainDamage();
        var presentDamage = new SwapchainDamage();
        retainedDamage.Reset(imageCount);
        presentDamage.Reset(imageCount);

        var truth = Blank(0);          // what the app would draw if it drew everything now
        var retained = Blank(-1);      // garbage until painted
        var present = new int[imageCount][,];
        for (var i = 0; i < imageCount; i++) present[i] = Blank(-2 - i);

        for (var frame = 1; frame <= 400; frame++)
        {
            // The app changes something and says what: a rectangle, the whole surface, or nothing said at all.
            var said = random.Next(10);
            if (said == 0)
            {
                truth = Blank(frame);
                retainedDamage.MarkFull();
                presentDamage.MarkFull();
            }
            else if (said == 1)
            {
                truth = Blank(frame);
            }
            else
            {
                int x = random.Next(Size), y = random.Next(Size);
                int w = 1 + random.Next(Size - x), h = 1 + random.Next(Size - y);
                for (var yy = y; yy < y + h; yy++)
                    for (var xx = x; xx < x + w; xx++)
                        truth[yy, xx] = frame;
                retainedDamage.Add(x, y, w, h);
                presentDamage.Add(x, y, w, h);
            }

            // BeginFrameRenderPass: the retained image is index 0 whichever image was acquired.
            var acquired = random.Next(imageCount);
            var partial = retainedDamage.TryTake(0, out var px, out var py, out var pw, out var ph);

            if (random.Next(12) == 0)
            {
                // The frame never reached the GPU (OnFrameDropped): nothing was painted and nothing copied.
                retainedDamage.MarkImageFull(0);
                presentDamage.MarkFull();
                continue;
            }

            if (partial) Copy(truth, retained, (int)px, (int)py, (int)pw, (int)ph);
            else Copy(truth, retained, 0, 0, Size, Size);
            retained.ShouldBe(truth, $"frame {frame}: the retained image is the frame");

            // RecordRetainedCopy.
            if (VulkanContext.TakePresentRegion(presentDamage, acquired, partial, out var cx, out var cy, out var cw, out var ch))
                Copy(retained, present[acquired], (int)cx, (int)cy, (int)cw, (int)ch);
            else
                Copy(retained, present[acquired], 0, 0, Size, Size);
            present[acquired].ShouldBe(truth, $"frame {frame}: swapchain image {acquired} is the frame");
        }
    }

    // What the mode is for: after one full frame, a small change is a small repaint and a small copy on EVERY
    // swapchain image, where the per-image path repaints each of them in full once.
    [Fact]
    public void AfterAFullFrameASmallChangeIsRepaintedSmallOnEveryImage()
    {
        var retainedDamage = new SwapchainDamage();
        var presentDamage = new SwapchainDamage();
        retainedDamage.Reset(4);
        presentDamage.Reset(4);

        // The first frame: full, onto image 0.
        retainedDamage.TryTake(0, out _, out _, out _, out _).ShouldBeFalse();
        VulkanContext.TakePresentRegion(presentDamage, 0, frameWasPartial: false, out _, out _, out _, out _).ShouldBeFalse();

        for (var image = 1; image < 4; image++)
        {
            retainedDamage.Add(10, 20, 30, 40);
            presentDamage.Add(10, 20, 30, 40);
            retainedDamage.TryTake(0, out var x, out var y, out var w, out var h).ShouldBeTrue("the retained image holds the last frame");
            (x, y, w, h).ShouldBe((10f, 20f, 30f, 40f));
            // An image that has never been written is given all of the frame, by a copy.
            VulkanContext.TakePresentRegion(presentDamage, image, frameWasPartial: true, out _, out _, out _, out _).ShouldBeFalse();
        }

        // Every image has been written once: from here a change is a rectangle on each.
        for (var image = 0; image < 4; image++)
        {
            retainedDamage.Add(1, 2, 3, 4);
            presentDamage.Add(1, 2, 3, 4);
            retainedDamage.TryTake(0, out var x, out var y, out var w, out var h).ShouldBeTrue();
            (x, y, w, h).ShouldBe((1f, 2f, 3f, 4f));
            VulkanContext.TakePresentRegion(presentDamage, image, frameWasPartial: true, out _, out _, out _, out _).ShouldBeTrue();
        }
    }

    [Theory]
    // A software rasteriser takes it; a GPU does not.
    [InlineData(true, true, true, null, true)]
    [InlineData(false, true, true, null, false)]
    // Asked for on a GPU (how the path is run under a validation layer there), and refused on a rasteriser.
    [InlineData(false, true, true, "1", true)]
    [InlineData(true, true, true, "0", false)]
    // Never where the swapchain's images cannot be copied into, or the window composes its frame another way.
    [InlineData(true, true, false, null, false)]
    [InlineData(true, true, false, "1", false)]
    [InlineData(true, false, true, null, false)]
    [InlineData(true, false, true, "1", false)]
    public void WhichContextsRetainTheirFrame(bool softwareRasterizer, bool opaqueSdrSwapchain, bool transferDstOffered, string? setting, bool expected)
        => VulkanContext.ChooseRetainedFrame(softwareRasterizer, opaqueSdrSwapchain, transferDstOffered, setting).ShouldBe(expected);
}
