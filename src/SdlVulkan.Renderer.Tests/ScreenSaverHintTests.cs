using System;
using Shouldly;
using Xunit;
using static SDL3.SDL;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// SDL holds the display on by default while a window is open; the library chooses on purpose instead
/// (issue #156). SDL hints and the screensaver flag are process-global, so these run alone.
/// </summary>
[Collection("SdlHints")]
public class ScreenSaverHintTests
{
    // SDL falls back to this environment variable over a normal-priority SetHint, so clear it, as ClickThroughHintTests does.
    public ScreenSaverHintTests() => Environment.SetEnvironmentVariable("SDL_VIDEO_ALLOW_SCREENSAVER", null);

    [Fact]
    public void ByDefaultTheHintLetsTheDisplaySleep()
    {
        // No SDL_Init needed: a hint is just a string SDL reads when video starts.
        SdlVulkanWindow.ApplyDisplaySleepHint(keepDisplayAwake: false);
        GetHint(Hints.VideoAllowScreensaver).ShouldBe("1");
    }

    [Fact]
    public void KeepDisplayAwakeSetsTheHintToHoldTheDisplay()
    {
        SdlVulkanWindow.ApplyDisplaySleepHint(keepDisplayAwake: true);
        try
        {
            GetHint(Hints.VideoAllowScreensaver).ShouldBe("0");
        }
        finally
        {
            SdlVulkanWindow.ApplyDisplaySleepHint(keepDisplayAwake: false);
        }
    }

    [Theory]
    [InlineData("dummy")]
    [InlineData("offscreen")]
    public void TheRuntimeCallFlipsWhetherTheScreenSaverIsEnabled(string headlessDriver)
    {
        // SDL's headless video drivers need no display. Skip, with SDL's reason, where video cannot init or the
        // driver has no screensaver support (SDL answers "That operation is not supported").
        SetHint(Hints.VideoDriver, headlessDriver);
        SdlVulkanWindow.ApplyDisplaySleepHint(keepDisplayAwake: false);
        if (!Init(InitFlags.Video))
        {
            var error = GetError();
            SetHint(Hints.VideoDriver, "");
            Assert.Skip($"SDL video cannot init on the {headlessDriver} driver: {error}");
        }

        string? unsupported = null;
        try
        {
            // The hint was "1", so SDL started with the screensaver allowed.
            ScreenSaverEnabled().ShouldBeTrue();

            if (!DisableScreenSaver())
            {
                unsupported = GetError();
            }
            else
            {
                EnableScreenSaver().ShouldBeTrue();
                // The library's own wrapper, which KeepDisplayAwake(bool) calls.
                SdlVulkanWindow.SetKeepDisplayAwake(true);
                ScreenSaverEnabled().ShouldBeFalse();
                SdlVulkanWindow.SetKeepDisplayAwake(false);
                ScreenSaverEnabled().ShouldBeTrue();
            }
        }
        finally
        {
            Quit();
            SetHint(Hints.VideoDriver, "");
        }

        if (unsupported is not null)
            Assert.Skip($"The {headlessDriver} driver cannot toggle the screensaver: {unsupported}");
    }
}
