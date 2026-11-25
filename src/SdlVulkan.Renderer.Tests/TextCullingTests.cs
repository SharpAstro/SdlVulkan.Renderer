using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// A text that cannot put ink inside the region a pass may paint is dropped before it is shaped
/// (<see cref="TextCulling"/>). The bound has to err one way only: a text wrongly dropped is a hole in the repainted
/// region, a text wrongly kept costs what it always did.
/// </summary>
public sealed class TextCullingTests
{
    // A 2880 x 1814 window whose frame repaints its clock: a strip in the top bar's right corner.
    private const int ClockX = 2600, ClockY = 0, ClockW = 280, ClockH = 48;

    private const float FontSize = 20f;
    private const float LineHeight = 26f;

    private static bool ReachesTheClock(string text, TextAlign align, float x, float y, float w, float h)
        => TextCulling.MayReach(text.Length, text.Split('\n').Length, FontSize, LineHeight, align, x, y, w, h,
            ClockX, ClockY, ClockW, ClockH);

    // The frame this exists for: every label of a sky map, a thousand pixels below the strip being repainted.
    [Theory]
    [InlineData(TextAlign.Near)]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Far)]
    public void ALabelFarBelowTheRepaintedStripIsDropped(TextAlign align)
        => ReachesTheClock("Triangulum Australe", align, 2650f, 900f, 200f, 26f).ShouldBeFalse();

    [Theory]
    [InlineData(TextAlign.Near)]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Far)]
    public void TheClocksOwnTextIsKept(TextAlign align)
        => ReachesTheClock("Wed 25 Nov 17:51:23", align, 2610f, 8f, 260f, 32f).ShouldBeTrue();

    // A line is not confined to its layout box: one longer than the box runs out of it, to the right when it starts at
    // the left edge. Dropped for where its BOX is, it would leave a hole where its ink was.
    [Fact]
    public void ALineThatRunsOutOfItsBoxIntoTheRegionIsKept()
        => ReachesTheClock("a long status line that runs well past the fifty pixel box it was given", TextAlign.Near, 2400f, 10f, 50f, 26f)
            .ShouldBeTrue();

    // And to the left when it ends at the right edge.
    [Fact]
    public void ARightAlignedLineReachesLeftOfItsBox()
    {
        const string line = "right aligned and long";

        // The box sits right of the region; the line ends at the box's right edge and runs back across the region.
        TextCulling.MayReach(line.Length, 1, FontSize, LineHeight, TextAlign.Far, 900f, 100f, 40f, 26f,
            regionX: 600, regionY: 90, regionW: 200, regionH: 50).ShouldBeTrue();

        // Left aligned in the same box it runs the other way, away from the region.
        TextCulling.MayReach(line.Length, 1, FontSize, LineHeight, TextAlign.Near, 900f, 100f, 40f, 26f,
            regionX: 600, regionY: 90, regionW: 200, regionH: 50).ShouldBeFalse();
    }

    // The block of lines may be taller than its box, and sits at its top, middle or bottom: kept wherever it could be.
    [Fact]
    public void LinesTallerThanTheirBoxAreKeptAboveAndBelowIt()
    {
        const string block = "one\ntwo\nthree\nfour\nfive\nsix";

        // Six lines are 156 px; the box is 26 px at y = 300. Bottom-aligned, the block starts 130 px above the box.
        TextCulling.MayReach(block.Length, 6, FontSize, LineHeight, TextAlign.Near, 100f, 300f, 200f, 26f,
            regionX: 0, regionY: 180, regionW: 400, regionH: 20).ShouldBeTrue();
        // Top-aligned, it ends 130 px below it.
        TextCulling.MayReach(block.Length, 6, FontSize, LineHeight, TextAlign.Near, 100f, 300f, 200f, 26f,
            regionX: 0, regionY: 440, regionW: 400, regionH: 20).ShouldBeTrue();
        // Past both, with an em to spare, it cannot be.
        TextCulling.MayReach(block.Length, 6, FontSize, LineHeight, TextAlign.Near, 100f, 300f, 200f, 26f,
            regionX: 0, regionY: 520, regionW: 400, regionH: 20).ShouldBeFalse();
    }

    // A full frame's region is the whole surface: everything on it is kept, and only what lies off it is dropped.
    [Fact]
    public void OnAFullFrameOnlyWhatLiesOffTheSurfaceIsDropped()
    {
        TextCulling.MayReach(5, 1, FontSize, LineHeight, TextAlign.Near, 1400f, 900f, 100f, 26f, 0, 0, 2880, 1814).ShouldBeTrue();
        TextCulling.MayReach(5, 1, FontSize, LineHeight, TextAlign.Near, 1400f, 2400f, 100f, 26f, 0, 0, 2880, 1814).ShouldBeFalse();
        TextCulling.MayReach(5, 1, FontSize, LineHeight, TextAlign.Near, 3400f, 900f, 100f, 26f, 0, 0, 2880, 1814).ShouldBeFalse();
    }

    // The bound for a glyph's advance is two ems, wider than any face's widest (an emoji is about one and a third).
    [Fact]
    public void TheBoundIsWiderThanAnyGlyph()
    {
        // Ten characters at 20 px: at most 400 px of line and an em of padding, from x = 1000.
        TextCulling.MayReach(10, 1, FontSize, LineHeight, TextAlign.Near, 1000f, 100f, 0f, 26f, 1410, 90, 50, 50).ShouldBeTrue();
        TextCulling.MayReach(10, 1, FontSize, LineHeight, TextAlign.Near, 1000f, 100f, 0f, 26f, 1425, 90, 50, 50).ShouldBeFalse();
    }

    // Nothing that is not a number is judged: it is drawn as it always was.
    [Theory]
    [InlineData(float.NaN, 100f)]
    [InlineData(100f, float.PositiveInfinity)]
    public void ALayoutThatIsNotFiniteIsKept(float x, float y)
        => TextCulling.MayReach(5, 1, FontSize, LineHeight, TextAlign.Near, x, y, 100f, 26f, ClockX, ClockY, ClockW, ClockH).ShouldBeTrue();
}
