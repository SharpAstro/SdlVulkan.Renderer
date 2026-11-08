using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// What SDL says about the display a window is on, as far as HDR output goes. Read from SDL events (display changed,
/// HDR state changed), never per frame: <see cref="SdlVulkanWindow.HdrDisplay"/> holds the latest.
/// </summary>
/// <param name="HdrActive">The window is on an HDR display with HDR on (<c>SDL_PROP_WINDOW_HDR_ENABLED_BOOLEAN</c> and
/// <c>SDL_PROP_DISPLAY_HDR_ENABLED_BOOLEAN</c> both true). On Windows the window's values follow the HDR desktop and
/// only the display property says whether the panel itself is HDR, hence both.</param>
/// <param name="SdrWhiteScRgb">The display's SDR white in scRGB units (1.0 = 80 cd/m²), the unit
/// <c>SDL_PROP_WINDOW_SDR_WHITE_LEVEL_FLOAT</c> is in; what <see cref="VulkanContext.SdrWhiteScRgb"/> takes.</param>
/// <param name="Headroom">How far above SDR white the display reaches (<c>SDL_PROP_WINDOW_HDR_HEADROOM_FLOAT</c>),
/// at least 1; what <see cref="VulkanContext.HdrHeadroom"/> takes.</param>
public readonly record struct HdrDisplayInfo(bool HdrActive, float SdrWhiteScRgb, float Headroom)
{
    /// <summary>An SDR display: no HDR, SDR white at 1.0 (80 cd/m²), no headroom. What a window reads before SDL has
    /// been asked.</summary>
    public static HdrDisplayInfo Sdr => new(false, 1f, 1f);

    /// <summary>The display's peak in scRGB units (1.0 = 80 cd/m²): <see cref="SdrWhiteScRgb"/> times
    /// <see cref="Headroom"/>.</summary>
    public float PeakScRgb => SdrWhiteScRgb * Headroom;

    /// <summary>
    /// The facts from SDL's raw property values. SDL reports 0 (or its default) for what a platform does not know, so
    /// a value that is not a positive finite number reads as 1, and a headroom below 1 as 1: the same bounds
    /// <see cref="VulkanContext.SdrWhiteScRgb"/> and <see cref="VulkanContext.HdrHeadroom"/> enforce, so the facts can
    /// always be applied.
    /// </summary>
    public static HdrDisplayInfo From(bool windowHdr, bool displayHdr, float sdrWhite, float headroom)
        => new(windowHdr && displayHdr,
            float.IsFinite(sdrWhite) && sdrWhite > 0f ? sdrWhite : 1f,
            float.IsFinite(headroom) && headroom >= 1f ? headroom : 1f);
}

/// <summary>
/// What <see cref="Decide"/> carries from one decision to the next, and the decision itself: what to do when SDL reports
/// that a window's display or HDR state changed, as a pure function of the facts and of what the surface offers now. No
/// SDL, no surface, so it is tested against fakes (<c>HdrDisplayStateTests</c>).
/// </summary>
/// <param name="Facts">The display facts the composite is current with.</param>
/// <param name="ScRgbInUse">The swapchain is scRGB (the context's <see cref="VulkanContext.HdrActive"/>).</param>
public readonly record struct HdrDisplayState(HdrDisplayInfo Facts, bool ScRgbInUse)
{
    /// <summary>
    /// The swapchain follows the surface, the composite values follow the facts. The swapchain is rebuilt when HDR was
    /// asked for and whether the surface offers scRGB (<see cref="HdrSurfaceFormat.Choose"/>, the very choice the context
    /// makes when it builds a swapchain) differs from what the swapchain is: a window moved to a display that lacks it,
    /// or back; HDR switched on or off in the OS where that changes what the surface offers. A change of SDR white or
    /// headroom alone rebuilds nothing, it only asks for the composite values to be brought current. When HDR was not
    /// asked for nothing is ever rebuilt or applied: the facts are only recorded, so an SDR app pays no more than the
    /// event itself.
    /// </summary>
    /// <param name="previous">The state the context is in now.</param>
    /// <param name="facts">What SDL says now.</param>
    /// <param name="offered">What the surface offers now (<c>vkGetPhysicalDeviceSurfaceFormatsKHR</c>); not read when
    /// <paramref name="wantsHdr"/> is false.</param>
    /// <param name="wantsHdr">The context was asked for an HDR swapchain (<see cref="VulkanContext.HdrRequested"/>).</param>
    public static HdrDisplayDecision Decide(
        HdrDisplayState previous, HdrDisplayInfo facts, ReadOnlySpan<VkSurfaceFormatKHR> offered, bool wantsHdr)
    {
        var factsChanged = facts != previous.Facts;
        if (!wantsHdr)
            return new HdrDisplayDecision(new HdrDisplayState(facts, false), false, factsChanged, false);

        var scRgbOffered = HdrSurfaceFormat.Choose(offered, hdrRequested: true).IsScRgb;
        var recreate = scRgbOffered != previous.ScRgbInUse;
        var composite = facts.SdrWhiteScRgb != previous.Facts.SdrWhiteScRgb || facts.Headroom != previous.Facts.Headroom;
        return new HdrDisplayDecision(new HdrDisplayState(facts, scRgbOffered), recreate, factsChanged, composite);
    }
}

/// <summary>The answer of <see cref="HdrDisplayState.Decide"/>.</summary>
/// <param name="State">The state after the decision (the new facts, and whether the swapchain will be scRGB).</param>
/// <param name="RecreateSwapchain">The swapchain must be rebuilt: HDR was asked for and scRGB became available, or
/// went away.</param>
/// <param name="FactsChanged">The facts differ from the previous ones: raise the change event.</param>
/// <param name="UpdateComposite">SDR white or headroom changed and HDR was asked for: the composite values must be
/// brought current. No rebuild is needed for this alone.</param>
public readonly record struct HdrDisplayDecision(
    HdrDisplayState State, bool RecreateSwapchain, bool FactsChanged, bool UpdateComposite);
