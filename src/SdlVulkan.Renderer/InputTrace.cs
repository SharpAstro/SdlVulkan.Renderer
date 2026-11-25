using System;

namespace SdlVulkan.Renderer;

/// <summary>
/// The input half of <c>SDLVK_FRAME_LOG</c>: what the loop dispatched since the last summary, by where it came from, and how
/// long each event had waited in SDL's queue before the loop handed it on. One line a second while input arrives.
/// </summary>
/// <remarks>
/// <para>
/// It exists because a frame line says what a frame cost but not what asked for it: "touch feels sluggish next to the mouse"
/// could not be told from a log (TianWen, 2026-11-25), since SDL turns a touch drag into mouse events that reach a host exactly
/// as the mouse's do. The source is SDL's own mark on each mouse event (<c>SDL_TOUCH_MOUSEID</c>, <c>SDL_PEN_MOUSEID</c>), so a
/// touch-synthesised drag is counted apart from a real mouse, and the raw finger events beside both.
/// </para>
/// <para>
/// The age is SDL's timestamp on the event against SDL's clock as the loop dispatches it: time the event spent queued while
/// the loop was busy or asleep, which is the input lag the loop adds. Time spent before SDL saw the event (a remote desktop's
/// transport, a driver) is not in it.
/// </para>
/// </remarks>
internal sealed class InputTrace
{
    /// <summary>Where an input event came from.</summary>
    internal enum Source
    {
        /// <summary>A mouse or a touchpad driving the pointer.</summary>
        Mouse,
        /// <summary>A mouse event SDL synthesised from a touchscreen.</summary>
        TouchMouse,
        /// <summary>A mouse event SDL synthesised from a pen.</summary>
        PenMouse,
        /// <summary>A raw finger event (down, motion, up).</summary>
        Finger,
        /// <summary>A wheel or a touchpad's scroll.</summary>
        Wheel,
        /// <summary>A key down or up.</summary>
        Key,
    }

    /// <summary>What was dispatched over <see cref="Seconds"/>, and how long it had waited in SDL's queue.</summary>
    internal readonly record struct Summary(
        int Mouse, int TouchMouse, int PenMouse, int Finger, int Wheel, int Key, double AgeAvgMs, double AgeMaxMs, double Seconds);

    private readonly int[] _counts = new int[6];
    private double _ageSumMs;
    private double _ageMaxMs;
    private int _aged;
    private bool _any;
    private long _since;

    /// <summary>The source of a mouse event from SDL's id for the mouse that sent it.</summary>
    internal static Source MouseSource(uint which, uint touchMouseId, uint penMouseId)
        => which == touchMouseId ? Source.TouchMouse : which == penMouseId ? Source.PenMouse : Source.Mouse;

    /// <summary>Counts one event, and its age in SDL's queue when it is known (a negative age is not).</summary>
    internal void Count(Source source, double ageMs)
    {
        _counts[(int)source]++;
        _any = true;
        if (ageMs >= 0)
        {
            _ageSumMs += ageMs;
            _aged++;
            _ageMaxMs = Math.Max(_ageMaxMs, ageMs);
        }
    }

    /// <summary>
    /// The summary since the last one, once a second has passed and something was counted; resets the tally when it answers.
    /// The first ask only starts the clock.
    /// </summary>
    internal bool TryTakeSummary(long now, long ticksPerSecond, out Summary summary)
    {
        summary = default;
        if (_since == 0)
        {
            _since = now;
            return false;
        }

        var elapsed = (now - _since) / (double)ticksPerSecond;
        if (elapsed < 1.0)
        {
            return false;
        }

        _since = now;
        if (!_any)
        {
            return false;
        }

        summary = new Summary(
            _counts[(int)Source.Mouse], _counts[(int)Source.TouchMouse], _counts[(int)Source.PenMouse],
            _counts[(int)Source.Finger], _counts[(int)Source.Wheel], _counts[(int)Source.Key],
            _aged > 0 ? _ageSumMs / _aged : 0, _ageMaxMs, elapsed);
        Array.Clear(_counts);
        _ageSumMs = 0;
        _ageMaxMs = 0;
        _aged = 0;
        _any = false;
        return true;
    }
}
