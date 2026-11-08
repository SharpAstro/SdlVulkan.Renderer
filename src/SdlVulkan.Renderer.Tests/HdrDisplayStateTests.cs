using SdlVulkan.Renderer;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>What the loop does when SDL says the display or the HDR state changed, over fake facts and fake surface
/// lists: pure, no SDL, no surface, no GPU.</summary>
public sealed class HdrDisplayStateTests
{
    private static VkSurfaceFormatKHR F(VkFormat format, VkColorSpaceKHR cs = VkColorSpaceKHR.SrgbNonLinear)
        => new() { format = format, colorSpace = cs };

    private static readonly VkSurfaceFormatKHR[] SdrOnly = [F(VkFormat.B8G8R8A8Unorm)];
    private static readonly VkSurfaceFormatKHR[] WithScRgb =
        [F(VkFormat.B8G8R8A8Unorm), F(VkFormat.R16G16B16A16Sfloat, VkColorSpaceKHR.ExtendedSrgbLinearEXT)];
    private static readonly VkSurfaceFormatKHR[] Hdr10Only =
        [F(VkFormat.B8G8R8A8Unorm), F(VkFormat.A2B10G10R10UnormPack32, VkColorSpaceKHR.Hdr10St2084EXT)];

    private static readonly HdrDisplayInfo Sdr = HdrDisplayInfo.Sdr;
    private static readonly HdrDisplayInfo Hdr = new(true, 2f, 2.31f);

    [Fact]
    public void HdrTurnedOn_WhereTheSurfaceNowOffersScRgb_RecreatesAndAdoptsTheFacts()
    {
        var d = HdrDisplayState.Decide(new(Sdr, ScRgbInUse: false), Hdr, WithScRgb, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeTrue();
        d.FactsChanged.ShouldBeTrue();
        d.UpdateComposite.ShouldBeTrue();
        d.State.ShouldBe(new HdrDisplayState(Hdr, ScRgbInUse: true));
    }

    [Fact]
    public void HdrTurnedOff_WhereTheSurfaceStopsOfferingScRgb_RecreatesBackToSdr()
    {
        var d = HdrDisplayState.Decide(new(Hdr, ScRgbInUse: true), Sdr, SdrOnly, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeTrue();
        d.FactsChanged.ShouldBeTrue();
        d.UpdateComposite.ShouldBeTrue();
        d.State.ShouldBe(new HdrDisplayState(Sdr, ScRgbInUse: false));
    }

    [Fact]
    public void MovedToADisplayWithoutScRgb_AndBack()
    {
        var onHdr = new HdrDisplayState(Hdr, ScRgbInUse: true);

        var away = HdrDisplayState.Decide(onHdr, Sdr, SdrOnly, wantsHdr: true);
        away.RecreateSwapchain.ShouldBeTrue();
        away.State.ScRgbInUse.ShouldBeFalse();

        var back = HdrDisplayState.Decide(away.State, Hdr, WithScRgb, wantsHdr: true);
        back.RecreateSwapchain.ShouldBeTrue();
        back.State.ShouldBe(onHdr);
    }

    [Fact]
    public void MovedBetweenTwoDisplaysThatBothOfferScRgb_RecreatesNothing()
    {
        var other = Hdr with { SdrWhiteScRgb = 3f };

        var d = HdrDisplayState.Decide(new(Hdr, ScRgbInUse: true), other, WithScRgb, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeFalse();
        d.UpdateComposite.ShouldBeTrue();
    }

    [Fact]
    public void SdrWhiteChangedAlone_UpdatesTheComposite_AndRebuildsNothing()
    {
        var brighter = Hdr with { SdrWhiteScRgb = 3.5f };

        var d = HdrDisplayState.Decide(new(Hdr, ScRgbInUse: true), brighter, WithScRgb, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeFalse();
        d.FactsChanged.ShouldBeTrue();
        d.UpdateComposite.ShouldBeTrue();
        d.State.ShouldBe(new HdrDisplayState(brighter, ScRgbInUse: true));
    }

    [Fact]
    public void HeadroomChangedAlone_UpdatesTheComposite_AndRebuildsNothing()
    {
        var d = HdrDisplayState.Decide(new(Hdr, ScRgbInUse: true), Hdr with { Headroom = 4f }, WithScRgb, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeFalse();
        d.UpdateComposite.ShouldBeTrue();
    }

    [Fact]
    public void NothingChanged_DoesNothing()
    {
        var d = HdrDisplayState.Decide(new(Hdr, ScRgbInUse: true), Hdr, WithScRgb, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeFalse();
        d.FactsChanged.ShouldBeFalse();
        d.UpdateComposite.ShouldBeFalse();
    }

    [Fact]
    public void ScRgbAlreadyOfferedButNotInUse_RecreatesEvenWithTheFactsUnchanged()
    {
        // A display change that moved the window to a surface offering scRGB, with equal facts: the swapchain still follows.
        var d = HdrDisplayState.Decide(new(Hdr, ScRgbInUse: false), Hdr, WithScRgb, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeTrue();
        d.FactsChanged.ShouldBeFalse();
    }

    [Fact]
    public void AnHdr10OnlySurface_IsNotScRgb_SoNothingToAdopt()
    {
        var d = HdrDisplayState.Decide(new(Sdr, ScRgbInUse: false), Hdr, Hdr10Only, wantsHdr: true);

        d.RecreateSwapchain.ShouldBeFalse();
        d.State.ScRgbInUse.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HdrNotAsked_NeverRecreatesAndNeverUpdatesTheComposite(bool surfaceOffersScRgb)
    {
        var offered = surfaceOffersScRgb ? WithScRgb : SdrOnly;

        var turnedOn = HdrDisplayState.Decide(new(Sdr, ScRgbInUse: false), Hdr, offered, wantsHdr: false);
        turnedOn.RecreateSwapchain.ShouldBeFalse();
        turnedOn.UpdateComposite.ShouldBeFalse();
        turnedOn.FactsChanged.ShouldBeTrue("the facts are still recorded and the change still raised");
        turnedOn.State.ShouldBe(new HdrDisplayState(Hdr, ScRgbInUse: false));

        var whiteMoved = HdrDisplayState.Decide(turnedOn.State, Hdr with { SdrWhiteScRgb = 3f }, offered, wantsHdr: false);
        whiteMoved.RecreateSwapchain.ShouldBeFalse();
        whiteMoved.UpdateComposite.ShouldBeFalse();
    }

    [Fact]
    public void HdrNotAsked_DoesNotLookAtTheSurfaceList()
    {
        // An SDR app's decision does not depend on, or read, the offered formats.
        var d = HdrDisplayState.Decide(new(Sdr, ScRgbInUse: false), Hdr, default, wantsHdr: false);

        d.RecreateSwapchain.ShouldBeFalse();
    }

    [Fact]
    public void Peak_IsSdrWhiteTimesHeadroom()
        => Hdr.PeakScRgb.ShouldBe(2f * 2.31f);

    [Theory]
    [InlineData(true, true, 2f, 2.31f, true, 2f, 2.31f)]
    [InlineData(true, false, 2f, 2.31f, false, 2f, 2.31f)]   // the window follows the HDR desktop, the panel says no
    [InlineData(false, true, 2f, 2.31f, false, 2f, 2.31f)]
    [InlineData(true, true, 0f, 0f, true, 1f, 1f)]           // SDL reports 0 for what a platform does not know
    [InlineData(true, true, float.NaN, 0.5f, true, 1f, 1f)]
    [InlineData(true, true, float.PositiveInfinity, float.PositiveInfinity, true, 1f, 1f)]
    public void From_ReadsSdlsValuesAndSanitisesWhatTheContextWouldRefuse(
        bool windowHdr, bool displayHdr, float white, float headroom, bool active, float wantWhite, float wantHeadroom)
        => HdrDisplayInfo.From(windowHdr, displayHdr, white, headroom)
            .ShouldBe(new HdrDisplayInfo(active, wantWhite, wantHeadroom));
}
