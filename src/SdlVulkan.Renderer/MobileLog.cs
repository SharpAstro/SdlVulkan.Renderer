namespace SdlVulkan.Renderer;

/// <summary>
/// The file a mobile host's diagnostics go to by default (sebgod/chess#75). Reading a phone's or tablet's console
/// needs a Mac (iOS) or adb (Android), which a tester rarely has, so on iOS the lines also go to a file in the app's
/// Documents folder, which the Files app shows when the app's Info.plist sets <c>UIFileSharingEnabled</c>: the
/// tester opens it on the device and sends it.
///
/// <para>Platform-free on purpose, so the desktop tests can check it: the iOS host supplies the folder and the
/// header line.</para>
/// </summary>
internal static class MobileLog
{
    /// <summary>
    /// The default log file name for an app: its title in lower case, every run of characters other than letters
    /// and digits made one hyphen, plus <c>.log</c> ("Chess" is chess.log, "TianWen" tianwen.log). Null for a title
    /// with nothing usable in it.
    /// </summary>
    internal static string? FileNameFor(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var name = new System.Text.StringBuilder(title.Length);
        foreach (var c in title.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c))
                name.Append(char.ToLowerInvariant(c));
            else if (name.Length > 0 && name[^1] != '-')
                name.Append('-');
        }
        var stem = name.ToString().TrimEnd('-');
        return stem.Length > 0 ? stem + ".log" : null;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for this run, replacing the last run's file so it holds only the run being asked
    /// about, and writes <paramref name="header"/> as its first line. Returns the sink to install: every message goes
    /// to <paramref name="also"/> (stderr, the device log) and then to the file, one line each, behind the time of
    /// day. Null when the file cannot be opened, after saying why through <paramref name="also"/>.
    ///
    /// <para>The writer is synchronized (the app may log from several threads) and flushes every line, since a crash
    /// must not take the last lines with it. It is never closed: it lives as long as the process. A failed write is
    /// dropped rather than thrown, because a full disk must not end the app.</para>
    /// </summary>
    internal static Action<string>? Open(string path, string header, Action<string>? also, Func<DateTimeOffset>? clock = null)
    {
        TextWriter file;
        try
        {
            file = TextWriter.Synchronized(new StreamWriter(path, append: false) { AutoFlush = true });
            file.WriteLine(header);
        }
        catch (Exception ex)
        {
            also?.Invoke($"[log] no log file at {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        return Sink(file, also, clock);
    }

    /// <summary>The sink <see cref="Open"/> returns, over any writer.</summary>
    internal static Action<string> Sink(TextWriter file, Action<string>? also, Func<DateTimeOffset>? clock = null)
    {
        clock ??= static () => DateTimeOffset.Now;
        return message =>
        {
            also?.Invoke(message);
            try
            {
                file.WriteLine($"{clock():HH:mm:ss.fff} {message}");
            }
            catch
            {
                // A full disk, or a file the OS took away: the line still went to `also`.
            }
        };
    }
}
