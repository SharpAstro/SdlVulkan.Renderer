using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The package's buildTransitive/SdlVulkan.Renderer.targets (sebgod/chess#74), evaluated by MSBuild as a consuming app
/// would import it: a net10.0-ios project gets SDL3.xcframework and MoltenVK.xcframework as NativeReference items, named
/// so the iOS build puts them where <see cref="AppleVulkan"/> loads them from; every other target framework gets nothing;
/// the opt-out and the folder property work. Only evaluation runs here: fetching needs macOS and an iOS build needs its
/// workload, which the iOS app's own CI job exercises.
/// </summary>
public sealed class IosFrameworksTargetsTests
{
    private static readonly string Targets = Path.Combine(AppContext.BaseDirectory, "buildTransitive", "SdlVulkan.Renderer.targets");

    [Fact]
    public void AnIosAppGetsBothFrameworksWhereAppleVulkanLooks()
    {
        var items = Evaluate("net10.0-ios26.0");

        items.Select(i => i.Name).ShouldBe(["SDL3.xcframework", "MoltenVK.xcframework"], ignoreOrder: true);
        foreach (var item in items)
        {
            item.Kind.ShouldBe("Framework");
            item.SmartLink.ShouldBe("False");
            // An xcframework's slice holds <Name>.framework/<Name>, which the build copies to Frameworks/.
            var name = Path.GetFileNameWithoutExtension(item.Name);
            new[] { AppleVulkan.BundledSdl3, AppleVulkan.BundledMoltenVk }.ShouldContain($"Frameworks/{name}.framework/{name}");
        }
        items.Select(i => Path.GetDirectoryName(i.Path)).Distinct().Single()
            .ShouldEndWith(Path.Combine("obj", "sdlvulkan-ios", "sdl-3.4.16_moltenvk-v1.4.2"));
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net10.0-android")]
    [InlineData("net10.0-macos")]
    public void NoOtherTargetFrameworkGetsThem(string targetFramework)
        => Evaluate(targetFramework).ShouldBeEmpty();

    [Fact]
    public void AnAppThatBundlesItsOwnOptsOut()
        => Evaluate("net10.0-ios26.0", "-p:SdlVulkanBundleIosFrameworks=false").ShouldBeEmpty();

    [Fact]
    public void AFolderGivenOnTheCommandLineIsUsedAsAFolder()
    {
        // A global property cannot be reassigned by the targets, and this one comes without a trailing separator.
        var folder = Path.Combine(Path.GetTempPath(), "shared-ios-frameworks");
        var items = Evaluate("net10.0-ios26.0", $"-p:SdlVulkanIosFrameworksDir={folder}");

        items.Select(i => i.Path).ShouldBe(
            [Path.Combine(folder, "SDL3.xcframework"), Path.Combine(folder, "MoltenVK.xcframework")], ignoreOrder: true);
    }

    private sealed record Item(string Path, string Name, string Kind, string SmartLink);

    private static Item[] Evaluate(string targetFramework, params string[] extra)
    {
        File.Exists(Targets).ShouldBeTrue($"the targets were not copied next to the tests: {Targets}");
        var dir = Directory.CreateTempSubdirectory("sdlvulkan-ios-targets-");
        try
        {
            // A bare project: no SDK, so no workload is needed to evaluate an ios target framework.
            var project = Path.Combine(dir.FullName, "probe.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <PropertyGroup>
                    <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                  </PropertyGroup>
                  <Import Project="{Targets}" />
                </Project>
                """);

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = dir.FullName,
            };
            foreach (var arg in new[] { "msbuild", project, "-nologo", "-nodeReuse:false", $"-p:TargetFramework={targetFramework}", "-getItem:NativeReference" }.Concat(extra))
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            process.WaitForExit();
            process.ExitCode.ShouldBe(0, $"dotnet msbuild failed:\n{stdout.Result}\n{stderr.Result}");

            using var json = JsonDocument.Parse(stdout.Result);
            return json.RootElement.GetProperty("Items").GetProperty("NativeReference").EnumerateArray()
                .Select(i => new Item(
                    i.GetProperty("FullPath").GetString()!,
                    i.GetProperty("Filename").GetString() + i.GetProperty("Extension").GetString(),
                    i.GetProperty("Kind").GetString()!,
                    i.GetProperty("SmartLink").GetString()!))
                .ToArray();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
