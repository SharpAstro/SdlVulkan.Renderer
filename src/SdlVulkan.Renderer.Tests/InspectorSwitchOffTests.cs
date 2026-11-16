using System;
using System.IO;
using System.Runtime.CompilerServices;
using DIR.Lib.Diagnostics;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The switch-off path of <see cref="DebugInspector"/> Attach (DIR.Lib's inspector feature switch off, as in a
/// published app without -p:DirInspectorSupport=true), driven through the internal seam. In memory: no process,
/// no wait. The class shares the collection of the other tests that set DIR_INSPECTOR.
/// </summary>
[Collection("InspectorEnvironment")]
public class InspectorSwitchOffTests
{
    private static SdlWindowView View() => (SdlWindowView)RuntimeHelpers.GetUninitializedObject(typeof(SdlWindowView));

    private static string Run(string? optIn, out DebugInspector inspector, out SdlEventLoop loop, out Action hook)
    {
        var old = Environment.GetEnvironmentVariable(DebugInspectorCore.OptInVariable);
        Environment.SetEnvironmentVariable(DebugInspectorCore.OptInVariable, optIn);
        try
        {
            loop = new SdlEventLoop();
            Action h = () => { };
            hook = h;
            loop.OnLoopIteration = h;
            var err = new StringWriter();
            inspector = DebugInspector.Attach(loop, View(), new DebugInspectorOptions { EnableDiscovery = false }, supported: false, err);
            return err.ToString();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DebugInspectorCore.OptInVariable, old);
        }
    }

    [Fact]
    public void SwitchOff_OptedIn_IsInert_AndSaysWhyOnStderr()
    {
        var text = Run("1", out var inspector, out var loop, out var hook);
        using (inspector)
        {
            inspector.IsActive.ShouldBeFalse();
            inspector.Port.ShouldBe(0);
            loop.OnLoopIteration.ShouldBeSameAs(hook);
            text.ShouldContain("[inspector]");
            text.ShouldContain("trimmed out of this build");
            text.ShouldContain("DirInspectorSupport=true");
        }
    }

    [Fact]
    public void SwitchOff_NotOptedIn_IsInert_AndSilent()
    {
        var text = Run(null, out var inspector, out var loop, out var hook);
        using (inspector)
        {
            inspector.IsActive.ShouldBeFalse();
            inspector.Port.ShouldBe(0);
            loop.OnLoopIteration.ShouldBeSameAs(hook);
            text.ShouldBeEmpty();
        }
    }
}
