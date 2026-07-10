using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DIR.Lib;
using SdlVulkan.Renderer;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Deterministic guard for the GPU-wedge class of bug the 6.17 resilience work fixed. On Adreno a
/// freshly appended SDF-atlas page was uploaded (a transfer write + Undefined→ShaderReadOnly layout
/// transition) and then sampled in the fragment shader inside the SAME queue submission with no
/// barrier between the two — a read-after-write data hazard. A well-behaved software driver (Mesa
/// lavapipe) tolerates it; a real tiler (Adreno) can hang the GPU on it. The fix quarantines a
/// just-appended page for one frame so its first upload and first sample land in different
/// submissions.
///
/// The Khronos validation layer's SYNCHRONIZATION VALIDATION flags exactly that hazard statically —
/// it does not need the hazard to actually hang, so it reproduces deterministically on lavapipe in
/// CI where the vendor hang never would. This test creates an offscreen context with the validation
/// layer + sync validation enabled, installs a debug-utils messenger that records validation
/// messages, then floods the SDF atlas with enough distinct glyphs to force several page appends and
/// renders a handful of frames. It asserts ZERO SYNC-HAZARD messages: green with the quarantine +
/// upload/sample barriers in place, red if either regresses.
///
/// Skips (does not fail) when the validation layer / debug-utils extension is unavailable, mirroring
/// the ICD-absent skip in the other offscreen tests — so a runner without the layer is inconclusive,
/// not a false pass. The CI test lane installs vulkan-validationlayers so it actually runs there.
/// </summary>
public sealed class SyncValidationGlyphFloodTests
{
    private const uint Width = 256;
    private const uint Height = 256;

    // Small page (power of two): glyphs rasterize at SdfRasterSize (64px), so a 256² page holds only
    // a handful of them and the ~36-glyph flood below spreads across several page appends while
    // staying well under the atlas's 16-page cap (so this exercises appends + upload/sample interleave,
    // not eviction).
    private const int AtlasDim = 256;

    // Deadman: the whole flood must finish well inside this. On a genuinely wedged device
    // WaitOffscreenFrameComplete would block forever, so the flood runs on a background task the test
    // only waits on with a timeout — a wedge fails the test fast with a breadcrumb instead of hanging
    // the CI job to its own (coarser) timeout-minutes ceiling.
    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(60);

    // The debug-utils callback is a plain unmanaged function pointer (no managed user-data), so its
    // sink is static. This class holds a single test and no parallel siblings touch this queue.
    private static readonly ConcurrentQueue<string> s_messages = new();

    private static string FontPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    [Fact]
    public async Task GlyphFlood_offscreen_emits_no_synchronization_hazards()
    {
        if (!TryCreateValidatedOffscreenContext(out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }

        s_messages.Clear();
        var wedged = false;
        try
        {
            var flood = Task.Run(RunFlood(ctx!), TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(flood, Task.Delay(Deadman, TestContext.Current.CancellationToken)) == flood;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: SDF glyph flood did not finish within {Deadman.TotalSeconds:0}s — " +
                            $"possible GPU wedge. Validation messages so far:\n{DumpMessages()}");
            }
            await flood; // surface any exception thrown inside the flood

            var hazards = s_messages.Where(IsSyncHazard).ToArray();
            Assert.True(hazards.Length == 0,
                $"Vulkan synchronization validation reported {hazards.Length} hazard(s) during the SDF glyph " +
                $"flood — the atlas upload/sample path has a read-after-write hazard (see 6.17 resilience):\n" +
                string.Join("\n\n", hazards));
        }
        finally
        {
            // On a wedge, deliberately leak the context: teardown (vkDeviceWaitIdle / vkFreeMemory)
            // would block on the same hung device — the exact trap the 6.17 host fast-exit avoids.
            if (!wedged)
            {
                if (messenger != VkDebugUtilsMessengerEXT.Null && api is not null)
                    api.vkDestroyDebugUtilsMessengerEXT(messenger);
                ctx?.Dispose(); // owns + destroys the instance
            }
        }
    }

    // Builds the flood body as an Action so Task.Run can host it on a background thread the test only
    // waits on with a timeout. The atlas quarantines a just-appended page for one frame, so a glyph's
    // page is uploaded on frame N and first sampled on frame N+1; re-drawing the whole set each frame
    // interleaves "upload new pages this submission" with "sample pages uploaded last submission" —
    // the exact pattern the hazard lived in. Creates and disposes its own renderer so nothing GPU-side
    // outlives a clean run.
    private static Action RunFlood(VulkanContext ctx) => () =>
    {
        var font = FontPath;
        const float size = 32f;
        var black = new RGBAColor32(0, 0, 0, 255);
        var white = new RGBAColor32(255, 255, 255, 255);

        // A-Z + 0-9: 36 distinct glyphs, all present in DejaVuSans, enough to append several 256² pages.
        var runes = Enumerable.Range('A', 26).Concat(Enumerable.Range('0', 10))
            .Select(c => new Rune((char)c)).ToArray();

        using var renderer = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: AtlasDim);

        // Warm every glyph synchronously in OnPreFlush (runs before the atlas Flush inside
        // BeginOffscreenFrame, so freshly rasterized cells upload this same frame). The per-frame
        // upload byte budget spreads a 36-glyph warm across a few frames, which is what we want.
        renderer.OnPreFlush = () =>
        {
            foreach (var r in runes)
                renderer.PreWarmSdfGlyph(font, size, r);
        };

        for (var frame = 0; frame < 6; frame++)
        {
            if (!renderer.BeginOffscreenFrame(black))
                continue;

            renderer.BeginSdfGlyphBatch(white, size);
            float x = 6f, y = 40f;
            foreach (var r in runes)
            {
                renderer.AddBatchedSdfGlyphAtBaseline(font, r, -1, baselineX: x, baselineY: y);
                x += 34f;
                if (x > Width - 34f) { x = 6f; y += 40f; if (y > Height - 8f) y = 40f; }
            }
            renderer.EndGlyphBatch();

            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }
    };

    // Sync-validation messages are reported with a "SYNC-HAZARD-*" message-id name (e.g.
    // SYNC-HAZARD-READ-AFTER-WRITE / -WRITE-AFTER-WRITE). Match on that so an unrelated validation
    // warning on a quirky runner does not fail the lane — this test guards the synchronization class
    // specifically.
    private static bool IsSyncHazard(string msg) =>
        msg.Contains("SYNC-HAZARD", StringComparison.OrdinalIgnoreCase);

    private static string DumpMessages() =>
        s_messages.IsEmpty ? "(none)" : string.Join("\n", s_messages);

    // Builds an offscreen context with VK_LAYER_KHRONOS_validation + the synchronization-validation
    // feature + a debug-utils messenger sinking into s_messages. Returns false (with a skip reason)
    // when the validation stack is unavailable, rather than failing.
    private static unsafe bool TryCreateValidatedOffscreenContext(
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

            ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
            return true;
        }
        catch (Exception e)
        {
            // No ICD, or the layer/extensions are advertised but fail at create time → inconclusive,
            // skip rather than fail (mirrors the other offscreen tests' ICD-absent behaviour). If the
            // instance came up (api set) tear it down; a null api means vkCreateInstance itself failed.
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
            s_messages.Enqueue($"[{severity}] {msg}");
        }
        return 0; // VK_FALSE — the app must not abort the call that triggered the message
    }
}
