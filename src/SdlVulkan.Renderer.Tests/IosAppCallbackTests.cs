using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The callback SDL_RunApp makes into <see cref="SdlVulkanIosApp"/> must need no marshalling. Mono, the iOS runtime,
/// cannot build a native-to-managed wrapper for an unsized array, and SDL3-CS's <c>MainFunc</c> has a
/// <c>string[] argv</c>: on the first iPad run that ended the app at launch with a MarshalDirectiveException (7.92).
/// No test here runs on iOS, so this checks the cause instead, which any platform can see.
/// </summary>
public class IosAppCallbackTests
{
    private static readonly MethodInfo SdlMain = typeof(SdlVulkanIosApp).GetMethod(
        "SdlMain", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SdlVulkanIosApp.SdlMain is gone; update this test with it.");

    [Fact]
    public void TheCallbackIsAnUnmanagedEntryPoint()
    {
        SdlMain.GetCustomAttribute<UnmanagedCallersOnlyAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void TheCallbackTakesAndReturnsOnlyBlittableValues()
    {
        SdlMain.ReturnType.ShouldBe(typeof(int));
        SdlMain.GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(int), typeof(nint)]);
    }

    [Fact]
    public void NoMemberOfTheHostUsesSdl3CsMainFunc()
    {
        var mainFunc = typeof(SDL3.SDL).GetNestedType("MainFunc");
        mainFunc.ShouldNotBeNull();
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        typeof(SdlVulkanIosApp).GetFields(all).Select(f => f.FieldType).ShouldNotContain(mainFunc);
    }
}
