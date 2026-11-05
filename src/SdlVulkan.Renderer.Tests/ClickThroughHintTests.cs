using System;
using Shouldly;
using Xunit;
using static SDL3.SDL;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// SDL3 defaults <c>SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH</c> to "0", which swallows the mouse-down that activates an
/// unfocused window. The library sets it to "1" before <c>SDL_Init</c> unless the app opts out with
/// <c>clickThroughOnFocus: false</c>. Hints are plain process-global strings that SDL_GetHint reads without
/// SDL_Init, so no GPU and no window are needed. The collection keeps any other class that touches SDL hints off this one;
/// the SDL_MOUSE_FOCUS_CLICKTHROUGH environment variable, which SDL_GetHint falls back to, is cleared for the test.
/// </summary>
[Collection("SdlHints")]
public class ClickThroughHintTests : IDisposable
{
    public ClickThroughHintTests()
    {
        Environment.SetEnvironmentVariable("SDL_MOUSE_FOCUS_CLICKTHROUGH", null);
        ResetHint(Hints.MouseFocusClickthrough);
    }

    public void Dispose() => ResetHint(Hints.MouseFocusClickthrough);

    [Fact]
    public void Default_sets_the_hint_to_1()
    {
        GetHint(Hints.MouseFocusClickthrough).ShouldBeNull();

        SdlVulkanWindow.ApplyInitHints(clickThroughOnFocus: true);

        GetHint(Hints.MouseFocusClickthrough).ShouldBe("1");
    }

    [Fact]
    public void Opting_out_leaves_the_hint_unset()
    {
        SdlVulkanWindow.ApplyInitHints(clickThroughOnFocus: false);

        GetHint(Hints.MouseFocusClickthrough).ShouldBeNull();
    }

    [Fact]
    public void Opting_out_leaves_a_value_the_host_set_untouched()
    {
        SetHint(Hints.MouseFocusClickthrough, "0").ShouldBeTrue();

        SdlVulkanWindow.ApplyInitHints(clickThroughOnFocus: false);

        GetHint(Hints.MouseFocusClickthrough).ShouldBe("0");
    }
}
