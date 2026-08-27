using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

// Deferred destruction: a consumer hands a GPU object here instead of destroying it, and the context
// destroys it once every frame that could reference it has retired.
//
// Why a drain is not enough, and this exists. TryWaitAllFramesIdle / TryWaitPriorFramesIdle retire
// PREVIOUS frames, which is what they promise. They cannot retire the frame being recorded: whatever
// the current command buffer has already bound stays bound until that buffer is submitted and its fence
// signals, and no wait taken mid-record can reach that point. So a consumer that destroys a resource
// mid-frame is correct only if it can prove nothing earlier in the SAME frame referenced it -- a
// property of call order across hooks the consumer usually does not own. The TianWen FITS viewer got
// that wrong while reasoning correctly about fences at the destroy site: a pre-render-pass hook bound a
// document's channel views, the render callback replaced the document and destroyed them, and the
// frame went to the GPU with dangling views. The validation layer reads it as "vkCmdBindDescriptorSets():
// ... invalid state ... VkImageView was destroyed", the driver as nvlddmkm 153, and Windows as a
// LiveKernelEvent 141 watchdog with the process gone (2026-08-27, twice in one day). A drain at the
// destroy site also stalls the render thread on every call, which for that viewer meant a device idle
// per file stepped through.
//
// The retirement rule. _frameOrdinal is incremented by BeginFrame / BeginOffscreenFrame right after
// the fence wait for the slot about to be recorded, so the frame being recorded has ordinal F and is
// submitted under slot F % MaxFramesInFlight. That slot's fence is next waited by the BeginFrame that
// increments the ordinal to F + MaxFramesInFlight, and by then every slot has been waited at least once
// since F, so every frame with ordinal <= F has retired. An entry deferred at ordinal F is therefore
// safe once _frameOrdinal - MaxFramesInFlight >= F. Between frames there is no recording buffer, but
// the last submitted frame (ordinal _frameOrdinal) may still be in flight, so the same stamp is the
// conservative and correct one there too. A rejected submit (see SubmitFrame) leaves nothing in
// flight under its slot, so retiring its entries on schedule is safe as well.
//
// Recovery and teardown flush the whole queue after their own drain: they destroy or rebuild the sync
// objects the schedule is expressed in, so nothing deferred can be scheduled past them, and after a
// device loss destroying objects is permitted regardless.
public sealed unsafe partial class VulkanContext
{
    private readonly struct DeferredDestroy
    {
        public readonly long Ordinal;
        public readonly VkImageView View;
        public readonly VkImage Image;
        public readonly VkDeviceMemory Memory;
        public readonly VkBuffer Buffer;
        public readonly VkDescriptorSet DescriptorSet;
        public readonly Action? Custom;

        public DeferredDestroy(long ordinal, VkImageView view, VkImage image, VkDeviceMemory memory,
            VkBuffer buffer, VkDescriptorSet descriptorSet, Action? custom)
        {
            Ordinal = ordinal;
            View = view;
            Image = image;
            Memory = memory;
            Buffer = buffer;
            DescriptorSet = descriptorSet;
            Custom = custom;
        }
    }

    // Appended on the render thread and consumed there, in order: entries are stamped with a
    // non-decreasing ordinal, so the retired prefix is always at the front.
    private readonly List<DeferredDestroy> _deferredDestroys = new();

    /// <summary>How many deferred destroys are still waiting for their frames to retire.</summary>
    public int PendingDeferredDestroys => _deferredDestroys.Count;

    /// <summary>
    /// Destroys the given handles once every frame that could reference them has retired: the frame
    /// being recorded, if any, and every frame still in flight. Null handles are ignored, so a caller
    /// passes whatever it holds. Render thread only, like the frame state machine. This is the ONLY
    /// correct way to release a GPU object a frame may have bound; draining fences at the call site
    /// retires previous frames but never the one being recorded.
    /// </summary>
    /// <param name="descriptorSet">Freed back to the shared pool (<see cref="FreeDescriptorSet"/>);
    /// pass a set from another pool through the <see cref="DeferDestroy(Action)"/> overload instead.</param>
    public void DeferDestroy(
        VkImageView view = default, VkImage image = default, VkDeviceMemory memory = default,
        VkBuffer buffer = default, VkDescriptorSet descriptorSet = default)
    {
        if (view == VkImageView.Null && image == VkImage.Null && memory == VkDeviceMemory.Null
            && buffer == VkBuffer.Null && descriptorSet == VkDescriptorSet.Null)
        {
            return;
        }
        AssertFrameThread(nameof(DeferDestroy));
        _deferredDestroys.Add(new DeferredDestroy(_frameOrdinal, view, image, memory, buffer, descriptorSet, null));
    }

    /// <summary>
    /// Runs <paramref name="destroy"/> once every frame that could reference what it frees has retired.
    /// For objects the typed overload does not cover, or for a consumer that keeps its own counters.
    /// </summary>
    public void DeferDestroy(Action destroy)
    {
        ArgumentNullException.ThrowIfNull(destroy);
        AssertFrameThread(nameof(DeferDestroy));
        _deferredDestroys.Add(new DeferredDestroy(_frameOrdinal, default, default, default, default, default, destroy));
    }

    /// <summary>
    /// Destroys every entry whose frames have retired. Called right after the ordinal advances in
    /// <see cref="BeginFrame"/> and <see cref="BeginOffscreenFrame"/>, i.e. immediately after the fence
    /// wait that proves the retirement.
    /// </summary>
    private void FlushRetiredDeferredDestroys()
    {
        if (_deferredDestroys.Count == 0)
        {
            return;
        }
        var retired = _frameOrdinal - MaxFramesInFlight;
        var n = 0;
        while (n < _deferredDestroys.Count && _deferredDestroys[n].Ordinal <= retired)
        {
            n++;
        }
        if (n == 0)
        {
            return;
        }
        for (var i = 0; i < n; i++)
        {
            Execute(_deferredDestroys[i]);
        }
        _deferredDestroys.RemoveRange(0, n);
    }

    /// <summary>
    /// Destroys every pending entry regardless of schedule. Only for the paths that have just drained
    /// the device and are about to destroy or rebuild the sync objects the schedule is measured in
    /// (<see cref="RecoverFromGpuError"/>, <see cref="Dispose"/>).
    /// </summary>
    private void FlushAllDeferredDestroys()
    {
        for (var i = 0; i < _deferredDestroys.Count; i++)
        {
            Execute(_deferredDestroys[i]);
        }
        _deferredDestroys.Clear();
    }

    private void Execute(in DeferredDestroy entry)
    {
        if (entry.Custom is { } custom)
        {
            custom();
            return;
        }
        var api = DeviceApi;
        if (entry.DescriptorSet != VkDescriptorSet.Null)
        {
            _dev.FreeDescriptorSet(entry.DescriptorSet);
        }
        if (entry.View != VkImageView.Null)
        {
            api.vkDestroyImageView(entry.View);
        }
        if (entry.Image != VkImage.Null)
        {
            api.vkDestroyImage(entry.Image);
        }
        if (entry.Buffer != VkBuffer.Null)
        {
            api.vkDestroyBuffer(entry.Buffer);
        }
        if (entry.Memory != VkDeviceMemory.Null)
        {
            api.vkFreeMemory(entry.Memory);
        }
    }
}
