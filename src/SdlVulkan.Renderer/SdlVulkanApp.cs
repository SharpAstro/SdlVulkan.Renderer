using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Owns the process-wide SDL lifecycle, the shared <see cref="VkInstance"/>, and the shared
/// <see cref="VulkanDevice"/> for a multi-window application. Create one at startup, then open
/// windows with <see cref="CreateWindow"/>; pair each with <see cref="VulkanContext.CreateForSharedDevice"/>
/// and its own <see cref="VkRenderer"/>. Every window shares this one device, so GPU resources built
/// against the device (page geometry buffers, image textures) stay valid in all of them — which is
/// what lets a document tab move between windows without re-uploading its geometry.
/// <para>
/// The shared device is created lazily from the first window's surface (a surface is needed to pick a
/// present-capable queue family). Dispose the app last, after every window and context is gone: it
/// tears down the device, then the instance, then SDL.
/// </para>
/// </summary>
public sealed class SdlVulkanApp : IDisposable
{
    private readonly VkSampleCountFlags _msaaSamples;
    private VulkanDevice? _device;
    private bool _disposed;

    public VkInstance Instance { get; }

    /// <summary>The shared device. Created on the first <see cref="CreateWindow"/> call (a surface is
    /// needed to pick a present-capable queue family), so accessing it before then throws.</summary>
    public VulkanDevice Device => _device
        ?? throw new InvalidOperationException(
            "The shared VulkanDevice is created from the first window's surface — call CreateWindow before accessing Device.");

    /// <summary>Whether the shared device has been created yet (true once a window exists).</summary>
    public bool HasDevice => _device is not null;

    private SdlVulkanApp(VkInstance instance, VkSampleCountFlags msaaSamples)
    {
        Instance = instance;
        _msaaSamples = msaaSamples;
    }

    /// <summary>
    /// Initializes SDL (video + events) and creates the shared Vulkan instance.
    /// <paramref name="msaaSamples"/> is baked into the shared device's render pass and pipelines, so
    /// every window on this app renders at the same sample count.
    /// </summary>
    public static SdlVulkanApp Create(VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1)
        => new(SdlVulkanWindow.InitSdlAndCreateInstance(), msaaSamples);

    /// <summary>
    /// Opens a new OS window with its own Vulkan surface. The first call also creates the shared
    /// device from this window's surface. Build the window's context with
    /// <see cref="VulkanContext.CreateForSharedDevice"/> passing <see cref="Device"/>.
    /// </summary>
    public SdlVulkanWindow CreateWindow(string title, int width, int height, bool maximized = true,
        bool borderless = false, bool alwaysOnTop = false, bool focusable = true)
    {
        var window = SdlVulkanWindow.CreateForApp(Instance, title, width, height,
            maximized, borderless, alwaysOnTop, focusable);
        // The app owns the instance, so the shared device must not destroy it (ownsInstance: false) —
        // it outlives any single device and backs every window's surface.
        _device ??= VulkanDevice.Create(Instance, window.Surface, _msaaSamples, ownsInstance: false);
        return window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The device (created ownsInstance: false) leaves the instance alone, so we destroy the
        // instance here after it. Callers must already have disposed every window + context — their
        // swapchains and surfaces reference this device/instance.
        _device?.Dispose();

        VulkanValidation.DestroyMessenger(Instance, GetApi(Instance));
        GetApi(Instance).vkDestroyInstance();

        Quit();
    }
}
