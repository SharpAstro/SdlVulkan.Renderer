using Microsoft.Extensions.Logging;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Source-generated log events for the renderer's unconditional diagnostics (see
/// <see cref="SdlVulkanLog"/> for where they go). <c>[LoggerMessage]</c> compiles each template
/// once at build time — the generated body gates on <c>IsEnabled</c>, allocates nothing when the
/// level is filtered, and never runtime-parses a template — which also makes it safe for messages
/// whose arguments may themselves contain braces (breadcrumbs, validation text).
/// <para>
/// Message templates deliberately reproduce the exact former <c>Console.Error</c> text, including
/// the <c>[SdlEventLoop]</c>/<c>[VulkanContext]</c> prefixes: field-log tooling greps these lines,
/// and the default stderr sink prints message-only, so the prefix is the only component identity
/// the line carries there. Event ids: 1xx event loop, 2xx context/frame, 3xx readback,
/// 4xx validation, 5xx device selection.
/// </para>
/// </summary>
internal static partial class LogEvents
{
    [LoggerMessage(101, LogLevel.Information, "[SdlEventLoop] GPU recovery completed (window {WindowId}); resuming.")]
    public static partial void GpuRecoveryCompleted(this ILogger logger, uint windowId);

    [LoggerMessage(102, LogLevel.Critical, "[SdlEventLoop] GPU recovery failed: {Reason}. Stopping event loop.")]
    public static partial void GpuRecoveryFailed(this ILogger logger, string? reason);

    [LoggerMessage(103, LogLevel.Critical, "[SdlEventLoop] GPU wedged: recovery did not return within {DeadlineMs}ms (window {WindowId}). Abandoning device.")]
    public static partial void GpuWedgedRecoveryDeadline(this ILogger logger, long deadlineMs, uint windowId);

    [LoggerMessage(104, LogLevel.Error, "[SdlEventLoop] OnGpuWedged handler threw: {ExceptionType}: {ExceptionMessage}")]
    public static partial void OnGpuWedgedHandlerThrew(this ILogger logger, string exceptionType, string exceptionMessage);

    [LoggerMessage(105, LogLevel.Information, "[SdlEventLoop] GPU fence recovered after {StuckMs}ms (window {WindowId}); no teardown needed.")]
    public static partial void GpuFenceRecovered(this ILogger logger, long stuckMs, uint windowId);

    [LoggerMessage(106, LogLevel.Warning, "[SdlEventLoop] GPU fence late (window {WindowId}); retrying without teardown ({Idle}); {Ledger}.")]
    public static partial void GpuFenceLate(this ILogger logger, uint windowId, string idle, string ledger);

    [LoggerMessage(107, LogLevel.Warning, "[SdlEventLoop] GPU fence stuck for {StuckMs}ms (window {WindowId}); escalating to full recovery.")]
    public static partial void GpuFenceStuckEscalating(this ILogger logger, long stuckMs, uint windowId);

    [LoggerMessage(108, LogLevel.Error, "[SdlEventLoop] wedge breadcrumb (window {WindowId}): {AtlasBreadcrumb}; {ChurnBreadcrumb}; {Ledger}; {CleanFrameAge}")]
    public static partial void WedgeBreadcrumb(this ILogger logger, uint windowId, string atlasBreadcrumb, string churnBreadcrumb, string ledger, string cleanFrameAge);

    [LoggerMessage(109, LogLevel.Critical, "[SdlEventLoop] GPU wedged: {Escalations} stuck escalations without a clean frame (window {WindowId}). Abandoning device.")]
    public static partial void GpuWedgedEscalationLimit(this ILogger logger, int escalations, uint windowId);

    [LoggerMessage(110, LogLevel.Warning, "[SdlEventLoop] Vulkan error mid-frame (window {WindowId}): {Result}. Recovering swapchain.")]
    public static partial void VulkanErrorMidFrame(this ILogger logger, uint windowId, VkResult result);

    [LoggerMessage(111, LogLevel.Error, "[SdlEventLoop] render degraded (recover streak {RecoverStreak}, window {WindowId}); requesting load-shed.")]
    public static partial void RenderDegraded(this ILogger logger, int recoverStreak, uint windowId);

    [LoggerMessage(112, LogLevel.Error, "[SdlEventLoop] OnRenderDegraded handler threw: {ExceptionType}: {ExceptionMessage}")]
    public static partial void OnRenderDegradedHandlerThrew(this ILogger logger, string exceptionType, string exceptionMessage);

    [LoggerMessage(113, LogLevel.Critical, "[SdlEventLoop] Vulkan recovery failed: {ExceptionType}: {ExceptionMessage}. Stopping event loop.")]
    public static partial void VulkanRecoveryFailed(this ILogger logger, string exceptionType, string exceptionMessage);

    [LoggerMessage(114, LogLevel.Error, "[SdlEventLoop] AbortFrame after a mid-frame exception threw: {ExceptionType}: {ExceptionMessage}")]
    public static partial void AbortFrameThrew(this ILogger logger, string exceptionType, string exceptionMessage);

    [LoggerMessage(201, LogLevel.Error, "[VulkanContext] vkQueueSubmit rejected frame {FrameIndex} (ErrorInitializationFailed); dropped the frame and replaced its acquire semaphore.")]
    public static partial void SubmitRejectedFrameDropped(this ILogger logger, int frameIndex);

    [LoggerMessage(202, LogLevel.Critical, "[VulkanContext] VK_ERROR_DEVICE_LOST from {Where} — the device is gone (GPU reset / TDR). This is a real device loss, not a late fence.")]
    public static partial void DeviceLostReported(this ILogger logger, string where);

    [LoggerMessage(203, LogLevel.Warning, "[VulkanContext] GPU already known stuck; skipping drain before {Context}.")]
    public static partial void DrainSkippedGpuStuck(this ILogger logger, string context);

    [LoggerMessage(204, LogLevel.Warning, "[VulkanContext] GPU did not idle within {TimeoutMs}ms during {Context}; forcing teardown.")]
    public static partial void DrainTimedOut(this ILogger logger, ulong timeoutMs, string context);

    [LoggerMessage(205, LogLevel.Warning, "[VulkanContext] GPU already known stuck; skipping {Context} drain.")]
    public static partial void AtlasDrainSkippedGpuStuck(this ILogger logger, string context);

    [LoggerMessage(206, LogLevel.Warning, "[VulkanContext] {Context} drain timed out after {TimeoutMs}ms; proceeding (atlas swap may race a wedged GPU that is about to be recovered).")]
    public static partial void AtlasDrainTimedOut(this ILogger logger, string context, ulong timeoutMs);

    [LoggerMessage(301, LogLevel.Warning, "[VulkanContext] screenshot readback skipped: GPU known stuck.")]
    public static partial void ReadbackSkippedGpuStuck(this ILogger logger);

    [LoggerMessage(302, LogLevel.Warning, "[VulkanContext] screenshot readback timed out after {TimeoutMs}ms; aborting (GPU saturated).")]
    public static partial void ReadbackTimedOut(this ILogger logger, ulong timeoutMs);

    [LoggerMessage(401, LogLevel.Warning, "[validation] messenger install failed: {Reason}")]
    public static partial void ValidationMessengerInstallFailed(this ILogger logger, string reason);

    // Level comes from the validation layer's own severity, so it is a runtime parameter.
    [LoggerMessage(EventId = 402, Message = "{ValidationLine}")]
    public static partial void ValidationMessage(this ILogger logger, LogLevel level, string validationLine);

    // Logged once per device creation. On a multi-GPU host (a discrete card plus an iGPU) the picker
    // takes the first device that satisfies the requirements, and enumeration order is up to the
    // loader, so which GPU is in use is not otherwise knowable. A later GPU report (device loss, a
    // wedge, a driver quirk) cannot be attributed to hardware without this line.
    [LoggerMessage(501, LogLevel.Information, "[VulkanDevice] selected {DeviceName} ({DeviceType}, driver {DriverVersion}, Vulkan {ApiVersion}), queue family {QueueFamily}, from {DeviceCount} enumerated.")]
    public static partial void PhysicalDeviceSelected(this ILogger logger, string deviceName, string deviceType, string driverVersion, string apiVersion, uint queueFamily, uint deviceCount);
}
