using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>One named span of a frame's GPU time, as <see cref="VulkanContext.LastGpuSections"/> reports it.</summary>
/// <param name="Name">The name the caller passed to <see cref="VulkanContext.BeginGpuSection"/>.</param>
/// <param name="Milliseconds">GPU time between the section's two timestamps.</param>
public readonly record struct GpuSectionTime(string Name, double Milliseconds);

// GPU frame timing: how long each frame's command buffer ran ON THE GPU, measured with timestamp
// queries rather than inferred from fence waits.
//
// Why it exists. Every GPU wedge on record (2026-08-19 in a FITS viewer, 2026-09-22 in an atlas, both
// on an Adreno X1-85) was the OS resetting this process's GPU context because one submission ran past
// the Windows GPU timeout, 2 s by default: a WATCHDOG live dump and LiveKernelEvent 141 each time. The
// CPU side of those frames looked ordinary, the fence wait only says "late", and the dump is deleted
// once it is reported, so nothing we logged could say WHAT ran long. This does: the frame total is
// reliable on every GPU, and a frame over SlowGpuFrameBudgetMs is logged with its sections.
//
// What to trust. The frame total is bracketed by timestamps OUTSIDE the render pass (top of pipe
// after the command buffer begins, bottom of pipe after the pass ends), which every implementation
// orders correctly. Section timestamps sit INSIDE the render pass, where a tiling GPU (the Adreno, any
// Mali or PowerVR) may bin the whole pass and report section boundaries that do not correspond to when
// that work ran. On an immediate-mode desktop GPU they are accurate. Read them as attribution on a
// desktop and as a hint on a tiler, never as a measurement there.
//
// Cost. Two timestamps per frame plus two per section, one reset, and one non-blocking readback of a
// slot whose fence has already been waited, so the results are there and nothing stalls. The strings
// are the caller's own references; nothing is allocated unless a frame is over budget, when the log
// line is built.
//
// Threading: render thread only, like the rest of the frame state machine (or the one job thread
// that drives an offscreen context).
public sealed unsafe partial class VulkanContext
{
    /// <summary>
    /// A frame whose GPU time exceeds this is logged with its sections. An eighth of the Windows GPU
    /// timeout: far past any frame this renderer is meant to produce, and early enough that a frame
    /// time climbing towards the timeout is on record several frames before the reset.
    /// </summary>
    public const double SlowGpuFrameBudgetMs = 250;

    /// <summary>Sections recorded per frame; <see cref="BeginGpuSection"/> ignores any past this.</summary>
    public const int MaxGpuSections = 16;

    // Per frame slot: [0] frame start, [1] frame end, then a begin/end pair per section.
    private const uint TimestampsPerSlot = 2 + 2 * MaxGpuSections;

    private bool _gpuTimingProbed;
    private bool _gpuTimingSupported;
    private VkQueryPool _timestampPool;
    private double _timestampPeriodNs;
    private ulong _timestampMask;

    // Whether the slot's LAST SUBMITTED command buffer carried timestamps, i.e. whether its query
    // range holds results worth reading once its fence has signalled. Set only by a submit that took:
    // an aborted or rejected frame wrote timestamps into a command buffer that never ran, and reading
    // that range would return stale values from an older frame, or nothing.
    private readonly bool[] _slotTimed = new bool[MaxFramesInFlight];
    private readonly bool[] _recordingTimed = new bool[MaxFramesInFlight];
    private readonly int[] _slotSectionCount = new int[MaxFramesInFlight];
    private readonly string[][] _slotSectionNames = CreateSectionNames();
    private readonly long[] _slotFrameOrdinal = new long[MaxFramesInFlight];
    private int _openSection = -1;

    private readonly GpuSectionTime[] _lastSections = new GpuSectionTime[MaxGpuSections];
    private int _lastSectionCount;

    /// <summary>
    /// GPU time of the most recently COMPLETED frame, in milliseconds, or NaN before the first one
    /// completes and on a queue without timestamp support. Lags the frame being recorded by up to
    /// <see cref="MaxFramesInFlight"/> frames, because a frame's time is only known once its fence has
    /// signalled.
    /// </summary>
    public double LastGpuFrameMs { get; private set; } = double.NaN;

    /// <summary>The frame ordinal <see cref="LastGpuFrameMs"/> belongs to.</summary>
    public long LastGpuFrameOrdinal { get; private set; }

    /// <summary>Longest GPU frame seen by this context, in milliseconds (0 before the first).</summary>
    public double PeakGpuFrameMs { get; private set; }

    /// <summary>How many completed frames exceeded <see cref="SlowGpuFrameBudgetMs"/>.</summary>
    public long SlowGpuFrames { get; private set; }

    /// <summary>
    /// The sections of the frame <see cref="LastGpuFrameMs"/> belongs to, in recording order. See the
    /// note at the top of this file on how far to trust them on a tiling GPU.
    /// </summary>
    public ReadOnlySpan<GpuSectionTime> LastGpuSections => _lastSections.AsSpan(0, _lastSectionCount);

    /// <summary>Whether this context's queue supports timestamps, so frame timing is live.</summary>
    public bool GpuTimingSupported
    {
        get
        {
            EnsureGpuTiming();
            return _gpuTimingSupported;
        }
    }

    /// <summary>
    /// Opens a named GPU section in the frame being recorded, closing any section still open (sections
    /// do not nest). A no-op outside a frame, past <see cref="MaxGpuSections"/>, or on a queue without
    /// timestamps. <paramref name="name"/> is stored by reference until the frame's results are read,
    /// so pass a literal or another string that is not rebuilt per frame.
    /// </summary>
    public void BeginGpuSection(string name)
    {
        if (!_recordingTimed[_currentFrame])
            return;
        EndGpuSection();
        var n = _slotSectionCount[_currentFrame];
        if (n >= MaxGpuSections)
            return;
        _slotSectionNames[_currentFrame][n] = name;
        _slotSectionCount[_currentFrame] = n + 1;
        _openSection = n;
        DeviceApi.vkCmdWriteTimestamp(_commandBuffers[_currentFrame], VkPipelineStageFlags.BottomOfPipe,
            _timestampPool, SlotBase(_currentFrame) + 2 + 2 * (uint)n);
    }

    /// <summary>Closes the open GPU section, if any. Safe to call when none is open.</summary>
    public void EndGpuSection()
    {
        if (_openSection < 0 || !_recordingTimed[_currentFrame])
            return;
        DeviceApi.vkCmdWriteTimestamp(_commandBuffers[_currentFrame], VkPipelineStageFlags.BottomOfPipe,
            _timestampPool, SlotBase(_currentFrame) + 3 + 2 * (uint)_openSection);
        _openSection = -1;
    }

    private static string[][] CreateSectionNames()
    {
        var names = new string[MaxFramesInFlight][];
        for (var i = 0; i < names.Length; i++)
            names[i] = new string[MaxGpuSections];
        return names;
    }

    private static uint SlotBase(int slot) => (uint)slot * TimestampsPerSlot;

    private void EnsureGpuTiming()
    {
        if (_gpuTimingProbed)
            return;
        _gpuTimingProbed = true;

        uint count = 0;
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* pFamilies = families)
            InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &count, pFamilies);
        var validBits = GraphicsQueueFamily < count ? families[GraphicsQueueFamily].timestampValidBits : 0u;
        InstanceApi.vkGetPhysicalDeviceProperties(PhysicalDevice, out var props);
        if (validBits == 0 || props.limits.timestampPeriod <= 0)
        {
            SdlVulkanLog.Logger.GpuTimingUnsupported();
            return;
        }

        VkQueryPoolCreateInfo poolCI = new()
        {
            queryType = VkQueryType.Timestamp,
            queryCount = TimestampsPerSlot * MaxFramesInFlight
        };
        if (DeviceApi.vkCreateQueryPool(&poolCI, out _timestampPool) != VkResult.Success)
        {
            _timestampPool = VkQueryPool.Null;
            SdlVulkanLog.Logger.GpuTimingUnsupported();
            return;
        }

        _timestampPeriodNs = props.limits.timestampPeriod;
        _timestampMask = validBits >= 64 ? ulong.MaxValue : (1UL << (int)validBits) - 1;
        _gpuTimingSupported = true;
    }

    // After the slot's fence is known signalled (BeginFrame's wait, or an offscreen completion wait):
    // read what its last submitted frame measured. Non-blocking, because the fence already proved the
    // work finished; a NotReady answer (a driver that makes query results visible later than the
    // fence) just skips the frame rather than stalling for it.
    private void CollectGpuTiming(int slot)
    {
        if (!_slotTimed[slot])
            return;
        _slotTimed[slot] = false;

        var sections = _slotSectionCount[slot];
        var used = 2 + 2 * (uint)sections;
        var stamps = stackalloc ulong[(int)TimestampsPerSlot];
        var result = DeviceApi.vkGetQueryPoolResults(_timestampPool, SlotBase(slot), used,
            (nuint)(used * sizeof(ulong)), stamps, sizeof(ulong), VkQueryResultFlags.Bit64);
        if (result != VkResult.Success)
            return;

        var frameMs = ToMs(stamps[0], stamps[1]);
        var names = _slotSectionNames[slot];
        for (var i = 0; i < sections; i++)
        {
            _lastSections[i] = new GpuSectionTime(names[i], ToMs(stamps[2 + 2 * i], stamps[3 + 2 * i]));
            names[i] = string.Empty;
        }
        _lastSectionCount = sections;
        LastGpuFrameMs = frameMs;
        LastGpuFrameOrdinal = _slotFrameOrdinal[slot];
        if (frameMs > PeakGpuFrameMs)
            PeakGpuFrameMs = frameMs;

        if (frameMs > SlowGpuFrameBudgetMs)
        {
            SlowGpuFrames++;
            SdlVulkanLog.Logger.GpuFrameSlow(LastGpuFrameOrdinal, frameMs, SlowGpuFrameBudgetMs, DescribeLastSections());
        }
    }

    private double ToMs(ulong begin, ulong end) => ((end - begin) & _timestampMask) * _timestampPeriodNs / 1e6;

    private string DescribeLastSections()
    {
        if (_lastSectionCount == 0)
            return "none recorded";
        var parts = new string[_lastSectionCount];
        for (var i = 0; i < _lastSectionCount; i++)
            parts[i] = $"{_lastSections[i].Name}={_lastSections[i].Milliseconds:F1}ms";
        return string.Join(", ", parts);
    }

    // Right after vkBeginCommandBuffer, outside any render pass (a query reset may not be recorded
    // inside one).
    private void BeginGpuFrameTiming(VkCommandBuffer cmd)
    {
        EnsureGpuTiming();
        _openSection = -1;
        _slotSectionCount[_currentFrame] = 0;
        _recordingTimed[_currentFrame] = _gpuTimingSupported;
        if (!_gpuTimingSupported)
            return;
        _slotFrameOrdinal[_currentFrame] = _frameOrdinal;
        DeviceApi.vkCmdResetQueryPool(cmd, _timestampPool, SlotBase(_currentFrame), TimestampsPerSlot);
        DeviceApi.vkCmdWriteTimestamp(cmd, VkPipelineStageFlags.TopOfPipe, _timestampPool, SlotBase(_currentFrame));
    }

    // After the render pass has ended and before vkEndCommandBuffer.
    private void EndGpuFrameTiming(VkCommandBuffer cmd)
    {
        if (!_recordingTimed[_currentFrame])
            return;
        EndGpuSection();
        DeviceApi.vkCmdWriteTimestamp(cmd, VkPipelineStageFlags.BottomOfPipe, _timestampPool, SlotBase(_currentFrame) + 1);
    }

    // Called with the submit's outcome, BEFORE the frame index advances.
    private void NoteGpuFrameSubmitted(bool submitted)
    {
        _slotTimed[_currentFrame] = submitted && _recordingTimed[_currentFrame];
        _recordingTimed[_currentFrame] = false;
    }

    // Recovery rebuilt the sync objects, so no slot's fence is evidence about its query range any more.
    private void ForgetGpuTiming()
    {
        Array.Clear(_slotTimed);
        Array.Clear(_recordingTimed);
        _openSection = -1;
    }

    private void DestroyGpuTiming()
    {
        if (_timestampPool != VkQueryPool.Null)
            DeviceApi.vkDestroyQueryPool(_timestampPool);
        _timestampPool = VkQueryPool.Null;
        _gpuTimingSupported = false;
    }
}
