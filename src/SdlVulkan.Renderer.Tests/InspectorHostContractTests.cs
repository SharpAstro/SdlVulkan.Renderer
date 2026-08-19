#if DEBUG
using System;
using System.Linq;
using DIR.Lib.Diagnostics;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// What <see cref="DebugInspector"/> now promises the shared core, checked without an SDL window.
///
/// <para>These are the seams the migration onto <see cref="DebugInspectorCore"/> created, and each one is a
/// silent failure if it drifts: a wrong <c>SurfaceKind</c> makes the app INVISIBLE to its sidecar rather than
/// mislabelled (that exact mistake already happened once on the terminal side), and a <c>batch</c> that
/// reappeared in <c>SteppedMethods</c> would shadow the core's scheduler with a verb SDL no longer
/// implements.</para>
/// </summary>
public class InspectorHostContractTests
{
    [Fact]
    public void ItImplementsTheSharedHostContracts()
    {
        typeof(IDebugInspectorHost).IsAssignableFrom(typeof(DebugInspector)).ShouldBeTrue();
        typeof(IDebugInspectorSteppedHost).IsAssignableFrom(typeof(DebugInspector)).ShouldBeTrue();
    }

    /// <summary>
    /// The tag a sidecar filters on. Wrong or absent, and this app is dropped from discovery entirely — which
    /// looks exactly like the app failing to start.
    /// </summary>
    [Fact]
    public void ItDeclaresItselfAsAnSdlSurface()
        => ((IDebugInspectorHost)FormatterServicesStandIn()).SurfaceKind.ShouldBe("sdl");

    /// <summary>
    /// The frame-spanning verbs are <c>pressHold</c> and <c>screenshot</c> — the latter because a capture
    /// rides the NEXT presented frame's command buffer and fence (the one legal way to read a swapchain
    /// image; an instantaneous screenshot could only read an image the process no longer owns). <c>batch</c>
    /// and <c>wait</c> moved into the core — they are pure scheduling with no SDL in them — so listing
    /// either here would route a request to a <c>Begin</c> that no longer builds one.
    /// </summary>
    [Fact]
    public void TheFrameSpanningVerbsArePressHoldAndScreenshot()
    {
        var stepped = (IDebugInspectorSteppedHost)FormatterServicesStandIn();

        stepped.SteppedMethods.ShouldBe(["pressHold", "screenshot"]);
        stepped.SteppedMethods.ShouldNotContain("batch", "the core schedules batches now");
        stepped.SteppedMethods.ShouldNotContain("wait", "wait only means anything as a batch step");
    }

    /// <summary>
    /// The addressing options are gone rather than accepted-and-ignored. Worth asserting because the old
    /// default was <c>IPAddress.Any</c> — the command server, which injects input and captures the
    /// framebuffer, accepted LAN connections. The core binds loopback with no opt-out.
    /// </summary>
    [Fact]
    public void TheTransportOptionsAreGone_SoNoCallerCanBelieveItStillChoosesAPort()
    {
        var names = typeof(DebugInspectorOptions).GetProperties().Select(p => p.Name).ToArray();

        names.ShouldNotContain("BindAddress");
        names.ShouldNotContain("Port");
        names.ShouldNotContain("DiscoveryGroup");
        names.ShouldNotContain("DiscoveryPort");
        names.ShouldContain("EnableDiscovery", "not announcing yourself is still a real choice");
    }

    /// <summary>
    /// An uninitialised instance, purely to read the members that do not touch SDL. Constructing a real one
    /// needs a window and a Vulkan device, and none of the contract above depends on either.
    /// </summary>
    private static object FormatterServicesStandIn()
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DebugInspector));
}
#endif
