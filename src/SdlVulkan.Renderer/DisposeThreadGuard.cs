using System;
using Microsoft.Extensions.Logging;

namespace SdlVulkan.Renderer;

/// <summary>
/// Names the hang <see cref="ShutdownDrain"/> prevents. An SDL window and its Vulkan context belong to the
/// thread that ran the loop; disposed from another thread (typically the continuation of an awaited
/// shutdown task) they block against a loop nobody is pumping. <see cref="Check"/> is the pure decision.
/// </summary>
internal static class DisposeThreadGuard
{
    /// <summary>
    /// Null when the dispose is fine (same thread, or no owner recorded yet); otherwise the message.
    /// </summary>
    internal static string? Check(int ownerThreadId, int currentThreadId, string what)
    {
        if (ownerThreadId == 0 || ownerThreadId == currentThreadId) return null;
        return $"{what} was disposed on thread {currentThreadId}, but it belongs to thread {ownerThreadId}, " +
               "the thread that ran the event loop. Dispose it on that thread: after SdlEventLoop.Run returns, " +
               "call ShutdownDrain.PumpUntilComplete(loop, drainTask) to wait for background work with the loop " +
               "pumping, then dispose.";
    }

    /// <summary>Throws in DEBUG, logs one warning in Release.</summary>
    internal static void Enforce(int ownerThreadId, string what) =>
        Enforce(ownerThreadId, Environment.CurrentManagedThreadId, what, SdlVulkanLog.Logger);

    internal static void Enforce(int ownerThreadId, int currentThreadId, string what, ILogger logger)
    {
        var message = Check(ownerThreadId, currentThreadId, what);
        if (message is null) return;
#if DEBUG
        throw new InvalidOperationException(message);
#else
        logger.DisposedOffThread(message);
#endif
    }
}
