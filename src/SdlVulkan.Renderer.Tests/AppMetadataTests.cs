using System.Reflection;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit;
using static SDL3.SDL;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// What the OS is told about the app: the resolver (arguments, else the entry assembly) is pure, and applying it
/// is checked by reading SDL's own answer back, without <c>SDL_Init</c>: SDL allows setting metadata before it,
/// which is the point.
/// </summary>
[Collection(SdlProcessStateCollection.Name)] // SDL's metadata is process-wide, and SDL_Quit elsewhere erases it
public class AppMetadataTests
{
    // SDL3-CS binds SDL_GetAppMetadataProperty as returning bool, which drops the string, so the test reads SDL's
    // answer through its own import of the native function.
    [DllImport("SDL3", EntryPoint = "SDL_GetAppMetadataProperty")]
    private static extern nint SdlGetAppMetadataProperty([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    private static string? ReadBack(string property) => Marshal.PtrToStringUTF8(SdlGetAppMetadataProperty(property));

    [Fact]
    public void GivenFieldsAreTakenAsTheyAre()
    {
        var resolved = SdlAppMetadata.Resolve(new SdlAppMetadata("TianWen", "1.4.2", "com.sharpastro.tianwen",
            "SharpAstro", "https://example.org/tianwen"));

        resolved.Name.ShouldBe("TianWen");
        resolved.Version.ShouldBe("1.4.2");
        resolved.Identifier.ShouldBe("com.sharpastro.tianwen");
        resolved.Creator.ShouldBe("SharpAstro");
        resolved.Url.ShouldBe("https://example.org/tianwen");
    }

    [Fact]
    public void NothingGivenFallsBackToTheEntryAssembly()
    {
        var entry = Assembly.GetEntryAssembly();
        entry.ShouldNotBeNull("the test host has an entry assembly");

        var resolved = SdlAppMetadata.Resolve(null);

        resolved.Name.ShouldBe(entry.GetName().Name);
        resolved.Version.ShouldNotBeNullOrWhiteSpace();
        resolved.Version.ShouldNotContain("+", customMessage: "the source-revision suffix is dropped");
        // No identifier is invented: a made-up one would group the app wrongly.
        resolved.Identifier.ShouldBeNull();
    }

    [Fact]
    public void OnlyTheMissingFieldsAreFilledIn()
    {
        var resolved = SdlAppMetadata.Resolve(new SdlAppMetadata(Name: "Mine", Identifier: "org.example.mine"));

        resolved.Name.ShouldBe("Mine");
        resolved.Identifier.ShouldBe("org.example.mine");
        resolved.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void BlankFieldsCountAsMissing()
    {
        var resolved = SdlAppMetadata.Resolve(new SdlAppMetadata("  ", "", "\t"));

        resolved.Name.ShouldBe(Assembly.GetEntryAssembly()!.GetName().Name);
        resolved.Identifier.ShouldBeNull();
    }

    [Fact]
    public void AppliedMetadataReadsBackFromSdlWithoutInit()
    {
        SdlAppMetadata.Resolve(new SdlAppMetadata("TianWen", "1.4.2", "com.sharpastro.tianwen",
            "SharpAstro", "https://example.org/tianwen")).Apply().ShouldBeTrue();

        ReadBack(Props.AppMetadataNameString).ShouldBe("TianWen");
        ReadBack(Props.AppMetadataVersionString).ShouldBe("1.4.2");
        ReadBack(Props.AppMetadataIdentifierString).ShouldBe("com.sharpastro.tianwen");
        ReadBack(Props.AppMetadataCreatorString).ShouldBe("SharpAstro");
        ReadBack(Props.AppMetadataURLString).ShouldBe("https://example.org/tianwen");
    }

    [Fact]
    public void ApplyingTheFallbackNamesTheEntryAssembly()
    {
        SdlAppMetadata.Resolve(null).Apply().ShouldBeTrue();

        ReadBack(Props.AppMetadataNameString).ShouldBe(Assembly.GetEntryAssembly()!.GetName().Name);
        ReadBack(Props.AppMetadataVersionString).ShouldNotBeNullOrWhiteSpace();
    }
}
