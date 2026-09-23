#if DEBUG
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// DEBUG-only: makes a <see cref="VulkanDevice"/>'s queue report the failures a GPU wedge leaves behind,
/// so every path that answers one can be driven on demand, on a healthy GPU, without waiting for the
/// driver to fail on its own.
/// </summary>
/// <remarks>
/// <para>Nothing here touches the GPU. A faked submit is answered <em>instead of</em> calling
/// <c>vkQueueSubmit</c>, which is what makes it faithful: a submit the driver rejects never executes
/// either (measured 2026-08-04, see <c>VulkanContext.SubmitFrame</c>), so the renderer finds exactly
/// the state a real rejection leaves, down to the reset fence with nothing behind it.</para>
/// <para>Two faults, the two a wedge has actually produced:</para>
/// <list type="bullet">
/// <item><see cref="RejectSubmits"/>: <c>VK_ERROR_INITIALIZATION_FAILED</c>, which is what the Qualcomm
/// Adreno X1-85 driver returns for every submit once an engine timeout has reset its context (not a
/// spec-legal return there). A count models the transient (two rejections, then work resumes); no count
/// models the dead device (every submit, for minutes, on 2026-09-22).</item>
/// <item><see cref="LoseDevice"/>: <c>VK_ERROR_DEVICE_LOST</c>, the spec's answer, which drivers on
/// other hardware give. Sticky, as a real loss is: every later submit reports it too.</item>
/// </list>
/// <para>The fault belongs to the DEVICE, not a window, because the queue does: under a shared
/// <see cref="VulkanDevice"/> every window presenting from it sees the fault at once, as they would a
/// real one. Arming and disarming are logged at Warning so a log that contains a fault also says it was
/// injected; the lines the renderer writes in reply are the real ones, deliberately.</para>
/// <para>What it cannot fake is the part that is the driver's alone: whether teardown blocks on a
/// genuinely hung device, and whether a new device on the same GPU works after a real reset.</para>
/// </remarks>
public sealed class GpuFaultInjection
{
    /// <summary>Sentinel for "reject until <see cref="Clear"/>".</summary>
    private const int Unlimited = int.MaxValue;

    // Written by whoever arms the fault (an inspector verb on the render thread, a test) and consumed by
    // every submit on the device, which on a shared device can be several threads' worth of callers
    // (an offscreen job, a one-shot upload). Interlocked on both sides; nothing here needs a lock.
    private int _rejectsRemaining;
    private volatile bool _deviceLost;
    private long _faked;

    /// <summary>
    /// Reject the next <paramref name="count"/> submits with <c>VK_ERROR_INITIALIZATION_FAILED</c>, or every
    /// submit until <see cref="Clear"/> when <paramref name="count"/> is null. Replaces any earlier count.
    /// </summary>
    public void RejectSubmits(int? count = null)
    {
        if (count is <= 0) throw new ArgumentOutOfRangeException(nameof(count), count, "a rejection count must be positive; pass null to reject until cleared");
        Volatile.Write(ref _rejectsRemaining, count ?? Unlimited);
        SdlVulkanLog.Logger.GpuFaultArmed(count is { } n ? $"reject the next {n} submit(s)" : "reject every submit until cleared");
    }

    /// <summary>Report <c>VK_ERROR_DEVICE_LOST</c> from every submit from now on, until <see cref="Clear"/>.
    /// Wins over a pending rejection count, as a lost device outranks a refused submit.</summary>
    public void LoseDevice()
    {
        _deviceLost = true;
        SdlVulkanLog.Logger.GpuFaultArmed("device lost on every submit until cleared");
    }

    /// <summary>Disarm both faults. A context that already SAW a faked loss keeps saying so
    /// (<see cref="VulkanContext.DeviceLost"/> is one-way, as it must be for a real one).</summary>
    public void Clear()
    {
        var wasArmed = IsArmed;
        Volatile.Write(ref _rejectsRemaining, 0);
        _deviceLost = false;
        if (wasArmed) SdlVulkanLog.Logger.GpuFaultCleared(FakedResults);
    }

    /// <summary>Whether any fault is armed.</summary>
    public bool IsArmed => _deviceLost || Volatile.Read(ref _rejectsRemaining) > 0;

    /// <summary>Whether submits are reporting device loss.</summary>
    public bool DeviceLost => _deviceLost;

    /// <summary>Rejections still to come; null while rejecting until cleared, 0 when not rejecting.</summary>
    public int? RejectsRemaining => Volatile.Read(ref _rejectsRemaining) is var n && n == Unlimited ? null : n;

    /// <summary>How many submits were answered by a fake since this device was created.</summary>
    public long FakedResults => Interlocked.Read(ref _faked);

    /// <summary>
    /// The result to report instead of submitting, when a fault is armed. Consumes one rejection from a
    /// count; a device loss and an unlimited rejection consume nothing.
    /// </summary>
    internal bool TryFakeSubmit(out VkResult result)
    {
        if (_deviceLost)
        {
            Interlocked.Increment(ref _faked);
            result = VkResult.ErrorDeviceLost;
            return true;
        }

        while (true)
        {
            var remaining = Volatile.Read(ref _rejectsRemaining);
            if (remaining <= 0)
            {
                result = VkResult.Success;
                return false;
            }

            var next = remaining == Unlimited ? Unlimited : remaining - 1;
            if (Interlocked.CompareExchange(ref _rejectsRemaining, next, remaining) == remaining)
            {
                Interlocked.Increment(ref _faked);
                result = VkResult.ErrorInitializationFailed;
                return true;
            }
        }
    }

    /// <summary>One-line state for the submission ledger, null when nothing is armed.</summary>
    internal string? Describe() => _deviceLost
        ? "FAULT INJECTED: device lost"
        : RejectsRemaining switch
        {
            null => "FAULT INJECTED: rejecting every submit",
            > 0 and var n => $"FAULT INJECTED: rejecting {n} more submit(s)",
            _ => null,
        };
}
#endif
