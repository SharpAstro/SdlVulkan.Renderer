using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The file a mobile host's diagnostics go to by default (sebgod/chess#75): its name from the app's title, a header
/// first, one timed line per message, the last run's file replaced, every message also passed on to stderr, and no
/// failure ever thrown into the app. The iOS host itself cannot run here; this is the part of it that is
/// platform-free.
/// </summary>
public sealed class MobileLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 25, 12, 34, 56, 789, TimeSpan.FromHours(11));

    [Theory]
    [InlineData("Chess", "chess.log")]
    [InlineData("TianWen", "tianwen.log")]
    [InlineData("SDL Vulkan", "sdl-vulkan.log")]
    [InlineData("  FITS / Viewer!  ", "fits-viewer.log")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("!!!", null)]
    [InlineData(null, null)]
    public void TheFileIsNamedAfterTheTitle(string? title, string? expected)
        => MobileLog.FileNameFor(title).ShouldBe(expected);

    [Fact]
    public void EachLineIsTimedAndAlsoPassedOn()
    {
        var file = new StringWriter();
        var passedOn = new List<string>();
        var sink = MobileLog.Sink(file, passedOn.Add, () => Noon);

        sink("[startup] attempt 1: surface lost; retrying");
        sink("[ios] ready");

        passedOn.ShouldBe(["[startup] attempt 1: surface lost; retrying", "[ios] ready"]);
        file.ToString().ShouldBe("12:34:56.789 [startup] attempt 1: surface lost; retrying" + Environment.NewLine
            + "12:34:56.789 [ios] ready" + Environment.NewLine);
    }

    [Fact]
    public void AWriteThatFailsIsDroppedAndTheMessageStillPassedOn()
    {
        var passedOn = new List<string>();
        var sink = MobileLog.Sink(new FullDisk(), passedOn.Add);

        Should.NotThrow(() => sink("still here"));
        passedOn.ShouldBe(["still here"]);
    }

    [Fact]
    public void TheHeaderComesFirstAndTheLastRunsFileIsReplaced()
    {
        var dir = Directory.CreateTempSubdirectory("sdlvulkan-mobilelog-");
        var path = Path.Combine(dir.FullName, "chess.log");
        File.WriteAllText(path, "the last run" + Environment.NewLine + "its lines" + Environment.NewLine);
        try
        {
            var sink = MobileLog.Open(path, "Chess 1.0 on iPad15,3 iPadOS 26.0.1", also: null, () => Noon);
            sink.ShouldNotBeNull();
            sink("first line of this run");

            // The writer stays open for the life of the process, as on the device, so read beside it.
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            reader.ReadToEnd().ShouldBe("Chess 1.0 on iPad15,3 iPadOS 26.0.1" + Environment.NewLine
                + "12:34:56.789 first line of this run" + Environment.NewLine);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void AFileThatCannotBeOpenedSaysWhyAndLeavesTheCallerItsOwnSink()
    {
        var passedOn = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), "sdlvulkan-no-such-folder-" + Guid.NewGuid().ToString("N"), "chess.log");

        MobileLog.Open(path, "header", passedOn.Add).ShouldBeNull();

        passedOn.Count.ShouldBe(1);
        passedOn[0].ShouldStartWith($"[log] no log file at {path}: DirectoryNotFoundException");
    }

    [Fact]
    public void TheHeaderIsWrittenEvenWhereNoAppleLibraryAnswers()
    {
        // On a desktop CoreFoundation and libSystem's sysctl are absent (Linux, Windows) or answer for the host (macOS):
        // either way the line is built, and nothing throws into the app's start.
        var header = Should.NotThrow(() => AppleAppInfo.Describe("Chess", Noon));

        header.ShouldStartWith("Chess ");
        header.ShouldEndWith(", started 2026-11-25T12:34:56.7890000+11:00");
        if (!OperatingSystem.IsMacOS())
        {
            header.ShouldContain("(version unknown)");
            header.ShouldContain("on an unknown device");
        }
    }

    private sealed class FullDisk : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw new IOException("No space left on device");
    }
}
