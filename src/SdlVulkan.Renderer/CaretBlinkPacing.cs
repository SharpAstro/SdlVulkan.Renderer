using System;
using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// The caret blink's share of the event loop, as pure functions of a window's <see cref="WindowUiSettings"/>, the phase
/// last painted and a <see cref="TimeProvider"/>. The loop owns the blink: before each frame it sets the phase on the
/// window's settings, and while a text field has the keyboard it asks for a frame at each phase change and only then.
/// </summary>
/// <remarks>No wall-clock reads of its own: every decision reads the clock it is given, so a fake clock tests it in memory.</remarks>
internal static class CaretBlinkPacing
{
    /// <summary>The "painted phase" of a view that has not drawn yet, so its first focused frame is owed.</summary>
    internal const long NeverPainted = -1;

    /// <summary>Whether a text field of this window has the keyboard.</summary>
    internal static bool HasFocusedField(WindowUiSettings? ui) => ui?.Focus.Current is not null;

    /// <summary>Whether a frame is owed now: a field has the keyboard and the phase moved since the last paint.</summary>
    internal static bool FrameOwed(WindowUiSettings? ui, long paintedPhase, TimeProvider clock)
        => HasFocusedField(ui) && CaretBlink.PhaseAt(clock) != paintedPhase;

    /// <summary>Milliseconds from now to the next phase change, at least 1.</summary>
    internal static int MillisecondsToNextChange(TimeProvider clock)
    {
        var now = (long)(clock.GetTimestamp() * (1000.0 / clock.TimestampFrequency));
        var into = now % CaretBlink.PhaseMilliseconds;
        return (int)Math.Max(1, CaretBlink.PhaseMilliseconds - into);
    }

    /// <summary>The per-frame step: sets the window's phase from the clock and returns it, the phase the frame paints in.</summary>
    internal static long Step(WindowUiSettings ui, TimeProvider clock)
    {
        var phase = CaretBlink.PhaseAt(clock);
        ui.CaretPhase = phase;
        return phase;
    }
}
