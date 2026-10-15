using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static SdlVulkan.Renderer.VulkanDevice;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The two decisions behind a transparent window, which need no GPU to check: which composite-alpha mode its
/// swapchain asks for (<see cref="VulkanContext.ChooseCompositeAlpha"/>), and which device it falls back to when the
/// native one offers no premultiplied alpha (<see cref="VulkanDevice.PickAlphaCandidate"/>). The surfaces and
/// devices below are the ones measured: a Windows desktop with a GTX 1070, an Intel UHD 630 and Microsoft's
/// compatibility pack installed, whose Dozen exposes one device per D3D12 adapter plus WARP.
/// </summary>
public sealed class TransparentWindowChoiceTests
{
    private const VkCompositeAlphaFlagsKHR Opaque = VkCompositeAlphaFlagsKHR.Opaque;
    private const VkCompositeAlphaFlagsKHR Pre = VkCompositeAlphaFlagsKHR.PreMultiplied;
    private const VkCompositeAlphaFlagsKHR Post = VkCompositeAlphaFlagsKHR.PostMultiplied;
    private const VkCompositeAlphaFlagsKHR Inherit = VkCompositeAlphaFlagsKHR.Inherit;

    [Theory]
    // NVIDIA's native driver: opaque only, whatever the window wants.
    [InlineData(Opaque, false, Opaque)]
    [InlineData(Opaque, true, Opaque)]
    // Intel's adds Inherit, which a transparent window does not take either: it is not a promise of alpha.
    [InlineData(Opaque | Inherit, true, Opaque)]
    // Dozen: an ordinary window stays opaque, a transparent one takes premultiplied.
    [InlineData(Opaque | Pre | Post, false, Opaque)]
    [InlineData(Opaque | Pre | Post, true, Pre)]
    // Android surfaces that offer Inherit only keep working either way.
    [InlineData(Inherit, false, Inherit)]
    [InlineData(Inherit, true, Inherit)]
    // Post-multiplied is never what a transparent window asks for; it gets the ordinary choice instead.
    [InlineData(Post, true, Post)]
    [InlineData(Opaque | Post, true, Opaque)]
    public void ChooseCompositeAlpha_TakesPremultipliedOnlyForATransparentWindowThatIsOfferedIt(
        VkCompositeAlphaFlagsKHR supported, bool wantsAlpha, VkCompositeAlphaFlagsKHR expected)
        => VulkanContext.ChooseCompositeAlpha(supported, wantsAlpha).ShouldBe(expected);

    [Fact]
    public void PickAlphaCandidate_OnTheMeasuredDesktop_TakesDozenOnTheSameGpu()
    {
        AlphaCandidate[] devices =
        [
            default, // the native GTX 1070 itself, which the picker never asks again
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: true, VkPhysicalDeviceType.DiscreteGpu),     // Dozen on the GTX 1070
            new(CanPresent: true, OffersPremultiplied: false, SameGpu: false, VkPhysicalDeviceType.IntegratedGpu), // Intel, native
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.IntegratedGpu),  // Dozen on the Intel
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.Cpu),            // Dozen on WARP
        ];

        PickAlphaCandidate(devices).ShouldBe(1);
    }

    [Fact]
    public void PickAlphaCandidate_PrefersTheSameGpuOverAFasterOneElsewhere()
    {
        // A transparent window of an app running on the iGPU stays on the iGPU, rather than moving its work to the
        // discrete card because a translation layer there happens to be discrete.
        AlphaCandidate[] devices =
        [
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.DiscreteGpu),
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: true, VkPhysicalDeviceType.IntegratedGpu),
        ];

        PickAlphaCandidate(devices).ShouldBe(1);
    }

    [Fact]
    public void PickAlphaCandidate_WithoutLuids_RanksDiscreteThenIntegratedThenCpu()
    {
        AlphaCandidate[] devices =
        [
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.Cpu),
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.IntegratedGpu),
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.DiscreteGpu),
            new(CanPresent: true, OffersPremultiplied: true, SameGpu: false, VkPhysicalDeviceType.DiscreteGpu),
        ];

        // The first of the two discrete devices: enumeration order breaks the tie.
        PickAlphaCandidate(devices).ShouldBe(2);
        PickAlphaCandidate(devices[..2]).ShouldBe(1);
        PickAlphaCandidate(devices[..1]).ShouldBe(0);
    }

    [Fact]
    public void PickAlphaCandidate_IgnoresDevicesThatCannotPresentOrOfferNoAlpha()
    {
        AlphaCandidate[] devices =
        [
            new(CanPresent: false, OffersPremultiplied: false, SameGpu: true, VkPhysicalDeviceType.DiscreteGpu),
            new(CanPresent: true, OffersPremultiplied: false, SameGpu: true, VkPhysicalDeviceType.DiscreteGpu),
        ];

        // Windows without the compatibility pack: nothing to fall back to, so the window presents opaque.
        PickAlphaCandidate(devices).ShouldBe(-1);
        PickAlphaCandidate([]).ShouldBe(-1);
    }
}
