using System;

namespace SdlVulkan.Renderer;

/// <summary>
/// When a window on a software rasteriser may draw again, by what its drawing has cost: the upper limit on what a
/// gesture (a pan, a drag, anything that asks for a frame per pointer move) may take of the machine.
/// </summary>
/// <remarks>
/// <para><b>Why the display's interval is not enough.</b> The loop already draws no faster than the display refreshes,
/// which bounds a GPU's work. A software rasteriser (llvmpipe in a VM, a board with no GPU driver) does a frame's
/// work on the processor, on every core it has, and one frame can take longer than a refresh: a consumer's sky atlas
/// panned at 25 frames a second on llvmpipe took 2.7 of 4 cores, with nothing saying it might not take all four from
/// whatever else the machine was doing (a camera download, a guider loop).</para>
/// <para><b>The rule.</b> After a frame, the window rests until the process has used no more than
/// <c>share</c> of the machine's processors since that frame BEGAN, counted as processor time over wall time; and
/// never longer than <c>maxHold</c> after the frame ended. The first half is the limit. The second is what keeps the
/// limit from becoming a stall: processor time is the whole process's, so work on other threads (a stack integrating,
/// a catalogue loading) is counted too, and without a bound a busy process would never be let draw.</para>
/// <para><b>Software rasterisers only</b> (<see cref="VulkanDevice.IsSoftwareRasterizer"/>): there the drawing IS
/// processor time. On a GPU the process's processor time says nothing about what a frame cost, and pacing by it would
/// slow the window down whenever the app computed something.</para>
/// </remarks>
public static class FrameCostPacing
{
    /// <summary>The share of the machine's processors a window's drawing may take, unless the loop says otherwise.</summary>
    public const double DefaultShare = 0.5;

    /// <summary>The longest a window rests after a frame for cost alone, unless the loop says otherwise.</summary>
    public static readonly TimeSpan DefaultMaxHold = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The timestamp before which the window does not draw again for cost.
    /// </summary>
    /// <param name="frameStart">When the last frame began.</param>
    /// <param name="frameEnd">When it was handed to the presentation engine.</param>
    /// <param name="processorSinceStart">The processor time the process has used since <paramref name="frameStart"/>,
    /// on every core. It goes on growing after the frame ends while a rasteriser's threads finish it, so the answer is
    /// asked again on each pass of the loop.</param>
    /// <param name="processors">The machine's processor count.</param>
    /// <param name="share">The share of them drawing may take; zero or less for no limit.</param>
    /// <param name="maxHoldTicks">The longest rest after <paramref name="frameEnd"/>, in timestamp ticks.</param>
    /// <param name="ticksPerSecond">The timestamp's frequency.</param>
    public static long DueTimestamp(long frameStart, long frameEnd, TimeSpan processorSinceStart, int processors,
        double share, long maxHoldTicks, long ticksPerSecond)
    {
        if (!(share > 0.0) || processors <= 0 || processorSinceStart <= TimeSpan.Zero)
        {
            return frameStart;
        }

        // processor / (wall * processors) <= share  <=>  wall >= processor / (processors * share)
        var rest = (long)(processorSinceStart.TotalSeconds / (processors * share) * ticksPerSecond);
        return Math.Min(frameStart + rest, frameEnd + maxHoldTicks);
    }
}
