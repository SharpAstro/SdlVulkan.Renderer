using System;
using System.Linq;
using SdlVulkan.Renderer;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>The swapchain format choice over fake surface-format lists: pure, no surface.</summary>
public sealed class HdrSurfaceFormatTests
{
    private static VkSurfaceFormatKHR F(VkFormat format, VkColorSpaceKHR cs = VkColorSpaceKHR.SrgbNonLinear)
        => new() { format = format, colorSpace = cs };

    private static readonly VkSurfaceFormatKHR Bgra = F(VkFormat.B8G8R8A8Unorm);
    private static readonly VkSurfaceFormatKHR BgraSrgb = F(VkFormat.B8G8R8A8Srgb);
    private static readonly VkSurfaceFormatKHR Rgba = F(VkFormat.R8G8B8A8Unorm);
    private static readonly VkSurfaceFormatKHR ScRgb =
        F(VkFormat.R16G16B16A16Sfloat, VkColorSpaceKHR.ExtendedSrgbLinearEXT);
    private static readonly VkSurfaceFormatKHR Hdr10 =
        F(VkFormat.A2B10G10R10UnormPack32, VkColorSpaceKHR.Hdr10St2084EXT);
    private static readonly VkSurfaceFormatKHR HalfSrgbNonLinear = F(VkFormat.R16G16B16A16Sfloat);

    /// <summary>The choice VulkanDevice.PickSurfaceColorFormat made before this change, copied here verbatim as the oracle.</summary>
    private static VkFormat Today(VkSurfaceFormatKHR[] formats)
    {
        if (formats.Length == 0) return VkFormat.B8G8R8A8Unorm;
        if (formats.Length == 1 && formats[0].format == VkFormat.Undefined) return VkFormat.B8G8R8A8Unorm;
        var hasBgra = false;
        var hasRgba = false;
        foreach (var f in formats)
        {
            if (f.format == VkFormat.B8G8R8A8Unorm) hasBgra = true;
            else if (f.format == VkFormat.R8G8B8A8Unorm) hasRgba = true;
        }
        if (hasBgra) return VkFormat.B8G8R8A8Unorm;
        if (hasRgba) return VkFormat.R8G8B8A8Unorm;
        return formats[0].format;
    }

    public static TheoryData<string, VkSurfaceFormatKHR[]> Lists => new()
    {
        { "hdr offered with bgra", [Bgra, ScRgb, Hdr10] },
        { "hdr offered first", [ScRgb, Bgra] },
        { "scRGB and HDR10 only", [ScRgb, Hdr10] },
        { "HDR10 only", [Hdr10] },
        { "BGRA only", [Bgra] },
        { "BGRA and sRGB variant", [BgraSrgb, Bgra] },
        { "RGBA only", [Rgba] },
        { "sRGB-coded BGRA only", [BgraSrgb] },
        { "single Undefined", [F(VkFormat.Undefined)] },
        { "half float in sRGB non-linear only", [HalfSrgbNonLinear] },
        { "empty", Array.Empty<VkSurfaceFormatKHR>() },
    };

    [Theory]
    [MemberData(nameof(Lists))]
    public void WithoutHdrAsked_TheAnswerIsTodays(string name, VkSurfaceFormatKHR[] formats)
    {
        _ = name;
        var choice = HdrSurfaceFormat.Choose(formats, hdrRequested: false);
        choice.Format.ShouldBe(Today(formats));
        choice.ColorSpace.ShouldBe(VkColorSpaceKHR.SrgbNonLinear);
        choice.IsScRgb.ShouldBeFalse();
        HdrSurfaceFormat.PickSdr(formats).ShouldBe(Today(formats));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void HdrAskedAndOffered_IsHalfFloatExtendedSrgbLinear(int which)
    {
        VkSurfaceFormatKHR[][] lists = [[Bgra, ScRgb, Hdr10], [ScRgb, Hdr10], [ScRgb]];
        var choice = HdrSurfaceFormat.Choose(lists[which], hdrRequested: true);
        choice.Format.ShouldBe(VkFormat.R16G16B16A16Sfloat);
        choice.ColorSpace.ShouldBe(VkColorSpaceKHR.ExtendedSrgbLinearEXT);
        choice.IsScRgb.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void HdrAskedButNotScRgbOffered_FallsBackToToday(string name, VkSurfaceFormatKHR[] formats)
    {
        var offersScRgb = formats.Any(f => f.format == VkFormat.R16G16B16A16Sfloat
            && f.colorSpace == VkColorSpaceKHR.ExtendedSrgbLinearEXT);
        var choice = HdrSurfaceFormat.Choose(formats, hdrRequested: true);
        if (offersScRgb)
        {
            choice.IsScRgb.ShouldBeTrue(name);
            return;
        }
        choice.Format.ShouldBe(Today(formats), name);
        choice.ColorSpace.ShouldBe(VkColorSpaceKHR.SrgbNonLinear);
    }

    [Fact]
    public void Hdr10Only_IsNotUsed_ForAnScRgbRequest()
    {
        var choice = HdrSurfaceFormat.Choose([Hdr10], hdrRequested: true);
        choice.IsScRgb.ShouldBeFalse();
        choice.Format.ShouldBe(VkFormat.A2B10G10R10UnormPack32, "today's answer for a list with neither BGRA nor RGBA is its first entry");
    }

    [Fact]
    public void HalfFloatInTheWrongColourSpace_IsNotScRgb()
    {
        HdrSurfaceFormat.Choose([HalfSrgbNonLinear, Bgra], hdrRequested: true).Format.ShouldBe(VkFormat.B8G8R8A8Unorm);
    }
}
