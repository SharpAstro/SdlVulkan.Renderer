using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Per-window swapchain + frame loop. Owns the surface, swapchain, framebuffers, per-frame sync,
/// the per-frame vertex ring, and the per-frame command buffers. The device-level state (logical
/// device, queue, command pool, render pass, descriptor pool/layout, pipeline layout) lives in a
/// shared <see cref="VulkanDevice"/> that this context references and forwards — so several windows
/// can present from one device. A context disposes the device only when it created it (the
/// single-window and offscreen paths); shared-device windows leave teardown to the device owner.
/// </summary>
public sealed unsafe partial class VulkanContext : IDisposable
{
    private readonly VulkanDevice _dev;
    private readonly bool _ownsDevice;
    private readonly uint _vertexBufferSize;
    /// <summary>
    /// Number of frames in flight on the GPU. Exposed so side-car resources
    /// (per-frame ring buffers, staging uploads) can size themselves to match
    /// the renderer's own per-frame sync discipline. Pair with
    /// <see cref="CurrentFrame"/> to index ring-buffered resources.
    /// </summary>
    public const int MaxFramesInFlight = 2;
    // Cap the per-frame in-flight fence wait (ns) so a never-signaled fence can't hard-freeze the loop.
    // 500ms is still tens of times a normal frame; only a stuck or starved GPU reaches it. The FIRST
    // timeout flips the context into "stuck" mode, where subsequent BeginFrame attempts poll the SAME
    // fence with a short wait instead - each retry is then nearly free, so the event loop keeps pumping
    // input between attempts (see SdlEventLoop's Timeout handling) instead of going blind for the full
    // cap. A successful wait (or RecoverFromGpuError rebuilding the sync objects) clears stuck mode.
    // A timeout is NOT destructive by itself: the loop only escalates into the sync+swapchain rebuild
    // after the fence has been stuck for a sustained period - a fence that is merely late (heavy frame,
    // TDR in progress, or a DirectML/ONNX compute job hogging the same hardware) signals on a later
    // poll and rendering resumes with zero teardown.
    private const ulong FenceWaitTimeoutNs = 500_000_000UL;
    private const ulong FenceStuckPollTimeoutNs = 10_000_000UL;
    // Cap (ns) for the device drain on the UI-thread recovery/resize paths (see TryDrainDevice).
    // 1s is well past any legitimate frame; reaching it means the GPU is genuinely wedged, in
    // which case we force the teardown rather than block the UI thread on an unbounded wait.
    private const ulong DrainTimeoutNs = 1_000_000_000UL;

    // Per-window fence-stuck state. Wrapped in a property so the one setter is the single writer
    // that also mirrors the state onto the shared device (_dev.IsGpuStuck) — that way device-level
    // teardown and cross-component render-thread drains can consult one known-good signal, and no
    // call site can set the fence state without the device flag following it.
    private bool _fenceWaitStuckBacking;
    private bool _fenceWaitStuck
    {
        get => _fenceWaitStuckBacking;
        set
        {
            _fenceWaitStuckBacking = value;
            _dev.IsGpuStuck = value;
        }
    }

    /// <summary>
    /// True when this window's GPU work is known wedged (its per-frame fence is timing out). Lets
    /// render-thread consumers (e.g. buffer-swap drains) skip an unbounded device wait that would
    /// hang while the GPU is stuck.
    /// </summary>
    public bool IsGpuStuck => _fenceWaitStuck;

    /// <summary>The shared device backing this window. GPU resources reused across windows (font
    /// atlases, textures, pipelines) are created against this rather than the context.</summary>
    public VulkanDevice GraphicsDevice => _dev;

    // Device-level members, forwarded to the shared VulkanDevice. Kept on the context so existing
    // consumers (VkRenderer, VkFontAtlas, VkTexture, side-car pipelines) that read ctx.Device /
    // ctx.RenderPass / ctx.PipelineLayout etc. continue to work whether or not the device is shared.
    public VkInstance Instance => _dev.Instance;
    public VkInstanceApi InstanceApi => _dev.InstanceApi;
    public VkPhysicalDevice PhysicalDevice => _dev.PhysicalDevice;
    public VkDevice Device => _dev.Device;
    public VkDeviceApi DeviceApi => _dev.DeviceApi;
    public VkQueue GraphicsQueue => _dev.GraphicsQueue;
    public uint GraphicsQueueFamily => _dev.GraphicsQueueFamily;
    public VkCommandPool CommandPool => _dev.CommandPool;
    public VkRenderPass RenderPass => _dev.RenderPass;
    public VkDescriptorPool DescriptorPool => _dev.DescriptorPool;

    /// <summary>The one linear/clamp sampler every <see cref="VkTexture"/> binds — see
    /// <see cref="VulkanDevice.LinearClampSampler"/>.</summary>
    public VkSampler LinearClampSampler => _dev.LinearClampSampler;
    public VkDescriptorSetLayout DescriptorSetLayout => _dev.DescriptorSetLayout;
    public VkDescriptorSet DescriptorSet => _dev.DescriptorSet;
    public VkPipelineLayout PipelineLayout => _dev.PipelineLayout;

    /// <summary>MSAA sample count (Count1 = no MSAA). Inherited from the shared device.</summary>
    public VkSampleCountFlags MsaaSamples => _dev.MsaaSamples;

    /// <summary>Device <c>maxImageDimension2D</c> limit. Forwarded from the shared device.</summary>
    public uint MaxImageDimension2D => _dev.MaxImageDimension2D;

    // Swapchain state
    public VkSwapchainKHR Swapchain { get; private set; }
    public VkFormat SwapchainFormat { get; private set; }
    public uint SwapchainWidth { get; private set; }
    public uint SwapchainHeight { get; private set; }

    private VkImage[] _swapchainImages = [];
    private VkImageView[] _swapchainImageViews = [];
    private VkFramebuffer[] _framebuffers = [];

    // MSAA resolve target (only when MsaaSamples > Count1)
    private VkImage _msaaImage;
    private VkDeviceMemory _msaaMemory;
    private VkImageView _msaaImageView;

    // Per-frame sync
    private readonly VkSemaphore[] _imageAvailableSemaphores = new VkSemaphore[MaxFramesInFlight];
    private readonly VkSemaphore[] _renderFinishedSemaphores = new VkSemaphore[MaxFramesInFlight];
    private readonly VkFence[] _inFlightFences = new VkFence[MaxFramesInFlight];
    private readonly VkCommandBuffer[] _commandBuffers = new VkCommandBuffer[MaxFramesInFlight];
    private int _currentFrame;
    private uint _currentImageIndex;

    // Frame state machine. Render thread ONLY (BeginFrame / BeginRenderPass / SubmitFrame /
    // AbortFrame all run on the event-loop thread), so these are deliberately plain fields —
    // volatile would imply a sharing that does not exist.
    private bool _frameBegun;       // BeginFrame acquired an image; a submit must resolve the frame
    private bool _renderPassBegun;  // vkCmdBeginRenderPass has been recorded into this frame

    // ---- Submission ledger (wedge forensics) ----------------------------------------------------
    // A wedge presents as a fence that never signals, and until now the log could not distinguish
    // the two causes: the GPU is genuinely slow/hung under work we submitted, versus nothing was
    // ever submitted under that fence (the orphan case — see the note in BeginFrame). Recording the
    // frame ordinal of each index's last submit answers it in one line, with no repro needed.
    //
    // Written on the render thread; read from breadcrumbs and (in DEBUG) the inspector's MCP thread,
    // hence Volatile/Interlocked — matching VulkanDevice's churn counters. Single writer, so no CAS.
    private long _frameOrdinal;                                            // ++ per frame that clears its fence wait
    private readonly long[] _submitOrdinal = new long[MaxFramesInFlight];   // 0 = never submitted under this index
    // 1 = a submit succeeded under this index and its fence will signal. 0 = nothing in flight, so the
    // fence CANNOT signal and BeginFrame must not wait on it. int rather than bool for Volatile access.
    private readonly int[] _submitPending = new int[MaxFramesInFlight];
    private long _submitsTotal, _submitsRejected;
    private volatile bool _deviceLost;

    /// <summary>True once any queue/fence/present call has reported VK_ERROR_DEVICE_LOST.</summary>
    public bool DeviceLost => _deviceLost;

    // Frame-state ownership, enforced rather than merely documented. The frame state machine is
    // single-threaded, and the ONLY legal transfer of this context to another thread is the sacrificial
    // recovery task (SdlEventLoop hands the renderer over while the render thread polls
    // GpuRecoveryTask.IsCompletedSuccessfully, whose completion is the publish barrier — that is why
    // none of this state needs volatile). If a future change ever calls a frame method from a second
    // thread, that reasoning silently breaks and the symptom would be another unexplained wedge; this
    // turns it into an immediate, named failure in DEBUG at zero release cost.
    private int _frameThreadId;

    [Conditional("DEBUG")]
    private void AssertFrameThread(string method)
    {
        var id = Environment.CurrentManagedThreadId;
        if (_frameThreadId == 0)
        {
            _frameThreadId = id;
            return;
        }
        if (_frameThreadId != id)
        {
            throw new InvalidOperationException(
                $"{method} called from thread {id}, but this context's frame state machine is owned by " +
                $"thread {_frameThreadId}. Frame methods are single-threaded; only the sacrificial " +
                "recovery task may touch this context, and only while the render thread polls it.");
        }
    }

    /// <summary>
    /// Re-seat frame-state ownership after the sacrificial recovery task legitimately ran frame-adjacent
    /// teardown on its own thread. Called by <see cref="RecoverFromGpuError"/> so the assertion tracks the
    /// handoff instead of tripping on it.
    /// </summary>
    [Conditional("DEBUG")]
    private void ReseatFrameThread() => _frameThreadId = 0;

    /// <summary>
    /// One-line submission ledger for the wedge breadcrumb. A gap larger than
    /// <see cref="MaxFramesInFlight"/> — or "NEVER" — means the fence being waited on has no
    /// submission behind it, so the GPU is idle and the stall is ours, not the driver's.
    /// </summary>
    public string SubmissionLedger
    {
        get
        {
            var idx = _currentFrame;
            var ord = Volatile.Read(ref _submitOrdinal[idx]);
            var now = Volatile.Read(ref _frameOrdinal);
            var last = ord == 0 ? "NEVER" : $"frame {ord} ({now - ord} frame(s) ago)";
            var rejected = Interlocked.Read(ref _submitsRejected);
            return $"ledger: waiting fence[{idx}], last submit under it {last}" +
                   (Volatile.Read(ref _submitPending[idx]) == 0 ? " (NOTHING PENDING)" : "") +
                   $"; frames {now}, submits {Interlocked.Read(ref _submitsTotal)}" +
                   (rejected > 0 ? $", REJECTED {rejected}" : "") +
                   (_deviceLost ? "; DEVICE_LOST seen" : "");
        }
    }

    /// <summary>
    /// Replace one frame index's image-available semaphore after a submit the driver rejected. The acquire
    /// signaled it and the rejected submit never queued the wait that would consume that signal, so it is
    /// signaled-but-not-pending: legal to destroy, and unsafe to reuse, because the next acquire on this
    /// index would otherwise signal an already-signaled binary semaphore.
    /// </summary>
    private void ReplaceImageAvailableSemaphore(int frameIndex)
    {
        DeviceApi.vkDestroySemaphore(_imageAvailableSemaphores[frameIndex]);
        VkSemaphoreCreateInfo ci = new();
        DeviceApi.vkCreateSemaphore(&ci, null, out var replacement).CheckResult();
        _imageAvailableSemaphores[frameIndex] = replacement;
        SdlVulkanLog.Logger.SubmitRejectedFrameDropped(frameIndex);
    }

    /// <summary>
    /// Log a real device loss distinctly, once. Nothing in the renderer used to single DEVICE_LOST out
    /// — every non-success came back as a generic VkException — so no field log could tell a GPU reset
    /// (TDR) apart from a fence that simply never got a submission. That distinction is the first thing
    /// a wedge report needs.
    /// </summary>
    private void NoteDeviceLost(VkResult result, string where)
    {
        if (result != VkResult.ErrorDeviceLost || _deviceLost) return;
        _deviceLost = true;
        SdlVulkanLog.Logger.DeviceLostReported(where);
    }

    /// <summary>
    /// Index of the current frame-in-flight (0 or 1 for double-buffered).
    /// Consumers that need per-frame GPU resources (UBO copies, staging buffers)
    /// use this to select the correct slot, guaranteed safe to write after
    /// <see cref="BeginFrame"/> returns (the fence for this slot was waited on).
    /// </summary>
    public int CurrentFrame => _currentFrame;

#if DEBUG
    /// <summary>
    /// DEBUG-only: index into <c>_swapchainImages</c> of the most recently presented frame.
    /// Stays valid after <see cref="EndFrame"/> (only <see cref="CurrentFrame"/> advances there),
    /// so the inspector's render-thread readback can copy the just-presented image.
    /// </summary>
    internal uint CurrentSwapchainImageIndex => _currentImageIndex;
#endif

    // Per-frame vertex staging buffers (avoids race between in-flight frames)
    private readonly VkBuffer[] _vertexBuffers = new VkBuffer[MaxFramesInFlight];
    private readonly VkDeviceMemory[] _vertexMemories = new VkDeviceMemory[MaxFramesInFlight];
    private readonly float*[] _vertexMapped = new float*[MaxFramesInFlight];
    private int _vertexOffset; // in floats

    // Not readonly: Android destroys the native surface on background and hands back a fresh one on
    // foreground, so the swapchain is rebuilt against a new surface via AdoptSurface.
    private VkSurfaceKHR _surface;
    private bool _disposed;

    private VulkanContext(VulkanDevice device, VkSurfaceKHR surface, uint vertexBufferSize, bool ownsDevice)
    {
        _dev = device;
        _surface = surface;
        _vertexBufferSize = vertexBufferSize;
        _ownsDevice = ownsDevice;
    }

    public static VulkanContext Create(VkInstance instance, VkSurfaceKHR surface, uint width, uint height,
        uint vertexBufferSize = 4 * 1024 * 1024, VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1)
    {
        // Single-window path: this context creates and owns its device.
        var device = VulkanDevice.Create(instance, surface, msaaSamples);
        var ctx = new VulkanContext(device, surface, vertexBufferSize, ownsDevice: true);

        ctx.CreateSyncObjects();
        ctx.AllocateCommandBuffers();
        ctx.CreateVertexBuffers();
        ctx.CreateSwapchain(width, height);

        return ctx;
    }

    /// <summary>
    /// Creates a window context that SHARES an existing <see cref="VulkanDevice"/> rather than
    /// creating its own. This is the multi-window path: <see cref="SdlVulkanApp"/> builds the device
    /// once (from the first window's surface) and hands it to every window's context here. The
    /// context owns only its swapchain / sync / vertex ring / command buffers — not the device, which
    /// the app disposes after the last window is gone. GPU resources built against the shared device
    /// (page geometry buffers, image textures) stay valid across all windows that share it, so a
    /// document session can move between windows without re-uploading them.
    /// </summary>
    public static VulkanContext CreateForSharedDevice(VulkanDevice device, VkSurfaceKHR surface,
        uint width, uint height, uint vertexBufferSize = 4 * 1024 * 1024)
    {
        var ctx = new VulkanContext(device, surface, vertexBufferSize, ownsDevice: false);

        ctx.CreateSyncObjects();
        ctx.AllocateCommandBuffers();
        ctx.CreateVertexBuffers();
        ctx.CreateSwapchain(width, height);

        return ctx;
    }

    // --- Device-level operations, forwarded to the shared VulkanDevice ---

    /// <summary>Allocates a new descriptor set from the shared pool with the shared layout.</summary>
    public VkDescriptorSet AllocateDescriptorSet() => _dev.AllocateDescriptorSet();

    /// <summary>Frees a descriptor set back to the shared pool.</summary>
    public void FreeDescriptorSet(VkDescriptorSet set) => _dev.FreeDescriptorSet(set);

    /// <summary>Updates a descriptor set to point to the given image view and sampler.</summary>
    public void UpdateDescriptorSet(VkDescriptorSet targetSet, VkImageView imageView, VkSampler sampler)
        => _dev.UpdateDescriptorSet(targetSet, imageView, sampler);

    public uint FindMemoryType(uint typeFilter, VkMemoryPropertyFlags properties)
        => _dev.FindMemoryType(typeFilter, properties);

    public void ExecuteOneShot(Action<VkCommandBuffer> action) => _dev.ExecuteOneShot(action);

    /// <summary>Creates a persistent vertex buffer with the given data (lives until destroyed).</summary>
    public (VkBuffer Buffer, VkDeviceMemory Memory) CreatePersistentVertexBuffer(ReadOnlySpan<float> data)
        => _dev.CreatePersistentVertexBuffer(data);

    public void DestroyBuffer(VkBuffer buffer, VkDeviceMemory memory) => _dev.DestroyBuffer(buffer, memory);

    public void RecreateSwapchain(uint width, uint height)
    {
        // Bounded drain (see TryDrainDevice): a resize that races a wedged GPU must not hang the
        // UI thread on an unbounded vkDeviceWaitIdle.
        TryDrainDevice(DrainTimeoutNs, "swapchain recreate");
        CleanupSwapchain();
        CreateSwapchain(width, height);
    }

    /// <summary>
    /// Android surface loss (app backgrounded): drain in-flight frames (bounded, like
    /// <see cref="RecreateSwapchain"/> — never an unbounded vkDeviceWaitIdle on the UI thread) and
    /// destroy the swapchain so the old surface is no longer referenced by it. Pair with
    /// <see cref="AdoptSurface"/> once the window has created a fresh surface. Split from AdoptSurface
    /// so the caller can recreate the window's surface in between: the old surface must outlive its
    /// swapchain (destroyed here) yet be gone before the new swapchain binds.
    /// </summary>
    public void PrepareForSurfaceLoss()
    {
        TryDrainDevice(DrainTimeoutNs, "surface loss");
        CleanupSwapchain();
    }

    /// <summary>
    /// Adopt a freshly-created presentation surface (created and owned by the window) and rebuild the
    /// swapchain against it. The previous surface was destroyed by the window, not here — so this does
    /// not touch the old handle.
    /// </summary>
    public void AdoptSurface(VkSurfaceKHR newSurface, uint width, uint height)
    {
        _surface = newSurface;
        CreateSwapchain(width, height);
    }

    /// <summary>
    /// Recover from a non-fatal Vulkan error (e.g. a queue submit that failed mid-frame).
    /// Waits for the device to idle, then rebuilds per-frame sync objects (fences and
    /// semaphores) and the swapchain. After this returns, the next BeginFrame can proceed
    /// without hitting a stuck unsignaled fence from the failed submit.
    /// <para>
    /// Throws if the device itself is lost — callers should treat that as terminal.
    /// </para>
    /// </summary>
    public void RecoverFromGpuError(uint width, uint height)
    {
        // Drain in-flight work before recreating the sync objects below. If the previous frame's
        // submit failed before signaling its fence, the next BeginFrame would otherwise block
        // forever on vkWaitForFences — recreating the sync objects sidesteps that.
        //
        // This used to be an unbounded vkDeviceWaitIdle, but on a genuinely wedged GPU (the fence
        // never signals — a TDR, or a frame that overran the event loop's fence-escalation window)
        // that blocks the UI thread forever and the window goes "Not responding" (the user is then
        // forced to kill it). TryDrainDevice caps the wait and forces the teardown on timeout: a
        // clean rebuild-after-timeout is recoverable (or at worst surfaces as a recovery failure the
        // event loop turns into a clean exit), whereas the unbounded wait could not escape the hang.
        //
        // attemptEvenIfStuck: on the stuck-fence escalation this method runs on the sacrificial
        // recovery task, where even a pathological block is abandonable — so unlike the UI-thread
        // resize path there is no reason to skip the bounded drain. And the drain matters here:
        // a stuck fence is usually a frame that is merely SLOW (Windows has never logged a TDR for
        // these — the GPU is alive), and destroying its fences and semaphores while the submission
        // still references them is illegal. A 2026-08-04 capture showed exactly that teardown being
        // followed by the driver rejecting every subsequent vkQueueSubmit; giving in-flight work up
        // to the drain cap to retire first makes the teardown legal whenever the frame completes,
        // and on a truly dead fence it degrades to today's behaviour after the cap.
        TryDrainDevice(DrainTimeoutNs, "GPU-error recovery", attemptEvenIfStuck: true);

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkDestroyFence(_inFlightFences[i]);
            DeviceApi.vkDestroySemaphore(_imageAvailableSemaphores[i]);
            DeviceApi.vkDestroySemaphore(_renderFinishedSemaphores[i]);
        }
        CreateSyncObjects();
        _currentFrame = 0;
        _fenceWaitStuck = false; // fresh fences start signaled — leave stuck-mode polling

        // The in-flight frame (if any) died with its semaphores and command buffer: it can never be
        // submitted now, and the fences it would have signaled no longer exist. Clear the frame state
        // so AbortFrame cannot try to resolve a frame against replaced sync objects, and clear the
        // ledger so it reports against the new fences rather than the destroyed ones.
        _frameBegun = false;
        _renderPassBegun = false;
        Array.Clear(_submitOrdinal);
        Array.Clear(_submitPending);   // fresh fences are signaled but have nothing behind them
        // This ran on the sacrificial task's thread; let the render thread re-seat ownership.
        ReseatFrameThread();

        CleanupSwapchain();
        CreateSwapchain(width, height);
    }

    /// <summary>
    /// Bounded replacement for <c>vkDeviceWaitIdle</c> on the UI-thread recovery/resize paths.
    /// An unbounded device-wait-idle on a wedged GPU (fence never signals) blocks the calling
    /// thread forever, freezing the window. This waits on all in-flight fences with a hard cap
    /// and returns <c>false</c> on timeout so the caller can force its teardown anyway — the
    /// caller is about to destroy + recreate the sync objects and swapchain regardless, so a
    /// timed-out drain degrades to a recoverable rebuild (or a clean exit) instead of a hang.
    /// </summary>
    private bool TryDrainDevice(ulong timeoutNs, string context, bool attemptEvenIfStuck = false)
    {
        // Guard on the GPU's known-good state. When the per-frame fence is already known stuck,
        // BeginFrame has been timing out — which is exactly how the event loop escalated into
        // recovery — and on the UI-thread callers (resize, surface loss) burning the full timeout
        // before failing is a stall the window can't afford, so those skip straight to the teardown
        // (the fences are recreated next regardless). The sacrificial recovery path passes
        // attemptEvenIfStuck instead: it can afford the bounded wait, and a "stuck" fence is usually
        // a slow frame whose in-flight work makes the teardown illegal until it retires — see the
        // caller. The other recovery trigger is a mid-frame submit/present error with a healthy
        // fence, where the drain always runs.
        if (_fenceWaitStuck && !attemptEvenIfStuck)
        {
            SdlVulkanLog.Logger.DrainSkippedGpuStuck(context);
            return false;
        }

        // Only wait on fences that have a submission behind them. A fence whose submit was rejected
        // (see SubmitFrame) is unsignaled with nothing in flight — waiting on it can only ever burn
        // the full cap. No pending fences at all means the device is drained by definition.
        var fences = stackalloc VkFence[MaxFramesInFlight];
        var pending = 0u;
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (Volatile.Read(ref _submitPending[i]) != 0)
                fences[pending++] = _inFlightFences[i];
        }
        if (pending == 0)
        {
            return true;
        }
        if (DeviceApi.vkWaitForFences(pending, fences, true, timeoutNs) == VkResult.Timeout)
        {
            SdlVulkanLog.Logger.DrainTimedOut(timeoutNs / 1_000_000, context);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Bounded "wait for prior in-flight frames" for the font-atlas grow/evict drains, which must
    /// ensure no previously-submitted frame is still sampling the atlas image before it is swapped or
    /// destroyed (the Adreno use-after-free hazard those drains were added for). Waits on every
    /// in-flight fence EXCEPT the current frame's: the atlas grow runs mid-record (between BeginFrame
    /// and EndFrame), so the current frame's fence is unsignaled and not yet submitted — waiting on it
    /// would always time out. Capped at <see cref="DrainTimeoutNs"/> and skipped when the GPU is
    /// already known stuck, so a rare atlas grow coinciding with a wedged GPU can't hard-freeze the
    /// render thread the way the old unbounded <c>vkDeviceWaitIdle</c> here could. On a healthy GPU it
    /// is equivalent (the prior frame's fence signals promptly), so the Adreno protection is preserved.
    /// </summary>
    internal bool TryWaitPriorFramesIdle(string context)
    {
        if (_fenceWaitStuck)
        {
            SdlVulkanLog.Logger.AtlasDrainSkippedGpuStuck(context);
            return false;
        }
        var fences = stackalloc VkFence[MaxFramesInFlight];
        var n = 0;
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            // Skip fences with no submission behind them (never submitted, or the submit was
            // rejected — see SubmitFrame): they cannot signal, and nothing is sampling the atlas
            // from a frame that never reached the GPU.
            if (i != _currentFrame && Volatile.Read(ref _submitPending[i]) != 0)
            {
                fences[n++] = _inFlightFences[i];
            }
        }
        if (n == 0)
        {
            return true; // nothing in flight -> no prior frame can reference the atlas
        }
        if (DeviceApi.vkWaitForFences((uint)n, fences, true, DrainTimeoutNs) == VkResult.Timeout)
        {
            SdlVulkanLog.Logger.AtlasDrainTimedOut(context, DrainTimeoutNs / 1_000_000);
            return false;
        }
        return true;
    }

    public VkCommandBuffer BeginFrame(out bool resized)
    {
        AssertFrameThread(nameof(BeginFrame));
        resized = false;
        var fence = _inFlightFences[_currentFrame];
        // Bounded wait. We rely on the submit signaling this fence — including on drivers (Adreno
        // X1-85) where vkQueueSubmit returns a bogus error yet still signals normally. If a fence is
        // genuinely never signaled, an unbounded wait here would hard-freeze the loop with no escape,
        // so cap it and throw on timeout. The throw happens BEFORE any frame state is mutated (no
        // fence reset, no command-buffer reset, no acquire), so re-entering BeginFrame just waits on
        // the same fence again — that is what makes the event loop's non-destructive retry safe. While
        // stuck, retries use the short poll timeout so the loop stays responsive between attempts (see
        // FenceWaitTimeoutNs comment). A real device-loss comes back as a negative result (DEVICE_LOST),
        // which CheckResult turns into the destructive recovery path.
        // Skip the wait entirely when nothing was ever submitted under this index (a submit the driver
        // rejected). Such a fence is unsignaled with no pending work, so no wait and no escalation can
        // ever satisfy it -- waiting on it was the wedge.
        var waitResult = Volatile.Read(ref _submitPending[_currentFrame]) == 0
            ? VkResult.Success
            : DeviceApi.vkWaitForFences(1, &fence, true,
                _fenceWaitStuck ? FenceStuckPollTimeoutNs : FenceWaitTimeoutNs);
        if (waitResult == VkResult.Timeout)
        {
            _fenceWaitStuck = true;
            throw new VkException(waitResult, "in-flight fence wait timed out — GPU late or stuck");
        }
        NoteDeviceLost(waitResult, "vkWaitForFences");
        waitResult.CheckResult();
        _fenceWaitStuck = false;
        _frameOrdinal++;

        // The fence for _currentFrame is now signaled (just waited, not yet reset). If a thumbnail
        // capture's copy rode this fence index, its GPU work is complete — snapshot it now without
        // any extra GPU wait. Done before the reset below so the fence is still in its signaled state.
        ConsumeThumbnailReadback();

        var result = DeviceApi.vkAcquireNextImageKHR(Swapchain, ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame], VkFence.Null, out _currentImageIndex);

        NoteDeviceLost(result, "vkAcquireNextImageKHR");

        if (result == VkResult.ErrorOutOfDateKHR)
        {
            resized = true;
            return VkCommandBuffer.Null;
        }

        // NOTE: the fence is deliberately NOT reset here — see EndFrame, which resets it immediately
        // before the submit that signals it.
        //
        // It used to be reset at this point, which opened a window in which the frame's fence was
        // unsignaled and NOTHING but EndFrame's vkQueueSubmit could ever signal it again. Everything
        // between here and that submit runs inside that window: the three atlas Flushes (including a
        // page Grow), OnPreFlush, OnPreRenderPass texture uploads, BeginRenderPass, and the whole of
        // the consumer's OnRender. Any exit on that stretch that skipped EndFrame — a throw, or a
        // Timeout raised mid-frame rather than by the wait above — orphaned the fence permanently:
        // _currentFrame was never advanced either, so the NEXT BeginFrame waited on a fence no
        // submission would ever signal. That presents exactly like a hung GPU (fence never signals)
        // while the GPU is in fact idle, and it defeats the event loop's non-destructive retry, whose
        // whole premise is that a Timeout leaves no mutated frame state. Resetting at submit time
        // instead means an abandoned frame leaves the fence SIGNALED and the next frame proceeds.
        var cmd = _commandBuffers[_currentFrame];
        DeviceApi.vkResetCommandBuffer(cmd, 0);

        VkCommandBufferBeginInfo beginInfo = new()
        {
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };
        DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo);
        // From here until EndFrame there is an acquired swapchain image and a recording command
        // buffer that must be resolved by a submit. AbortFrame does that when the frame is abandoned.
        _frameBegun = true;
        _renderPassBegun = false;

        // Reset vertex offset for this frame
        _vertexOffset = 0;

        return cmd;
    }

    public void BeginRenderPass(VkCommandBuffer cmd, float clearR, float clearG, float clearB, float clearA)
    {
        VkClearValue clear = new();
        clear.color = new VkClearColorValue(clearR, clearG, clearB, clearA);

        VkRenderPassBeginInfo rpBI = new()
        {
            renderPass = RenderPass,
            framebuffer = _framebuffers[_currentImageIndex],
            renderArea = new VkRect2D(0, 0, SwapchainWidth, SwapchainHeight),
            clearValueCount = 1,
            pClearValues = &clear
        };

        DeviceApi.vkCmdBeginRenderPass(cmd, &rpBI, VkSubpassContents.Inline);
        _renderPassBegun = true;

        // Set dynamic viewport and scissor
        VkViewport viewport = new(0, 0, SwapchainWidth, SwapchainHeight, 0, 1);
        DeviceApi.vkCmdSetViewport(cmd, 0, viewport);
        VkRect2D scissor = new(0, 0, SwapchainWidth, SwapchainHeight);
        DeviceApi.vkCmdSetScissor(cmd, 0, scissor);
    }

    public void EndFrame(VkCommandBuffer cmd) => SubmitFrame(cmd, endRenderPass: true);

    private void SubmitFrame(VkCommandBuffer cmd, bool endRenderPass)
    {
        AssertFrameThread(nameof(SubmitFrame));
        if (endRenderPass)
        {
            DeviceApi.vkCmdEndRenderPass(cmd);
            _renderPassBegun = false;
        }
        DeviceApi.vkEndCommandBuffer(cmd);

        var waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        var signalSemaphore = _renderFinishedSemaphores[_currentFrame];
        VkPipelineStageFlags waitStage = VkPipelineStageFlags.ColorAttachmentOutput;

        VkSubmitInfo submitInfo = new()
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &waitSemaphore,
            pWaitDstStageMask = &waitStage,
            commandBufferCount = 1,
            pCommandBuffers = &cmd,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &signalSemaphore
        };

        // Reset the fence HERE, not in BeginFrame — the whole point is that only the few statements
        // between this reset and the submit below can leave it unsignaled (see the note in BeginFrame).
        // Legal: BeginFrame waited this fence to the signaled state and nothing has been submitted
        // under it since, so it is not in use by any pending submission.
        var frameFence = _inFlightFences[_currentFrame];
        DeviceApi.vkResetFences(1, &frameFence);

        var swapchain = Swapchain;
        var imageIndex = _currentImageIndex;
        VkPresentInfoKHR presentInfo = new()
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &signalSemaphore,
            swapchainCount = 1,
            pSwapchains = &swapchain,
            pImageIndices = &imageIndex
        };

        // Submit then present, both on this device's owning thread — the device relies on single-owner
        // submission rather than a lock (see VulkanDevice.AssertQueueThread), so nothing can interleave
        // between the two halves of this frame's queue work.
        VkResult presentResult;
        _dev.AssertQueueThread(nameof(SubmitFrame));
        var submitResult = DeviceApi.vkQueueSubmit(GraphicsQueue, 1, &submitInfo, frameFence);
        RenderDiag.Vk("submit", submitResult, $"frame={_currentFrame} img={_currentImageIndex}");
        NoteDeviceLost(submitResult, "vkQueueSubmit");

        // Qualcomm Adreno X1-85 (qcdx8380, Windows-on-ARM) returns VK_ERROR_INITIALIZATION_FAILED from
        // vkQueueSubmit, which is not a spec-legal return here (only SUCCESS, OUT_OF_{HOST,DEVICE}_MEMORY
        // and DEVICE_LOST are). It used to be tolerated on the belief that the work executed anyway and the
        // fence signaled normally, which made it free to ignore.
        //
        // MEASURED 2026-08-04: that belief is wrong — the work does NOT execute. A field capture caught the
        // failure mode exactly. After each escalation-recovery, precisely TWO frames were submitted, both
        // rejected with this code, and the third frame then waited forever on the first one's fence. That is
        // what the "GPU wedge" has been all along: not a hung GPU (Windows logged no TDR across any wedge,
        // and the GPU sat idle) but a submission that never happened, leaving a fence nothing could ever
        // signal. Escalation rebuilt the sync objects, which bought exactly two more frames, and it looped —
        // 7 cycles, 14 rejected submits, in one capture.
        //
        // So a rejected submit is now treated as what it is: a DROPPED FRAME. Nothing is in flight, so
        //   - the fence is left unmarked, and BeginFrame skips the wait for an index with no pending submit
        //     (a fence with no submission behind it can never signal; waiting on it WAS the wedge);
        //   - present is skipped, because it would wait on a render-finished semaphore that will never be
        //     signaled either;
        //   - the acquire semaphore is replaced (see ReplaceImageAvailableSemaphore).
        // The frame index still advances, so this degrades to one visibly dropped frame, not a stall.
        var submitted = submitResult == VkResult.Success;
        if (submitted)
        {
            // Ledger: this index now has work in flight that will signal its fence. On a stuck fence the
            // breadcrumb compares this against the index being waited on, which is what tells "the GPU is
            // slow" apart from "nothing was ever submitted under it".
            Volatile.Write(ref _submitOrdinal[_currentFrame], _frameOrdinal);
            Volatile.Write(ref _submitPending[_currentFrame], 1);
            Interlocked.Increment(ref _submitsTotal);
        }
        else if (submitResult == VkResult.ErrorInitializationFailed)
        {
            Interlocked.Increment(ref _submitsRejected);
            ReplaceImageAvailableSemaphore(_currentFrame);
            // A thumbnail copy recorded into this frame died with it. Cancel it, or the next
            // BeginFrame on this index — which skips the fence wait, there being nothing to wait
            // for — would snapshot a readback buffer the GPU never wrote.
            if (_thumbPending && _thumbPendingIndex == _currentFrame)
                _thumbPending = false;
            _frameBegun = false;
            _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
            return;
        }
        else
        {
            submitResult.CheckResult();
        }

        // Present is intentionally not CheckResult'd — ErrorOutOfDateKHR/SuboptimalKHR on resize are
        // handled by the next BeginFrame's acquire. Log it (DEBUG only) so we can confirm present is
        // actually succeeding now that submit no longer throws on the benign Adreno quirk above.
        presentResult = DeviceApi.vkQueuePresentKHR(GraphicsQueue, &presentInfo);
        RenderDiag.Vk("present", presentResult, $"frame={_currentFrame} img={_currentImageIndex}");
        NoteDeviceLost(presentResult, "vkQueuePresentKHR");

        _frameBegun = false;
        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    /// <summary>
    /// Resolve a frame that was begun but will not be presented normally, so its fence and its acquired
    /// swapchain image do not leak into the next frame. Safe to call when no frame is in flight (no-op).
    /// <para>
    /// A frame between BeginFrame and EndFrame owns an acquired image, a pending acquire-semaphore signal
    /// and a recording command buffer. Simply walking away from it leaves the semaphore signaled with no
    /// waiter — and the next acquire on the same index would then signal an already-signaled binary
    /// semaphore, which is illegal and is a plausible route to a submit that waits forever. The cheapest
    /// legal resolution is to finish the frame anyway: close the render pass, submit (which consumes the
    /// acquire signal and signals the fence) and present whatever was recorded. A partially-drawn frame
    /// flashes; a wedged device does not come back.
    /// </para>
    /// Render thread only, like the rest of the frame state machine.
    /// </summary>
    public void AbortFrame()
    {
        if (!_frameBegun) return;
        // endRenderPass tracks reality: a frame that died before BeginRenderPass must not have
        // vkCmdEndRenderPass recorded into it (illegal), but still needs the submit to retire its
        // semaphore and fence.
        SubmitFrame(_commandBuffers[_currentFrame], endRenderPass: _renderPassBegun);
    }

    [Conditional("DEBUG")]
    private static void DebugLogBufferFull(int vertexOffset, int requestLength)
    {
        var usedMB = (vertexOffset * sizeof(float)) / (1024f * 1024f);
        var requestKB = (requestLength * sizeof(float)) / 1024f;
        Console.Error.WriteLine($"[VkBuffer] FULL at {usedMB:F1}MB, rejected {requestKB:F0}KB write");
    }

    public uint WriteVertices(ReadOnlySpan<float> data)
    {
        var maxFloats = (int)(_vertexBufferSize / sizeof(float));
        if (_vertexOffset + data.Length > maxFloats)
        {
            DebugLogBufferFull(_vertexOffset, data.Length);
            return uint.MaxValue;
        }

        var byteOffset = (uint)(_vertexOffset * sizeof(float));
        data.CopyTo(new Span<float>(_vertexMapped[_currentFrame] + _vertexOffset, data.Length));
        _vertexOffset += data.Length;
        return byteOffset;
    }

    public VkBuffer VertexBuffer => _vertexBuffers[_currentFrame];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain before teardown so we don't destroy resources the GPU is still reading — but skip
        // it when the GPU is already known stuck (an unbounded vkDeviceWaitIdle on a wedged device
        // would hang the quit, the same "Not responding" failure mode the recovery path avoids).
        if (!_fenceWaitStuck)
        {
            DeviceApi.vkDeviceWaitIdle();
        }

        CleanupSwapchain();
        if (_isOffscreen) CleanupOffscreenTarget();
        CleanupThumbnailTarget();

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vertexBuffers[i] != VkBuffer.Null)
            {
                DeviceApi.vkUnmapMemory(_vertexMemories[i]);
                DeviceApi.vkDestroyBuffer(_vertexBuffers[i]);
                DeviceApi.vkFreeMemory(_vertexMemories[i]);
            }
        }

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkDestroySemaphore(_imageAvailableSemaphores[i]);
            DeviceApi.vkDestroySemaphore(_renderFinishedSemaphores[i]);
            DeviceApi.vkDestroyFence(_inFlightFences[i]);
        }

        // Return the per-frame command buffers to the shared pool. When this context owns the device
        // the pool is destroyed just below anyway, but a shared-device window (one of several) must
        // free them explicitly or they leak until the app tears the device down.
        fixed (VkCommandBuffer* pCmds = _commandBuffers)
            DeviceApi.vkFreeCommandBuffers(CommandPool, (uint)_commandBuffers.Length, pCmds);

        // The surface is per-window (created against the shared instance). Destroy it before the
        // device tears the instance down. The swapchain that referenced it is already gone above.
        // Offscreen contexts have no surface — skip the destroy (Vortice binding AVs on Null).
        if (_surface != VkSurfaceKHR.Null)
            InstanceApi.vkDestroySurfaceKHR(_surface);

        // Only tear down the device if this context created it (single-window / offscreen). A device
        // shared by several windows is disposed by its owner once the last window is gone.
        if (_ownsDevice)
            _dev.Dispose();
    }

    private void CreateSwapchain(uint width, uint height)
    {
        InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(PhysicalDevice, _surface, out var caps);

        var extent = caps.currentExtent;
        if (extent.width == uint.MaxValue)
        {
            extent.width = Math.Clamp(width, caps.minImageExtent.width, caps.maxImageExtent.width);
            extent.height = Math.Clamp(height, caps.minImageExtent.height, caps.maxImageExtent.height);
        }

        var imageCount = caps.minImageCount + 1;
        if (caps.maxImageCount > 0 && imageCount > caps.maxImageCount)
            imageCount = caps.maxImageCount;

        // Chosen once against the surface in VulkanDevice.Create; the render pass baked in the same
        // format, so the swapchain images must match it (BGRA on desktop, RGBA on Android/Mali).
        var format = _dev.ColorFormat;
        SwapchainFormat = format;
        SwapchainWidth = extent.width;
        SwapchainHeight = extent.height;

        // Prefer Mailbox over Fifo when the driver offers it: Fifo (vsync) can block
        // vkAcquireNextImageKHR for up to a full vblank interval, adding up to ~16 ms of
        // input-to-pixel latency while actively rendering (zoom/pan). Mailbox replaces the
        // queued image instead of waiting — stale intermediate frames are worthless for an
        // interactive viewer. Fifo is the spec-guaranteed fallback. The event loop is
        // idle-suppressing, so Mailbox does not turn the app into a busy renderer.
        var presentMode = VkPresentModeKHR.Fifo;
        uint pmCount;
        InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(PhysicalDevice, _surface, &pmCount, null);
        if (pmCount > 0)
        {
            Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[(int)pmCount];
            fixed (VkPresentModeKHR* pModes = modes)
                InstanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(PhysicalDevice, _surface, &pmCount, pModes);
            foreach (var mode in modes)
            {
                if (mode == VkPresentModeKHR.Mailbox)
                {
                    presentMode = VkPresentModeKHR.Mailbox;
                    break;
                }
            }
        }

        var imageUsage = VkImageUsageFlags.ColorAttachment;
#if DEBUG
        // TransferSrc lets the DEBUG-only inspector copy the presented frame out for screenshots
        // (see VulkanContext.SwapchainReadback.cs). Desktop drivers always support it as a swapchain
        // usage, but Android/Mali swapchains often expose ColorAttachment ONLY — and requesting an
        // unsupported usage fails vkCreateSwapchainKHR (reported as VK_ERROR_SURFACE_LOST_KHR on
        // Android). Only add it when the surface actually advertises it.
        if ((caps.supportedUsageFlags & VkImageUsageFlags.TransferSrc) != 0)
            imageUsage |= VkImageUsageFlags.TransferSrc;
#endif

        // Pick a supported composite-alpha mode. Desktop supports Opaque; Android surfaces commonly
        // support Inherit ONLY, and requesting an unsupported mode fails swapchain creation (reported
        // as VK_ERROR_SURFACE_LOST_KHR on Mali).
        var compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque;
        if ((caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.Opaque) == 0)
        {
            if ((caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.Inherit) != 0)
                compositeAlpha = VkCompositeAlphaFlagsKHR.Inherit;
            else if ((caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.PreMultiplied) != 0)
                compositeAlpha = VkCompositeAlphaFlagsKHR.PreMultiplied;
            else if ((caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.PostMultiplied) != 0)
                compositeAlpha = VkCompositeAlphaFlagsKHR.PostMultiplied;
        }

        VkSwapchainCreateInfoKHR swapCI = new()
        {
            surface = _surface,
            minImageCount = imageCount,
            imageFormat = format,
            imageColorSpace = VkColorSpaceKHR.SrgbNonLinear,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = imageUsage,
            imageSharingMode = VkSharingMode.Exclusive,
            // After a device rotation, currentTransform becomes Rotate90/270 — an app that adopts it
            // as preTransform must render pre-rotated (rotate its projection), which VkRenderer does
            // not. Prefer Identity when supported: the presentation engine applies the rotation
            // itself (small compositor cost on mobile, correct visuals). Desktop surfaces report
            // Identity as current anyway, so this is Android-rotation-only in practice.
            preTransform = (caps.supportedTransforms & VkSurfaceTransformFlagsKHR.Identity) != 0
                ? VkSurfaceTransformFlagsKHR.Identity
                : caps.currentTransform,
            compositeAlpha = compositeAlpha,
            presentMode = presentMode,
            clipped = true,
            oldSwapchain = VkSwapchainKHR.Null
        };

        DeviceApi.vkCreateSwapchainKHR(&swapCI, null, out var swapchain).CheckResult();
        Swapchain = swapchain;

        // Get swapchain images
        DeviceApi.vkGetSwapchainImagesKHR(Swapchain, out uint imgCount).CheckResult();
        Span<VkImage> images = stackalloc VkImage[(int)imgCount];
        DeviceApi.vkGetSwapchainImagesKHR(Swapchain, images).CheckResult();
        _swapchainImages = images.ToArray();

        // Create image views
        _swapchainImageViews = new VkImageView[imgCount];
        for (var i = 0; i < imgCount; i++)
        {
            var viewCI = new VkImageViewCreateInfo(
                _swapchainImages[i],
                VkImageViewType.Image2D,
                format,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&viewCI, null, out _swapchainImageViews[i]).CheckResult();
        }

        // Create MSAA color image if needed
        if (MsaaSamples != VkSampleCountFlags.Count1)
        {
            VkImageCreateInfo msaaImageCI = new()
            {
                imageType = VkImageType.Image2D,
                format = format,
                extent = new VkExtent3D(extent.width, extent.height, 1),
                mipLevels = 1,
                arrayLayers = 1,
                samples = MsaaSamples,
                tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransientAttachment,
                sharingMode = VkSharingMode.Exclusive
            };
            DeviceApi.vkCreateImage(&msaaImageCI, null, out _msaaImage).CheckResult();

            DeviceApi.vkGetImageMemoryRequirements(_msaaImage, out var memReqs);
            VkMemoryAllocateInfo allocInfo = new()
            {
                allocationSize = memReqs.size,
                memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
            };
            DeviceApi.vkAllocateMemory(&allocInfo, null, out _msaaMemory).CheckResult();
            DeviceApi.vkBindImageMemory(_msaaImage, _msaaMemory, 0).CheckResult();

            var msaaViewCI = new VkImageViewCreateInfo(
                _msaaImage, VkImageViewType.Image2D, format,
                VkComponentMapping.Rgba,
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1));
            DeviceApi.vkCreateImageView(&msaaViewCI, null, out _msaaImageView).CheckResult();
        }

        // Create framebuffers
        _framebuffers = new VkFramebuffer[imgCount];
        Span<VkImageView> msaaAttachments = stackalloc VkImageView[2];
        for (var i = 0; i < imgCount; i++)
        {
            VkFramebufferCreateInfo fbCI;
            if (MsaaSamples != VkSampleCountFlags.Count1)
            {
                // MSAA: attachment 0 = multisample, attachment 1 = resolve (swapchain)
                msaaAttachments[0] = _msaaImageView;
                msaaAttachments[1] = _swapchainImageViews[i];
                fixed (VkImageView* pAtt = msaaAttachments)
                {
                    fbCI = new()
                    {
                        renderPass = RenderPass,
                        attachmentCount = 2,
                        pAttachments = pAtt,
                        width = extent.width,
                        height = extent.height,
                        layers = 1
                    };
                    DeviceApi.vkCreateFramebuffer(&fbCI, null, out _framebuffers[i]).CheckResult();
                }
            }
            else
            {
                var attachment = _swapchainImageViews[i];
                fbCI = new()
                {
                    renderPass = RenderPass,
                    attachmentCount = 1,
                    pAttachments = &attachment,
                    width = extent.width,
                    height = extent.height,
                    layers = 1
                };
                DeviceApi.vkCreateFramebuffer(&fbCI, null, out _framebuffers[i]).CheckResult();
            }
        }
    }

    private void CleanupSwapchain()
    {
        foreach (var fb in _framebuffers)
            DeviceApi.vkDestroyFramebuffer(fb);
        foreach (var iv in _swapchainImageViews)
            DeviceApi.vkDestroyImageView(iv);

        // Cleanup MSAA resources
        if (_msaaImageView != VkImageView.Null)
        {
            DeviceApi.vkDestroyImageView(_msaaImageView);
            _msaaImageView = VkImageView.Null;
        }
        if (_msaaImage != VkImage.Null)
        {
            DeviceApi.vkDestroyImage(_msaaImage);
            _msaaImage = VkImage.Null;
        }
        if (_msaaMemory != VkDeviceMemory.Null)
        {
            DeviceApi.vkFreeMemory(_msaaMemory);
            _msaaMemory = VkDeviceMemory.Null;
        }

        if (Swapchain != VkSwapchainKHR.Null)
            DeviceApi.vkDestroySwapchainKHR(Swapchain);

        _framebuffers = [];
        _swapchainImageViews = [];
        _swapchainImages = [];
        Swapchain = VkSwapchainKHR.Null;
    }

    private void CreateSyncObjects()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkCreateSemaphore(out _imageAvailableSemaphores[i]).CheckResult();
            DeviceApi.vkCreateSemaphore(out _renderFinishedSemaphores[i]).CheckResult();
            DeviceApi.vkCreateFence(VkFenceCreateFlags.Signaled, out _inFlightFences[i]).CheckResult();
        }
    }

    private void AllocateCommandBuffers()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            DeviceApi.vkAllocateCommandBuffer(CommandPool, out _commandBuffers[i]).CheckResult();
        }
    }

    private void CreateVertexBuffers()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            VkBufferCreateInfo bufCI = new()
            {
                size = _vertexBufferSize,
                usage = VkBufferUsageFlags.VertexBuffer,
                sharingMode = VkSharingMode.Exclusive
            };
            DeviceApi.vkCreateBuffer(&bufCI, null, out _vertexBuffers[i]).CheckResult();

            DeviceApi.vkGetBufferMemoryRequirements(_vertexBuffers[i], out var memReqs);
            VkMemoryAllocateInfo allocInfo = new()
            {
                allocationSize = memReqs.size,
                memoryTypeIndex = FindMemoryType(memReqs.memoryTypeBits,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
            };
            DeviceApi.vkAllocateMemory(&allocInfo, null, out _vertexMemories[i]).CheckResult();
            DeviceApi.vkBindBufferMemory(_vertexBuffers[i], _vertexMemories[i], 0);

            void* mapped;
            DeviceApi.vkMapMemory(_vertexMemories[i], 0, _vertexBufferSize, 0, &mapped);
            _vertexMapped[i] = (float*)mapped;
        }
    }
}
