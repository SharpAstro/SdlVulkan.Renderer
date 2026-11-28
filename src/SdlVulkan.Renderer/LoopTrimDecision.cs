namespace SdlVulkan.Renderer;

/// <summary>What the loop does with a view on a window event, when <see cref="SdlEventLoop.TrimWhenHidden"/> may be on.</summary>
internal enum LoopTrimAction
{
    /// <summary>Nothing to do for the trim.</summary>
    None,
    /// <summary>Call <c>VkRenderer.Trim</c> and draw nothing until the window is shown again.</summary>
    Trim,
    /// <summary>Stop holding frames back; the next frame begin rebuilds what the trim released.</summary>
    Resume,
}

/// <summary>The pure decision behind the loop's trim opt-in: no SDL, no GPU.</summary>
internal static class LoopTrimDecision
{
    internal enum Kind { Hidden, Minimized, Shown, Restored, Other }

    /// <param name="kind">The window event.</param>
    /// <param name="android">Android keeps its own surface-loss path; the opt-in never applies there.</param>
    /// <param name="optIn"><see cref="SdlEventLoop.TrimWhenHidden"/>.</param>
    /// <param name="alreadyTrimmed">The view is already held trimmed by the loop.</param>
    internal static LoopTrimAction Decide(Kind kind, bool android, bool optIn, bool alreadyTrimmed)
    {
        if (android) return LoopTrimAction.None;
        switch (kind)
        {
            case Kind.Hidden:
            case Kind.Minimized:
                return optIn && !alreadyTrimmed ? LoopTrimAction.Trim : LoopTrimAction.None;
            case Kind.Shown:
            case Kind.Restored:
                // Resume whether or not the opt-in is still on: a view trimmed earlier must not stay dark.
                return alreadyTrimmed ? LoopTrimAction.Resume : LoopTrimAction.None;
            default:
                return LoopTrimAction.None;
        }
    }
}
