using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

// Work recorded into a frame is provisional until that frame reaches the queue.
//
// Every upload that rides the frame's command buffer (the glyph atlases' flushes, deferred texture
// uploads, a cached-layer pass) used to advance its bookkeeping at RECORD time: the dirty rect was
// reset, the texture marked uploaded, the slot marked rendered. A frame the driver then refused carried
// all of it away, and nothing ever uploaded it again. Seen live, with the Adreno's rejected submits
// faked (GpuFaultInjection): a notification written during the storm lost every letter first drawn
// then ("Displa recovered from a stall on the Home vie"), for the life of the process.
//
// So a recorder states what undoing its work means, and the context runs that exactly when the frame
// does not reach the queue: a rejected or failed submit, a frame discarded by recovery, or one that was
// begun and never ended, which the next frame's start detects. An accepted submit drops the rollbacks.
public sealed unsafe partial class VulkanContext
{
    // The rollbacks of the frame being recorded. Frame state, so single-threaded like the rest of it:
    // the render thread, the sacrificial recovery task while the render thread only polls it, or the one
    // job driving an offscreen context. Reused across frames; null until something first registers.
    private List<Action>? _frameRollbacks;

    // The command buffer of the frame being recorded, Null outside one. What OnFrameDropped compares
    // against, so work recorded into a one-shot's command buffer registers nothing.
    private VkCommandBuffer _recordingFrameCmd;

    /// <summary>
    /// Registers what to undo if the frame recorded into <paramref name="cmd"/> never reaches the queue.
    /// </summary>
    /// <remarks>
    /// <para>For work whose bookkeeping advances when it is RECORDED (an upload marked done, a region
    /// marked clean): a frame the driver refuses, one that fails its submit, one that recovery discards,
    /// and one that is begun and never ended all carry the work away unexecuted, and the rollback is what
    /// puts it back in line for the next frame. Runs at most once, on the thread driving the frame, after
    /// the drop is known and before the next frame records anything; dropped unrun when the submit is
    /// accepted.</para>
    /// <para>A command buffer that is not the frame being recorded registers nothing and returns false:
    /// a one-shot's submit is synchronous, so its caller already sees the failure.</para>
    /// <para>A rollback must tolerate its owner having been disposed in between, and must not throw.</para>
    /// </remarks>
    public bool OnFrameDropped(VkCommandBuffer cmd, Action rollback)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        if (cmd == VkCommandBuffer.Null || cmd != _recordingFrameCmd) return false;
        (_frameRollbacks ??= new List<Action>()).Add(rollback);
        return true;
    }

    /// <summary>
    /// Called FIRST in each frame begin, before anything reads the index's capture state. A frame still
    /// marked as recording was begun and never ended (an exception between the two, with no abort), so
    /// its work never reached the queue: undo it, and cancel the captures it carried. Cancelling them has
    /// to precede the begin's readback step, which would otherwise hand their unwritten buffers back as
    /// finished captures, the fence it checks having been waited for a DIFFERENT, earlier frame.
    /// </summary>
    private void NoteUnendedFrameDropped()
    {
        if (_recordingFrameCmd != VkCommandBuffer.Null)
            NoteFrameDropped("the previous frame was begun and never ended");
    }

    /// <summary>A frame starts recording into <paramref name="cmd"/>: what it records from here on is
    /// provisional until it reaches the queue.</summary>
    private void BeginFrameRecording(VkCommandBuffer cmd) => _recordingFrameCmd = cmd;

    /// <summary>The frame's submit was accepted: its work is on the queue, so nothing needs undoing.</summary>
    private void NoteFrameSubmitted()
    {
        _recordingFrameCmd = VkCommandBuffer.Null;
        _frameRollbacks?.Clear();
    }

    /// <summary>The frame being recorded will not reach the queue. Call before the frame index advances.</summary>
    private void NoteFrameDropped(string why)
    {
        _recordingFrameCmd = VkCommandBuffer.Null;

        // Capture work that rode this frame's fence index, which can never now signal for it. Cancelled
        // here rather than only on a rejected submit, which is all that used to cancel them: a failed
        // submit and a discarded frame leave the same unwritten readback buffer behind.
        if (_thumbPending && _thumbPendingIndex == _currentFrame)
            _thumbPending = false;
        CancelPresentCaptureOnDroppedFrame();

        RunFrameRollbacks(why);
    }

    private void RunFrameRollbacks(string why)
    {
        if (_frameRollbacks is not { Count: > 0 } pending) return;

        // Copied out first, so a rollback that re-queues work (a texture asking to be recorded again)
        // cannot extend the list being walked.
        var rollbacks = pending.ToArray();
        pending.Clear();
        foreach (var rollback in rollbacks)
            rollback();
        SdlVulkanLog.Logger.FrameDroppedWorkRequeued(rollbacks.Length, why);
    }

    // Deferred texture uploads whose frame was dropped, re-recorded at the start of the next frame. Held
    // here rather than handed back to whoever created the texture, so no consumer needs to know a frame
    // can be dropped: its texture simply arrives one frame later.
    private List<VkTexture>? _requeuedTextureUploads;

    internal void RequeueTextureUpload(VkTexture texture)
        => (_requeuedTextureUploads ??= new List<VkTexture>()).Add(texture);

    /// <summary>
    /// Records <paramref name="texture"/>'s upload (<see cref="VkTexture.CreateDeferred"/>) into the next
    /// frame, at its start and before any render pass, instead of in a one-shot of its own. For a texture
    /// made where a frame is already recording its render pass (a draw path): the one-shot would block this
    /// thread until the GPU finished, and submit in the middle of a frame, which some drivers reject.
    /// Draw it once <see cref="VkTexture.IsUploaded"/> is true, and ask for that next frame. A dropped frame
    /// records it again on its own. Render thread only.
    /// </summary>
    public void QueueTextureUpload(VkTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        AssertFrameThread(nameof(QueueTextureUpload));
        if (!texture.IsUploaded) RequeueTextureUpload(texture);
    }

    /// <summary>Re-records every re-queued texture upload into the frame just begun, before any render
    /// pass (transfers cannot happen inside one).</summary>
    private void RecordRequeuedTextureUploads(VkCommandBuffer cmd)
    {
        if (_requeuedTextureUploads is not { Count: > 0 } pending) return;
        var textures = pending.ToArray();
        pending.Clear();
        foreach (var texture in textures)
            texture.RecordUpload(cmd); // registers a fresh rollback against this frame
    }
}
