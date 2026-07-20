using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Opt-in Vulkan validation-layer diagnostics. Vulkan does almost no correctness checking itself; the
/// Khronos validation layer (<c>VK_LAYER_KHRONOS_validation</c>) is an optional software layer that
/// intercepts every call and reports spec violations. Its most valuable mode for this renderer is
/// SYNCHRONIZATION validation, which flags memory hazards (read-after-write etc.) STATICALLY at
/// command-record time — so a hazard like an SDF-atlas page uploaded and sampled in one submission
/// without a barrier (the Adreno GPU-wedge class fixed in the resilience work) is caught as a clear
/// <c>SYNC-HAZARD-*</c> message on any driver, instead of a nondeterministic vendor hang.
///
/// This wires the layer's output — previously dropped to the loader's default stderr sink — into a
/// prefixed stderr line plus a bounded in-memory ring buffer the debug inspector can read back
/// (<c>validation_report</c>). It is a diagnosis facility, not runtime self-healing: the sacrificial
/// recovery + fast-exit remain the field mechanism for an actual wedge; this is how you FIND and
/// prevent the bug in dev/CI so the field never sees it.
///
/// Gating (never on in a shipping build — the layer carries real CPU cost and is not shipped):
/// <list type="bullet">
/// <item>Layer + messenger + logging: DEBUG build <b>and</b> <c>SDLVK_VALIDATION=1</c> — opt-in even
/// in Debug. The layer validates EVERY <c>vkCmd*</c> call, so with it always-on a Debug session ran
/// several times slower than Release for no diagnostic benefit when nobody was reading the reports;
/// a plain Debug run is now fast by default and validation is a deliberate act (the tianwen
/// <c>run-gui</c> skill sets the variable when a GPU bug is actually being chased).</item>
/// <item>Synchronization validation (extra cost): additionally requires <c>SDLVK_SYNC_VALIDATION=1</c>.</item>
/// </list>
/// Everything degrades to a no-op when the layer is not installed on the host.
/// </summary>
internal static unsafe class VulkanValidation
{
    public const string LayerName = "VK_LAYER_KHRONOS_validation";

    // Ring buffer of the most recent messages surfaced to the inspector. Bounded so a chatty layer
    // (sync validation can repeat) can never grow unbounded.
    private const int MaxRetained = 512;
    private static readonly ConcurrentQueue<string> s_messages = new();
    private static long s_totalMessages;
    private static long s_syncHazards;

    // One messenger per instance handle (an app has one shared instance; a single-window / offscreen
    // context owns its own). Keyed so teardown destroys the right one and calling on an
    // instance with no messenger is a safe no-op.
    private static readonly ConcurrentDictionary<nint, VkDebugUtilsMessengerEXT> s_messengers = new();

#if DEBUG
    private const bool CompiledDebug = true;
#else
    private const bool CompiledDebug = false;
#endif

    /// <summary>Layer + messenger + logging on: a DEBUG build <b>and</b> <c>SDLVK_VALIDATION=1</c>.
    /// Opt-in even in Debug (see the class doc) — a Release/AOT build can never enable it.</summary>
    public static bool Enabled { get; } = CompiledDebug && IsEnvSet("SDLVK_VALIDATION");

    /// <summary>Synchronization validation additionally enabled (opt-in via <c>SDLVK_SYNC_VALIDATION=1</c>).
    /// Only meaningful when <see cref="Enabled"/>.</summary>
    public static bool SyncEnabled { get; } = Enabled && IsEnvSet("SDLVK_SYNC_VALIDATION");

    private static bool IsEnvSet(string name)
        => Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE" or "yes";

    /// <summary>True if the validation layer is actually installed and enumerable on this host.</summary>
    public static bool LayerAvailable()
    {
        uint count = 0;
        vkEnumerateInstanceLayerProperties(&count, null);
        if (count == 0)
            return false;
        var props = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = props)
            vkEnumerateInstanceLayerProperties(&count, p);
        foreach (var layer in props)
            if (VkStringInterop.ConvertToManaged(layer.layerName) == LayerName)
                return true;
        return false;
    }

    /// <summary>The messenger create-info (severities + the static callback). Also chained into the
    /// instance's <c>pNext</c> at creation so messages during vkCreateInstance / vkDestroyInstance are
    /// captured too.</summary>
    public static VkDebugUtilsMessengerCreateInfoEXT MessengerCreateInfo() => new()
    {
        messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
        messageType = VkDebugUtilsMessageTypeFlagsEXT.Validation
                    | VkDebugUtilsMessageTypeFlagsEXT.General
                    | VkDebugUtilsMessageTypeFlagsEXT.Performance,
        pfnUserCallback = &Callback
    };

    /// <summary>Installs the persistent messenger on an instance created with validation enabled.
    /// Best-effort — a failure just means the diagnostics are unavailable, never fatal.</summary>
    public static void InstallMessenger(VkInstance instance, VkInstanceApi api)
    {
        try
        {
            var ci = MessengerCreateInfo();
            if (api.vkCreateDebugUtilsMessengerEXT(&ci, out var messenger) == VkResult.Success)
                s_messengers[instance.Handle] = messenger;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[validation] messenger install failed: {ex.Message}");
        }
    }

    /// <summary>Destroys the instance's messenger, if any. Call immediately before vkDestroyInstance.
    /// A no-op for an instance that never had one, so both teardown paths can call it unconditionally.</summary>
    public static void DestroyMessenger(VkInstance instance, VkInstanceApi api)
    {
        if (s_messengers.TryRemove(instance.Handle, out var messenger) && messenger != VkDebugUtilsMessengerEXT.Null)
            api.vkDestroyDebugUtilsMessengerEXT(messenger);
    }

    /// <summary>A snapshot for the inspector: running totals plus the retained recent messages.</summary>
    public static ValidationSnapshot Snapshot()
        => new(Interlocked.Read(ref s_totalMessages), Interlocked.Read(ref s_syncHazards), s_messages.ToArray());

    [UnmanagedCallersOnly]
    private static uint Callback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        if (data != null && data->pMessage != null)
        {
            var text = Marshal.PtrToStringUTF8((nint)data->pMessage) ?? string.Empty;
            var line = $"[validation:{severity}] {text}";
            Console.Error.WriteLine(line);
            Interlocked.Increment(ref s_totalMessages);
            if (text.Contains("SYNC-HAZARD", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref s_syncHazards);
            s_messages.Enqueue(line);
            while (s_messages.Count > MaxRetained && s_messages.TryDequeue(out _)) { }
        }
        return 0; // VK_FALSE — never abort the call that triggered the message
    }
}

/// <summary>Inspector-facing snapshot of the validation state (see <see cref="VulkanValidation.Snapshot"/>).</summary>
internal readonly record struct ValidationSnapshot(long TotalMessages, long SyncHazards, string[] Recent);
