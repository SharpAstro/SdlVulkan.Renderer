using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>
/// Reads DIR.Lib's per-user inspector registry: one <c>&lt;pid&gt;-&lt;tcpPort&gt;.json</c> descriptor per running
/// debug-inspector core, which is how a DEBUG app built on DIR.Lib 11.21 or later is found on its own machine
/// without binding a socket a firewall would ask about. This project does not reference DIR.Lib, so the
/// contract is DIR.Lib's <c>src/DIR.Lib/Diagnostics/inspector-registry.md</c>, implemented here by hand.
/// <para>
/// A reader never deletes. A descriptor left by a crash is dropped by the liveness rule (<see cref="Check"/>)
/// and swept by the next app that starts a core; one whose pid is running but whose start time disagrees is
/// skipped AND reported, since on Linux that can be a live instance after a wall-clock step.
/// </para>
/// </summary>
internal static class InspectorRegistry
{
    /// <summary>
    /// How far apart the recorded and the observed start time of a pid may be and still be the same process.
    /// The writer's own tolerance; a reused pid is separated from the original by the dead app's lifetime.
    /// </summary>
    internal static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>LocalApplicationData/DIR.Lib/inspector</c>, computed as the writer computes it. Null where the OS
    /// gives no per-user data folder, in which case nothing can have registered.
    /// <para>
    /// <see cref="Environment.SpecialFolderOption.DoNotVerify"/>, as the spec says: with the default option a
    /// Unix <c>GetFolderPath</c> returns "" while <c>~/.local/share</c> (or <c>$XDG_DATA_HOME</c>) does not exist
    /// yet, which would hide the folder's path rather than report it empty. A folder that is not there is no
    /// instance registered, and <see cref="Read(string, ICollection{string})"/> lists nothing for it.
    /// </para>
    /// </summary>
    internal static string? DefaultDirectory
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } root
            ? Path.Combine(root, "DIR.Lib", "inspector")
            : null;

    /// <summary>What the liveness rule made of a descriptor's pid.</summary>
    internal enum Liveness
    {
        /// <summary>Running, and the same process that wrote the descriptor (or its start time cannot be read).</summary>
        Live,

        /// <summary>Not running: a descriptor left by a crash, waiting for the next writer's sweep.</summary>
        Dead,

        /// <summary>
        /// Running, but started more than <see cref="StartTimeTolerance"/> away from the recorded time. Usually a
        /// reused pid; on Linux, where .NET derives a start time from the boot time, also a live instance after
        /// the wall clock was stepped. The two cannot be told apart here, so this one is reported, not dropped
        /// silently.
        /// </summary>
        StartMismatch,
    }

    /// <summary>
    /// The live instances registered in <paramref name="directory"/>, each at <c>127.0.0.1</c> (the command
    /// server is loopback-only). A missing or unreadable folder means none. Not filtered by kind; the caller
    /// filters registry and multicast results by the same rule. A descriptor skipped although its pid is
    /// running adds a line to <paramref name="notes"/>.
    /// </summary>
    internal static IReadOnlyList<InspectorInstance> Read(string? directory, ICollection<string>? notes = null)
    {
        if (string.IsNullOrEmpty(directory)) return [];

        string[] files;
        try { files = Directory.GetFiles(directory, "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        // Sorted so a process hosting several cores (one per window) lists the same one first every time.
        Array.Sort(files, StringComparer.Ordinal);
        return Read(files, notes);
    }

    /// <summary>
    /// The live instances among <paramref name="files"/>. Split from the enumeration so a test can hand it a
    /// path that has already gone, which is what an app exiting between enumeration and read looks like.
    /// </summary>
    internal static IReadOnlyList<InspectorInstance> Read(IEnumerable<string> files, ICollection<string>? notes = null)
    {
        var found = new List<InspectorInstance>();
        foreach (var file in files)
        {
            if (!IsDescriptorName(Path.GetFileName(file))) continue;
            if (TryReadAllBytes(file) is not { } bytes) continue;
            if (!InspectorDiscoveryClient.TryParseDescriptor(bytes, IPAddress.Loopback, out var instance, out var processStart))
                continue;   // half-written by something other than the writer, or lacking pid / tcpPort

            switch (Check(instance!.Pid, processStart))
            {
                case Liveness.Live:
                    found.Add(instance);
                    break;
                case Liveness.StartMismatch:
                    notes?.Add($"descriptor skipped: start-time mismatch ({file})");
                    break;
            }
        }
        return found;
    }

    /// <summary>
    /// Opens with <see cref="FileShare.ReadWrite"/> and <see cref="FileShare.Delete"/>, reads, closes at once.
    /// Sharing Delete lets the app's Dispose remove the file under us; nothing lets a rename through on Windows,
    /// so the handle is held no longer than the read. Null when the file is gone (its app just exited) or stays
    /// unreadable: an open that loses a race with the writer's replace (access denied or a sharing violation,
    /// for the moment the old file is being swapped out) is retried briefly first, so a live instance does not
    /// drop out of a listing because its title changed.
    /// </summary>
    private static byte[]? TryReadAllBytes(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2) return null;
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// The registry's liveness rule, as the writer's sweep applies it: the pid is running, and, when the
    /// descriptor recorded a start time, it is the same process rather than a later one given the same pid.
    /// When the start time cannot be read (access denied) the pid alone decides.
    /// </summary>
    internal static Liveness Check(int pid, DateTime? processStartUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return Liveness.Dead;
            if (processStartUtc is not { } recorded) return Liveness.Live;
            return (process.StartTime.ToUniversalTime() - recorded).Duration() <= StartTimeTolerance
                ? Liveness.Live
                : Liveness.StartMismatch;
        }
        catch (ArgumentException) { return Liveness.Dead; }          // no process with that pid
        catch (InvalidOperationException) { return Liveness.Dead; }  // it exited while being asked
        catch (Exception) { return Liveness.Live; }                   // cannot tell (access denied): the pid decides
    }

    /// <summary>
    /// Only <c>&lt;pid&gt;-&lt;tcpPort&gt;.json</c>, both decimal. The writer's temp file ends in <c>.tmp</c> and
    /// anything else in the folder is not ours to read.
    /// </summary>
    internal static bool IsDescriptorName(string name)
    {
        if (!name.EndsWith(".json", StringComparison.Ordinal)) return false;

        var stem = name.AsSpan(0, name.Length - ".json".Length);
        var dash = stem.IndexOf('-');
        return dash > 0
            && int.TryParse(stem[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(stem[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
