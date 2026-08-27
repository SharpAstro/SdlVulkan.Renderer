using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// An offscreen <see cref="VulkanContext"/> on its OWN instance with VK_LAYER_KHRONOS_validation, the
/// synchronization-validation feature and a debug-utils messenger that records every warning and
/// error into <see cref="Messages"/>. Shared by the tests that assert the validation layer stays
/// silent over some GPU-side sequence (the SDF atlas flood, deferred destruction), so each of them
/// states only the sequence it guards.
/// </summary>
/// <remarks>
/// The debug-utils callback is a plain unmanaged function pointer with no managed user data, so the
/// sink is static. The tests using it join the <c>OffscreenGpu</c> collection and therefore run
/// serially; each clears <see cref="Messages"/> before its sequence.
/// <para><see cref="TryCreate"/> returns false with a skip reason when the layer or extensions are
/// unavailable, mirroring the ICD-absent skip of the other offscreen tests: a runner without the layer
/// is inconclusive, not a false pass. The CI test lane installs vulkan-validationlayers.</para>
/// </remarks>
internal static class ValidatedOffscreen
{
    public static readonly ConcurrentQueue<string> Messages = new();

    public static string DumpMessages() =>
        Messages.IsEmpty ? "(none)" : string.Join("\n", Messages);

    // Sync-validation messages are reported with a "SYNC-HAZARD-*" message-id name (e.g.
    // SYNC-HAZARD-READ-AFTER-WRITE / -WRITE-AFTER-WRITE). Match on that so an unrelated validation
    // warning on a quirky runner does not fail a lane that guards the synchronization class only.
    public static bool IsSyncHazard(string msg) =>
        msg.Contains("SYNC-HAZARD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any validation ERROR (not a warning): the bar for a sequence that must be spec-clean.</summary>
    public static bool IsError(string msg) =>
        msg.StartsWith("[Error]", StringComparison.Ordinal);

    /// <summary>Tears down what <see cref="TryCreate"/> built. Skip this after a GPU wedge: teardown
    /// blocks on the same hung device.</summary>
    public static void Destroy(VulkanContext? ctx, VkDebugUtilsMessengerEXT messenger, VkInstanceApi? api)
    {
        if (messenger != VkDebugUtilsMessengerEXT.Null && api is not null)
            api.vkDestroyDebugUtilsMessengerEXT(messenger);
        ctx?.Dispose(); // owns + destroys the instance
    }

    public static unsafe bool TryCreate(uint width, uint height,
        out VulkanContext? ctx, out VkDebugUtilsMessengerEXT messenger, out VkInstanceApi? api, out string skip)
    {
        ctx = null;
        messenger = VkDebugUtilsMessengerEXT.Null;
        api = null;
        skip = string.Empty;

        try
        {
            vkInitialize().CheckResult();

            const string validationLayer = "VK_LAYER_KHRONOS_validation";
            if (!InstanceLayerAvailable(validationLayer))
            {
                skip = $"{validationLayer} not available on this host (install vulkan-validationlayers)";
                return false;
            }

            var syncFeature = stackalloc VkValidationFeatureEnableEXT[1]
            {
                VkValidationFeatureEnableEXT.SynchronizationValidation
            };
            VkValidationFeaturesEXT validationFeatures = new()
            {
                enabledValidationFeatureCount = 1,
                pEnabledValidationFeatures = syncFeature
            };

            VkDebugUtilsMessengerCreateInfoEXT debugCI = new()
            {
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.Validation | VkDebugUtilsMessageTypeFlagsEXT.General,
                pfnUserCallback = &DebugCallback
            };
            // Chain: instance -> validation features (turns on sync validation) -> messenger CI (also
            // captures messages emitted during vkCreateInstance / vkDestroyInstance).
            validationFeatures.pNext = &debugCI;

            using var layers = new VkStringArray([validationLayer]);
            using var extensions = new VkStringArray([
                VK_EXT_DEBUG_UTILS_EXTENSION_NAME,
                VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME
            ]);

            VkInstanceCreateInfo instanceCI = new()
            {
                pNext = &validationFeatures,
                enabledLayerCount = layers.Length,
                ppEnabledLayerNames = layers,
                enabledExtensionCount = extensions.Length,
                ppEnabledExtensionNames = extensions
            };

            vkCreateInstance(&instanceCI, null, out var instance).CheckResult();
            api = GetApi(instance);
            api.vkCreateDebugUtilsMessengerEXT(&debugCI, out messenger).CheckResult();

            ctx = VulkanContext.CreateOffscreen(instance, width, height);
            return true;
        }
        catch (Exception e)
        {
            // No ICD, or the layer/extensions are advertised but fail at create time -> inconclusive,
            // skip rather than fail. If the instance came up (api set) tear it down; a null api means
            // vkCreateInstance itself failed.
            skip = $"Vulkan validation stack not usable on this host: {e.Message}";
            if (api is not null)
            {
                if (messenger != VkDebugUtilsMessengerEXT.Null)
                    api.vkDestroyDebugUtilsMessengerEXT(messenger);
                api.vkDestroyInstance();
            }
            ctx = null;
            messenger = VkDebugUtilsMessengerEXT.Null;
            api = null;
            return false;
        }
    }

    private static unsafe bool InstanceLayerAvailable(string layerName)
    {
        uint count = 0;
        vkEnumerateInstanceLayerProperties(&count, null);
        if (count == 0)
            return false;
        var props = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = props)
            vkEnumerateInstanceLayerProperties(&count, p);
        foreach (var layer in props)
            if (VkStringInterop.ConvertToManaged(layer.layerName) == layerName)
                return true;
        return false;
    }

    [UnmanagedCallersOnly]
    private static unsafe uint DebugCallback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        if (data != null && data->pMessage != null)
        {
            var msg = Marshal.PtrToStringUTF8((nint)data->pMessage) ?? string.Empty;
            Messages.Enqueue($"[{severity}] {msg}");
        }
        return 0; // VK_FALSE: the app must not abort the call that triggered the message
    }
}
