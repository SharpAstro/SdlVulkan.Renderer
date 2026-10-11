using System.IO;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// What <see cref="SdlFileDialogs"/> hands SDL. The dialog itself needs a desktop to show on, so it is checked by
/// hand; these are the two translations that decide what the user sees in it: which files a filter offers, and
/// where the dialog starts.
/// </summary>
public class SdlFileDialogsTests
{
    [Fact]
    public void AFilterBecomesSdlsPattern() =>
        SdlFileDialogs.Pattern(new FileDialogFilter("FITS", "fits; .fit;fts;fz")).ShouldBe("fits;fit;fts;fz");

    [Fact]
    public void AFilterWithNoExtensionsMatchesEveryFile() =>
        SdlFileDialogs.Pattern(new FileDialogFilter("All files", "")).ShouldBe("*");

    [Fact]
    public void AnOpenDialogStartsInTheFolderAndNotTheOneAbove()
    {
        // The trailing separator is what makes SDL read the location as the folder to show. Without it the
        // Windows dialog opened in /data with "frames" in the name box.
        var request = new FileDialogRequest { InitialDirectory = "/data/frames", SuggestedFileName = "ignored.fits" };
        var expected = $"/data/frames{Path.DirectorySeparatorChar}";
        SdlFileDialogs.Location(SdlFileDialogs.OpenFile, request).ShouldBe(expected);
        SdlFileDialogs.Location(SdlFileDialogs.OpenFolder, request).ShouldBe(expected);
    }

    [Fact]
    public void AFolderThatAlreadyEndsInASeparatorKeepsIt() =>
        SdlFileDialogs.Location(SdlFileDialogs.OpenFile, new FileDialogRequest { InitialDirectory = "/data/frames/" })
            .ShouldBe("/data/frames/");

    [Fact]
    public void ASaveDialogStartsOnTheSuggestedNameInsideTheFolder()
    {
        var request = new FileDialogRequest { InitialDirectory = "/data/out", SuggestedFileName = "m42.png" };
        SdlFileDialogs.Location(SdlFileDialogs.SaveFile, request).ShouldBe(Path.Combine("/data/out", "m42.png"));
    }

    [Fact]
    public void ASaveDialogWithNoFolderStartsOnTheNameAlone() =>
        SdlFileDialogs.Location(SdlFileDialogs.SaveFile, new FileDialogRequest { SuggestedFileName = "m42.png" })
            .ShouldBe("m42.png");

    [Fact]
    public void NoFolderLeavesThePlatformToChoose() =>
        SdlFileDialogs.Location(SdlFileDialogs.OpenFile, new FileDialogRequest()).ShouldBeNull();
}
