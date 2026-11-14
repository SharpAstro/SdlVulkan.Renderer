using System;
using System.Linq;
using System.Reflection;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The callback SDL_RunApp makes into <see cref="SdlVulkanIosApp"/> must need no marshalling, through the mechanism
/// that is known to work under Mono's ahead-of-time compilation on a device. SDL3-CS's <c>MainFunc</c> has a
/// <c>string[] argv</c>, which Mono cannot marshal into managed code (MarshalDirectiveException at launch, the first iPad
/// run); 7.92's function pointer into an [UnmanagedCallersOnly] method aborted inside the runtime before Main. 7.93 is
/// the delegate + [MonoPInvokeCallback] that reached the callback on the device, with argv a raw pointer. No test here
/// runs on iOS, so this checks the shape instead, which any platform can see.
/// </summary>
public class IosAppCallbackTests
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly MethodInfo SdlMain = typeof(SdlVulkanIosApp).GetMethod("SdlMain", All)
        ?? throw new InvalidOperationException("SdlVulkanIosApp.SdlMain is gone; update this test with it.");

    [Fact]
    public void TheCallbackIsMarkedForMonosAheadOfTimeCompiler()
    {
        var attribute = SdlMain.GetCustomAttribute<MonoPInvokeCallbackAttribute>();
        attribute.ShouldNotBeNull();
        attribute.DelegateType.ShouldBe(typeof(SdlVulkanIosApp.RawMainFunc));
    }

    [Fact]
    public void TheCallbackAndItsDelegateTakeAndReturnOnlyBlittableValues()
    {
        Type[] expected = [typeof(int), typeof(nint)];
        SdlMain.ReturnType.ShouldBe(typeof(int));
        SdlMain.GetParameters().Select(p => p.ParameterType).ShouldBe(expected);

        var invoke = typeof(SdlVulkanIosApp.RawMainFunc).GetMethod("Invoke")!;
        invoke.ReturnType.ShouldBe(typeof(int));
        invoke.GetParameters().Select(p => p.ParameterType).ShouldBe(expected);
    }

    [Fact]
    public void NoMemberOfTheHostUsesSdl3CsMainFunc()
    {
        var mainFunc = typeof(SDL3.SDL).GetNestedType("MainFunc");
        mainFunc.ShouldNotBeNull();
        typeof(SdlVulkanIosApp).GetFields(All).Select(f => f.FieldType).ShouldNotContain(mainFunc);
        typeof(SdlVulkanIosApp).GetMethods(All)
            .SelectMany(m => m.GetParameters()).Select(p => p.ParameterType).ShouldNotContain(mainFunc);
    }
}
