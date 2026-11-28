using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

public sealed unsafe partial class VulkanContext
{
    // Set by Trim, cleared by the next frame begin (RestoreAfterTrim).
    private bool _trimmed;
    private uint _trimWidth, _trimHeight;

    /// <summary>True between a successful <see cref="Trim"/> and the next frame begin, which builds what Trim released.</summary>
    public bool IsTrimmed => _trimmed;

    /// <summary>
    /// Releases the GPU memory this context holds while nothing is drawn: a window's swapchain (its images, framebuffers,
    /// MSAA target, and the HDR, retained and layered images with it) and both vertex ring slots, then runs every deferred
    /// destroy at once (the drain proved the GPU idle). The next <see cref="BeginFrame"/> / <see cref="BeginOffscreenFrame"/>
    /// builds the swapchain again at the last extent (a changed size comes back through the usual out-of-date resize) and the
    /// ring at its initial size, before recording anything.
    /// <para>An offscreen context keeps its render target (its size is the caller's, and dropping it would only move the
    /// allocation to the next frame); it releases the ring. The shared depth image of a window goes and returns with the
    /// swapchain's framebuffers, which are rebuilt from the standing depth request.</para>
    /// <para>Only between frames: throws inside one. Returns false, releasing nothing, when the bounded device drain
    /// times out.</para>
    /// </summary>
    public bool Trim()
    {
        if (_disposed) return false;
        if (_recordingFrameCmd != VkCommandBuffer.Null)
            throw new InvalidOperationException("Trim cannot run inside a frame; call it between EndFrame and the next BeginFrame");
        if (!TryDrainDevice(DrainTimeoutNs, "trim")) return false;

        if (!_isOffscreen && !_trimmed)
        {
            FlushPresentQueueAfterDrain(true);
            _trimWidth = SwapchainWidth;
            _trimHeight = SwapchainHeight;
            CleanupSwapchain();
        }
        DestroyVertexRing();
        FlushAllDeferredDestroys();
        _trimmed = true;
        return true;
    }

    /// <summary>Runs the deferred destroys now. For a caller that has just drained the device through <see cref="Trim"/>.</summary>
    internal void RunDeferredDestroysNow() => FlushAllDeferredDestroys();

    private void DestroyVertexRing()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vertexBuffers[i] != VkBuffer.Null)
            {
                DeviceApi.vkUnmapMemory(_vertexMemories[i]);
                DeviceApi.vkDestroyBuffer(_vertexBuffers[i]);
                DeviceApi.vkFreeMemory(_vertexMemories[i]);
            }
            _vertexBuffers[i] = VkBuffer.Null;
            _vertexMemories[i] = VkDeviceMemory.Null;
            _vertexMapped[i] = null;
            _vertexBufferSizes[i] = 0;
        }
        _vertexOffset = 0;
        _vertexDemand = 0;
        _vertexRingWanted = 0;
    }

    // At the head of each frame begin, before anything is recorded or acquired.
    private void RestoreAfterTrim()
    {
        if (!_trimmed) return;
        _trimmed = false;
        if (!_isOffscreen && Swapchain == VkSwapchainKHR.Null)
            CreateSwapchain(_trimWidth, _trimHeight);
        for (var i = 0; i < MaxFramesInFlight; i++)
            CreateVertexBuffer(i, _vertexBufferSize);
    }
}
