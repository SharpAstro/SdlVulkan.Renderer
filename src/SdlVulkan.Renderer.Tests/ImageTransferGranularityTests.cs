using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// What region an atlas flush may copy, given the queue family's minImageTransferGranularity.
///
/// <para>The atlases used to upload their dirty rectangle unconditionally, which is legal only
/// where the granularity is (1,1,1) — true of every desktop driver, and untrue of Mesa's dzn, which
/// reports (0,0,0) for "whole subresources only". Khronos validation reported it as
/// VUID-vkCmdCopyBufferToImage-imageOffset-07738 on every glyph-atlas flush under WSL; a driver is
/// free to turn that undefined behaviour into corruption, and dzn intermittently did.</para>
/// </summary>
public class ImageTransferGranularityTests
{
    [Fact]
    public void GranularityOneLeavesEveryRectangleAlone()
    {
        // The desktop case. It has to be untouched, or every flush on every normal driver would
        // start uploading more than it needs.
        var dirty = new CopyRect(117, 425, 1010, 176);
        Assert.Equal(dirty, ImageTransferGranularity.Snap(dirty, 1, 1, 1024, 1024));
    }

    [Fact]
    public void ZeroGranularityMeansTheWholeSubresource()
    {
        // dzn. Offset must be zero and the extent must be the image's own; the whole page is the
        // only legal region, and it contains whatever was dirty.
        var snapped = ImageTransferGranularity.Snap(new CopyRect(0, 117, 1010, 176), 0, 0, 1024, 1024);
        Assert.Equal(new CopyRect(0, 0, 1024, 1024), snapped);
    }

    [Fact]
    public void ADepthOnlyZeroStillMeansWhole()
    {
        // (0,0,0) is the value in the wild, but a driver reporting a zero in only one axis must not
        // be read as "granularity 0 in that axis" — nothing can be a multiple of zero.
        Assert.Equal(new CopyRect(0, 0, 512, 512),
                     ImageTransferGranularity.Snap(new CopyRect(8, 8, 16, 16), 4, 0, 512, 512));
        Assert.Equal(new CopyRect(0, 0, 512, 512),
                     ImageTransferGranularity.Snap(new CopyRect(8, 8, 16, 16), 0, 4, 512, 512));
    }

    [Fact]
    public void ACoarseGranularityRoundsTheOffsetDownAndTheEdgeUp()
    {
        // The in-between case some mobile parts report. The snapped rect must CONTAIN the dirty one
        // — a smaller upload would leave stale texels in the atlas and draw the previous glyph.
        var snapped = ImageTransferGranularity.Snap(new CopyRect(7, 9, 10, 10), 4, 4, 1024, 1024);

        Assert.Equal(4, snapped.X);
        Assert.Equal(8, snapped.Y);
        Assert.Equal(0, snapped.X % 4);
        Assert.Equal(0, snapped.Y % 4);
        Assert.True(snapped.X <= 7 && snapped.Y <= 9);
        Assert.True(snapped.X + snapped.Width >= 17);
        Assert.True(snapped.Y + snapped.Height >= 19);
    }

    [Fact]
    public void TheImageEdgeIsLegalAtAnyGranularity()
    {
        // The spec allows a region to stop at the image edge even when that leaves a partial
        // granularity block, and rounding past it would be an out-of-bounds copy.
        var snapped = ImageTransferGranularity.Snap(new CopyRect(1000, 1000, 22, 22), 16, 16, 1010, 1010);

        Assert.Equal(1010, snapped.X + snapped.Width);
        Assert.Equal(1010, snapped.Y + snapped.Height);
        Assert.True(snapped.X <= 1000 && snapped.Y <= 1000);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(2u, 2u)]
    [InlineData(4u, 4u)]
    [InlineData(8u, 4u)]
    public void TheResultAlwaysContainsTheDirtyRectAndFitsTheImage(uint gw, uint gh)
    {
        const int dim = 256;
        foreach (var (x, y, w, h) in new[] { (0, 0, 1, 1), (3, 5, 7, 9), (250, 1, 6, 255), (17, 200, 200, 56) })
        {
            var snapped = ImageTransferGranularity.Snap(new CopyRect(x, y, w, h), gw, gh, dim, dim);

            Assert.True(snapped.X <= x, $"x {snapped.X} > {x}");
            Assert.True(snapped.Y <= y, $"y {snapped.Y} > {y}");
            Assert.True(snapped.X + snapped.Width >= x + w, "right edge shrank");
            Assert.True(snapped.Y + snapped.Height >= y + h, "bottom edge shrank");
            Assert.True(snapped.X >= 0 && snapped.Y >= 0);
            Assert.True(snapped.X + snapped.Width <= dim && snapped.Y + snapped.Height <= dim);
        }
    }
}
