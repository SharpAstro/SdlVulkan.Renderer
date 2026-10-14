using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using SdlVulkan.Renderer.Inspector;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The sidecar's reader of DIR.Lib's per-user inspector registry, against descriptor files written in the
/// format DIR.Lib's <c>inspector-registry.md</c> specifies. Every fixture lives in a fresh temporary folder
/// handed to the reader through its internal override; the real per-user folder is never read or touched.
/// The live process in these fixtures is the test process itself, the one pid certain to be running.
/// </summary>
public sealed class InspectorRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdlvk-inspector-registry-" + Guid.NewGuid().ToString("N"));

    private static readonly int OwnPid = Environment.ProcessId;

    /// <summary>Nothing runs under this pid on Windows or Linux, so it is what a dead app's pid looks like.</summary>
    private const int DeadPid = int.MaxValue;

    public InspectorRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static DateTime OwnStartUtc
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return self.StartTime.ToUniversalTime();
        }
    }

    /// <summary>A descriptor as the writer produces it: one line, the core's members, then the SDL host's extras.</summary>
    private static string Descriptor(int pid, int port, DateTime? processStartUtc, string kind = "sdl", string title = "Chess - game 3")
    {
        var start = processStartUtc is { } s
            ? $",\"processStart\":\"{DateTime.SpecifyKind(s, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)}\""
            : "";
        return $"{{\"app\":\"Chess\",\"kind\":\"{kind}\",\"tcpPort\":{port},\"pid\":{pid},\"proto\":1{start},"
            + $"\"title\":\"{title}\",\"startedAt\":\"2026-10-15T09:12:34.0001234+00:00\"}}";
    }

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteDescriptor(int pid, int port, DateTime? processStartUtc, string kind = "sdl")
        => WriteFile($"{pid}-{port}.json", Descriptor(pid, port, processStartUtc, kind));

    [Fact]
    public void ALiveEntry_IsListedAtLoopbackWithItsPort()
    {
        WriteDescriptor(OwnPid, 53817, OwnStartUtc);

        var listed = InspectorRegistry.Read(_dir);

        var instance = listed.ShouldHaveSingleItem();
        instance.Address.ShouldBe(IPAddress.Loopback);
        instance.TcpPort.ShouldBe(53817);
        instance.Pid.ShouldBe(OwnPid);
        instance.App.ShouldBe("Chess");
        instance.Title.ShouldBe("Chess - game 3", "the host's extras ride along, as on a multicast reply");
        instance.Proto.ShouldBe(1);
        instance.Kind.ShouldBe("sdl");
    }

    [Fact]
    public void AStartTimeWithinTolerance_OrNoneAtAll_IsStillLive()
    {
        WriteDescriptor(OwnPid, 50001, OwnStartUtc.AddSeconds(1.5));
        WriteDescriptor(OwnPid, 50002, processStartUtc: null);

        InspectorRegistry.Read(_dir).Select(i => i.TcpPort).ShouldBe([50001, 50002],
            "within 2 s is the same process, and without a recorded start the pid alone decides");
    }

    [Fact]
    public void ADeadPid_IsDroppedQuietlyAndNotDeleted()
    {
        var path = WriteDescriptor(DeadPid, 50003, OwnStartUtc);
        var notes = new List<string>();

        InspectorRegistry.Read(_dir, notes).ShouldBeEmpty();
        notes.ShouldBeEmpty("a crash's leftover is routine, and waits for the next writer's sweep");
        File.Exists(path).ShouldBeTrue("readers never delete; the next app that starts a core sweeps");
    }

    [Fact]
    public void AReusedPid_IsSkippedAndSaid_AndNotDeleted()
    {
        // Our own pid, live, but recorded as starting an hour before this process did: the file is a dead
        // app's, and the pid has since been given to someone else. On Linux the same picture can be a live app
        // after the wall clock was stepped, so the skip is reported rather than silent.
        var path = WriteDescriptor(OwnPid, 50004, OwnStartUtc.AddHours(-1));
        var notes = new List<string>();

        InspectorRegistry.Read(_dir, notes).ShouldBeEmpty();
        notes.ShouldHaveSingleItem().ShouldBe($"descriptor skipped: start-time mismatch ({path})");
        File.Exists(path).ShouldBeTrue("readers never delete; the next app that starts a core sweeps");
    }

    [Fact]
    public void AHalfWrittenOrIncompleteFile_IsSkippedWithoutFailingTheListing()
    {
        WriteFile($"{OwnPid}-50005.json", Descriptor(OwnPid, 50005, OwnStartUtc)[..40]);
        WriteFile($"{OwnPid}-50006.json", $"{{\"app\":\"Chess\",\"kind\":\"sdl\",\"pid\":{OwnPid},\"proto\":1}}");
        WriteFile($"{OwnPid}-50007.json", "");
        WriteDescriptor(OwnPid, 50008, OwnStartUtc);

        InspectorRegistry.Read(_dir).ShouldHaveSingleItem().TcpPort.ShouldBe(50008);
    }

    [Fact]
    public void AnUnusablePidOrPort_DoesNotParse()
    {
        // Pid 0 matters most: on Windows it is the Idle process, running and unreadable, so a liveness check
        // alone would keep it.
        WriteFile($"{OwnPid}-50018.json", Descriptor(pid: 0, port: 50018, processStartUtc: null));
        WriteFile($"{OwnPid}-50019.json", Descriptor(pid: -5, port: 50019, processStartUtc: null));
        WriteFile($"{OwnPid}-50020.json", Descriptor(OwnPid, port: 0, processStartUtc: null));
        WriteFile($"{OwnPid}-50021.json", Descriptor(OwnPid, port: 70000, processStartUtc: null));
        var notes = new List<string>();

        InspectorRegistry.Read(_dir, notes).ShouldBeEmpty();
        notes.ShouldBeEmpty("a file that does not parse is skipped quietly");
    }

    [Fact]
    public void AFileThatVanishesBeforeItIsRead_IsSkippedWithoutFailingTheListing()
    {
        // What an app exiting between the enumeration and the read looks like: a path that is no longer there.
        var gone = WriteDescriptor(OwnPid, 50009, OwnStartUtc);
        var live = WriteDescriptor(OwnPid, 50010, OwnStartUtc);
        File.Delete(gone);

        InspectorRegistry.Read([gone, live]).ShouldHaveSingleItem().TcpPort.ShouldBe(50010);
    }

    [Fact]
    public void OnlyDescriptorNamesAreRead()
    {
        var descriptor = Descriptor(OwnPid, 50011, OwnStartUtc);
        WriteFile($"{OwnPid}-50011.tmp", descriptor);    // the writer's in-progress temp file
        WriteFile("notes.json", descriptor);
        WriteFile($"{OwnPid}-x.json", descriptor);
        WriteFile($"-{OwnPid}-50011.json", descriptor);

        InspectorRegistry.Read(_dir).ShouldBeEmpty();
    }

    /// <summary>
    /// Computes the path only; the folder is never read here. Pinned because on a Unix account without
    /// <c>~/.local/share</c> the default option yields "" and the registry would silently have no path at all.
    /// </summary>
    [Fact]
    public void TheDefaultFolder_IsLocalAppDataDirLibInspector_ComputedWithoutVerifying()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        InspectorRegistry.DefaultDirectory.ShouldBe(Path.Combine(root, "DIR.Lib", "inspector"));
    }

    [Fact]
    public void AMissingFolder_ListsNothing()
    {
        InspectorRegistry.Read(Path.Combine(_dir, "absent")).ShouldBeEmpty();
        InspectorRegistry.Read(directory: null).ShouldBeEmpty();
    }

    /// <summary>
    /// The whole discovery, with the registry overridden and a query that fails at the socket, as one does with
    /// no network. A query to port 0 on loopback never leaves this machine and errors either way: Linux refuses
    /// the send, Windows fails the receive with a connection reset (loopback's ICMP port-unreachable). The
    /// registry still lists, and its entries are filtered by kind exactly as multicast replies are.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_ListsTheRegistryWithoutTheNetwork_AndFiltersByKind()
    {
        WriteDescriptor(OwnPid, 50012, OwnStartUtc, kind: "sdl");
        WriteDescriptor(OwnPid, 50013, OwnStartUtc, kind: "tui");
        WriteDescriptor(DeadPid, 50014, OwnStartUtc, kind: "sdl");

        var discovery = new InspectorDiscoveryClient(IPAddress.Loopback, port: 0, registryDirectory: _dir);
        var listed = await discovery.DiscoverAsync(TestContext.Current.CancellationToken);

        var instance = listed.ShouldHaveSingleItem("the tui core is a terminal and the dead one is gone");
        instance.Address.ShouldBe(IPAddress.Loopback);
        instance.TcpPort.ShouldBe(50012);
        instance.Pid.ShouldBe(OwnPid);
    }

    [Fact]
    public async Task ListInstances_ShowsAStartTimeSkipBesideTheInstances()
    {
        WriteDescriptor(OwnPid, 50015, OwnStartUtc);
        var skipped = WriteDescriptor(OwnPid, 50016, OwnStartUtc.AddHours(-1));

        var discovery = new InspectorDiscoveryClient(IPAddress.Loopback, port: 0, registryDirectory: _dir);
        var text = await InspectorTools.list_instances(discovery, TestContext.Current.CancellationToken);

        text.ShouldBe(
            $"pid={OwnPid}  app=Chess  title=Chess - game 3  127.0.0.1:50015  proto=1{Environment.NewLine}"
            + $"note: descriptor skipped: start-time mismatch ({skipped}){Environment.NewLine}");
    }

    [Fact]
    public async Task ListInstances_SaysWhyWhenTheOnlyDescriptorWasSkipped()
    {
        var skipped = WriteDescriptor(OwnPid, 50017, OwnStartUtc.AddHours(-1));

        var discovery = new InspectorDiscoveryClient(IPAddress.Loopback, port: 0, registryDirectory: _dir);
        var text = await InspectorTools.list_instances(discovery, TestContext.Current.CancellationToken);

        text.ShouldStartWith("No debuggable instances found.");
        text.ShouldContain($"note: descriptor skipped: start-time mismatch ({skipped})");
    }

    [Fact]
    public void Merge_DedupesByAddressAndPid_CountingThisMachinesAddressesAsLoopback()
    {
        var lanIp = IPAddress.Parse("192.168.1.5");     // this machine's own interface address
        var otherMachine = IPAddress.Parse("10.0.0.7");

        InspectorInstance At(IPAddress address, int pid, int port, string app)
            => new(address, port, app, null, pid, 1, "sdl");

        var registered = new[] { At(IPAddress.Loopback, 100, 5000, "registered") };
        var replied = new[]
        {
            At(lanIp, 100, 5000, "same app, LAN discovery also on"),
            At(otherMachine, 100, 6000, "another machine, same pid"),
            At(otherMachine, 100, 6000, "its second reply"),
            At(lanIp, 200, 7000, "an older local build, multicast only"),
        };

        var merged = InspectorDiscoveryClient.Merge(registered, replied, lanIp.Equals);

        merged.Select(i => i.App).ShouldBe(
            ["registered", "another machine, same pid", "an older local build, multicast only"],
            "registry entries come first; the local reply duplicating one goes, the other machine's pid stays");
        merged[2].Address.ShouldBe(lanIp, "a kept reply keeps the address it came from");
    }
}
