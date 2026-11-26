using System;

namespace SdlVulkan.Renderer;

/// <summary>
/// Takes the collector's pause between two frames rather than inside one. Set on
/// <see cref="SdlEventLoop.BetweenFrames"/>, the loop asks it after every iteration that presented a frame.
/// </summary>
/// <remarks>
/// <para>
/// A generation-0 collection stops every thread for a few milliseconds, and the runtime starts one at the allocation
/// that uses up the generation's budget. A GUI's render thread allocates most of what the app does, and it does so
/// while it paints, so that allocation is nearly always inside a paint: in TianWen's GUI, 511 of the 1,489 paints over
/// 8 ms in one traced session had a collection in them. A frame that has just been presented has the rest of its
/// interval to spare, so a collection asked for THERE, once most of the budget is used, costs no frame anything, and
/// the budget it hands back is one the next paints do not run out of.
/// </para>
/// <para>
/// The budget is the runtime's own, and it is not one number: about 6 MB between collections in TianWen, but 3.2 to
/// 4.1 MB in the PDF viewer on the same machine and runtime (traced 2026-11-26, against a "desired" gen-0 size the
/// runtime reported as 4.1 to 6.0 MB), and of the settings that claim to raise it only the <c>DOTNET_GCgen0size</c>
/// environment variable does, which a program cannot set for itself. A fixed threshold fits one app and never fires in
/// the other: at 4 MB the viewer's runtime always collected first, and this did nothing (0 of 13 collections). So the
/// threshold is LEARNED: every collection the runtime starts itself says how much it let the process allocate first,
/// and the threshold is three quarters of a running average of that, never above the constructor's ceiling and never
/// below <see cref="MinimumThresholdBytes"/>. A collection that came after less than that minimum (an induced one, a
/// large allocation's) says nothing about the budget and is not counted.
/// </para>
/// <para>Moved here from TianWen (TianWen.UI.Abstractions), so every app on this loop takes its pauses the same way.</para>
/// </remarks>
public sealed class BetweenFramesCollector
{
    /// <summary>The most that is ever waited for: two thirds of the 6 MB the runtime allowed generation 0 in TianWen.</summary>
    public const long DefaultThresholdBytes = 4L * 1024 * 1024;

    /// <summary>The least that is ever waited for, and the least a runtime collection must come after to say anything
    /// about the budget.</summary>
    public const long MinimumThresholdBytes = 1L * 1024 * 1024;

    private readonly long _ceiling;
    private readonly Func<long> _allocated;
    private readonly Func<int> _collections;
    private readonly Action _collect;
    private long _allocatedAtLastCollection;
    private int _collectionsAtLastLook;

    // The running average of what the runtime let the process allocate between collections it started; 0 until the first.
    private long _observedBudget;

    /// <param name="thresholdBytes">The most that is allocated, process-wide, since the last collection before one is
    /// asked for. The threshold in use starts there and comes down to what the runtime is seen to allow.</param>
    public BetweenFramesCollector(long thresholdBytes = DefaultThresholdBytes)
        : this(thresholdBytes, static () => GC.GetTotalAllocatedBytes(), static () => GC.CollectionCount(0),
            static () => GC.Collect(0, GCCollectionMode.Forced, blocking: true))
    {
    }

    /// <summary>The seam the tests use: what has been allocated, how many gen-0 collections there have been, and how
    /// to collect.</summary>
    internal BetweenFramesCollector(long thresholdBytes, Func<long> allocated, Func<int> collections, Action collect)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdBytes, 1);
        _ceiling = thresholdBytes;
        ThresholdBytes = thresholdBytes;
        _allocated = allocated;
        _collections = collections;
        _collect = collect;
        _allocatedAtLastCollection = allocated();
        _collectionsAtLastLook = collections();
    }

    /// <summary>How many collections this asked for.</summary>
    public int Collections { get; private set; }

    /// <summary>How much is allocated since the last collection before one is asked for now: the constructor's
    /// threshold until the runtime has been seen collecting on its own, then three quarters of what it allows.</summary>
    public long ThresholdBytes { get; private set; }

    /// <summary>
    /// Called once a frame has been presented. Collects generation 0 when <see cref="AllocatedSinceCollection"/> has
    /// reached <see cref="ThresholdBytes"/>, and says whether it did.
    /// </summary>
    public bool AfterFrame()
    {
        var collections = _collections();
        var allocated = _allocated();
        if (collections != _collectionsAtLastLook)
        {
            // The runtime collected since the last frame: what was allocated since the collection before is (within a
            // frame's allocation) the budget it allowed, and its budget starts again from about here. Shared out over
            // every collection since the last look, since there can be several: no frame is presented while the app is
            // idle and its background threads allocate, nor while this is not set on the loop.
            Learn((allocated - _allocatedAtLastCollection) / (collections - _collectionsAtLastLook));
            _collectionsAtLastLook = collections;
            _allocatedAtLastCollection = allocated;
            return false;
        }

        if (allocated - _allocatedAtLastCollection < ThresholdBytes)
        {
            return false;
        }

        _collect();
        _collectionsAtLastLook = _collections();
        _allocatedAtLastCollection = _allocated();
        Collections++;
        return true;
    }

    /// <summary>What has been allocated, process-wide, since the last collection this saw.</summary>
    public long AllocatedSinceCollection => _allocated() - _allocatedAtLastCollection;

    private void Learn(long allowed)
    {
        if (allowed < MinimumThresholdBytes)
        {
            return;
        }

        // Half the weight on the newest: the budget moves with what survives, and two or three collections are enough
        // to follow it, while one odd interval moves the threshold only halfway.
        _observedBudget = _observedBudget == 0 ? allowed : (_observedBudget + allowed) / 2;
        ThresholdBytes = Math.Clamp(_observedBudget * 3 / 4, Math.Min(MinimumThresholdBytes, _ceiling), _ceiling);
    }
}
