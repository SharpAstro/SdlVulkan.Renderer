using System;

namespace SdlVulkan.Renderer;

/// <summary>
/// When the event loop asks a window's <see cref="SdlWindowView.CheckNeedsRedraw"/> between frames: at most about once a
/// display refresh. (Right before a frame is drawn the loop asks it too, whenever its pass has not; that is
/// <c>SdlEventLoop.Run</c>'s rule, not this pacing.)
/// </summary>
/// <remarks>
/// <para>
/// The check exists to notice what changed OUTSIDE the loop's own input (a background task's completion, a mirror's
/// reading), and the only thing its answer can do is arm a frame, which is drawn no sooner than it is due. Asked on every
/// wake, as it was, a burst of input asked it thousands of times a second for sixty frames: a GUI dragging its sky map
/// asked 3,700 times a second for 55 frames (TianWen, 2026-11-25), every one of them a host's whole check for nothing.
/// </para>
/// <para>
/// Idle, the loop wakes every 16 ms unless the window's host says otherwise, and an idle window must still be asked on
/// every one of those wakes, or an external change waits two of them. So the spacing is 90 percent of a refresh and never
/// more than <see cref="MaxSpacing"/>: a wake a timer tick early, or a display slower than 60 Hz, is still asked.
/// </para>
/// <para>
/// <b>A host that knows when it could next owe a frame says so</b> (<see cref="SdlWindowView.IdleCheckInterval"/>), and
/// an idle window is then left alone until that time, an event, or <see cref="SdlEventLoop.Wake"/>. Sixty asks a second
/// of a window nothing is happening to were sixty wakes of the thread, each with the loop's own hooks behind it, for an
/// answer that was "no" every time: more of an idle GUI's render thread than its frames (TianWen, 2026-11-25). Two
/// things bound the interval whatever the host states: for <see cref="InputSettle"/> after input the window is asked at
/// the refresh's pace, since what input starts often falls due a moment later (a tooltip's delay, a double click's
/// window); and never longer than <see cref="MaxIdle"/>, since the loop's per-pass hook is a host's proof of life.
/// </para>
/// </remarks>
internal static class RedrawCheckPacing
{
    /// <summary>The longest the check is spaced, under the loop's 16 ms idle wake with a millisecond to spare.</summary>
    internal static readonly TimeSpan MaxSpacing = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Whether the paced check is due at <paramref name="now"/>, given the timestamp <see cref="NextDue"/> returned at the
    /// last ask (0 before the first, so the first is always due).
    /// </summary>
    internal static bool Due(long now, long nextCheckDue) => now >= nextCheckDue;

    /// <summary>
    /// When the next paced check falls due after one asked at <paramref name="now"/>: 90 percent of a refresh
    /// (<paramref name="frameIntervalTicks"/>) later, and no more than <see cref="MaxSpacing"/>.
    /// </summary>
    internal static long NextDue(long now, long frameIntervalTicks, long ticksPerSecond)
        => now + Spacing(frameIntervalTicks, ticksPerSecond);

    /// <summary>How long after input a window is asked at the refresh's pace, whatever idle interval its host stated.</summary>
    internal static readonly TimeSpan InputSettle = TimeSpan.FromSeconds(1);

    /// <summary>The longest a window is left unasked, whatever idle interval its host stated.</summary>
    internal static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(1);

    /// <summary>The paced spacing itself: 90 percent of a refresh, and no more than <see cref="MaxSpacing"/>.</summary>
    internal static long Spacing(long frameIntervalTicks, long ticksPerSecond)
        => Math.Min(frameIntervalTicks * 90 / 100, (long)(MaxSpacing.TotalSeconds * ticksPerSecond));

    /// <summary>
    /// When the next check falls due after one asked at <paramref name="now"/> of a window that owes no frame: its
    /// host's idle interval later, where the host stated one longer than the paced spacing and no input came within
    /// <see cref="InputSettle"/>; the paced spacing otherwise.
    /// </summary>
    /// <param name="idleTicks">The host's <see cref="SdlWindowView.IdleCheckInterval"/>, in ticks; 0 for none stated.</param>
    /// <param name="ticksSinceInput">How long ago the loop last dispatched an event of the system's (input, a window
    /// event); <see cref="long.MaxValue"/> for never.</param>
    internal static long NextDue(long now, long frameIntervalTicks, long ticksPerSecond, long idleTicks, long ticksSinceInput)
    {
        var spacing = Spacing(frameIntervalTicks, ticksPerSecond);
        if (idleTicks <= spacing || ticksSinceInput < (long)(InputSettle.TotalSeconds * ticksPerSecond))
        {
            return now + spacing;
        }

        return now + Math.Min(idleTicks, (long)(MaxIdle.TotalSeconds * ticksPerSecond));
    }

    /// <summary>
    /// The due time after an event or a wake: something may have changed, so the check is due a paced spacing after it
    /// was last asked, however far its host's idle interval had put it. Never later than it already was.
    /// </summary>
    internal static long PulledIn(long nextCheckDue, long lastCheck, long frameIntervalTicks, long ticksPerSecond)
        => Math.Min(nextCheckDue, lastCheck + Spacing(frameIntervalTicks, ticksPerSecond));

    /// <summary>
    /// How long the loop waits for an event with no frame owed: until the earliest check falls due, never under the
    /// loop's base 16 ms (a check due sooner is asked on that wake, as it always was) nor over <see cref="MaxIdle"/>.
    /// </summary>
    /// <param name="ticksUntilCheck">Ticks until the earliest check falls due; zero or less when one already is.</param>
    internal static int IdleWaitMilliseconds(long ticksUntilCheck, long ticksPerSecond)
        => (int)Math.Clamp(ticksUntilCheck * 1000 / ticksPerSecond, 16, (long)MaxIdle.TotalMilliseconds);
}
