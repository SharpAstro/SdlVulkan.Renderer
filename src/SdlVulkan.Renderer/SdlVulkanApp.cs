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
    // The second device DeviceFor makes for transparent windows the shared device cannot give alpha. Sought once:
    // which devices exist does not change while the app runs, so a miss is not worth repeating per window.
    private VulkanDevice? _alphaDevice;
    private bool _alphaDeviceSought;
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
    /// <see cref="VulkanContext.CreateForSharedDevice"/> passing <see cref="Device"/>. A <paramref name="hidden"/>
    /// window stays off screen (and off the taskbar) until <see cref="SdlVulkanWindow.Show"/>: an app that starts in
    /// the background, to be brought up by a global hotkey, starts this way.
    /// </summary>
    public SdlVulkanWindow CreateWindow(string title, int width, int height, bool maximized = true,
        bool borderless = false, bool alwaysOnTop = false, bool focusable = true, bool hidden = false)
    {
        var window = SdlVulkanWindow.CreateForApp(Instance, title, width, height,
            maximized, borderless, alwaysOnTop, focusable, hidden);
        // The app owns the instance, so the shared device must not destroy it (ownsInstance: false) —
        // it outlives any single device and backs every window's surface.
        _device ??= VulkanDevice.Create(Instance, window.Surface, _msaaSamples, ownsInstance: false);
        return window;
    }

    /// <summary>
    /// Opens a window whose pixels blend with what is behind it, as <see cref="SdlVulkanWindow.CreateTransparent"/>
    /// does for a standalone one. Build its context with <see cref="VulkanContext.CreateTransparentForSharedDevice"/>
    /// passing <see cref="DeviceFor"/>, not <see cref="Device"/>. Opening one first does not change which device is
    /// shared: that is still picked as <see cref="CreateWindow"/> picks it, for the ordinary windows.
    /// </summary>
    public SdlVulkanWindow CreateTransparentWindow(string title, int width, int height, bool borderless = true,
        bool alwaysOnTop = true, bool focusable = true, bool hidden = false)
    {
        var window = SdlVulkanWindow.CreateForApp(Instance, title, width, height, maximized: false,
            borderless, alwaysOnTop, focusable, hidden, transparent: true);
        _device ??= VulkanDevice.Create(Instance, window.Surface, _msaaSamples, ownsInstance: false);
        return window;
    }

    /// <summary>
    /// The device to build <paramref name="window"/>'s context on. <see cref="Device"/> for an ordinary window, and
    /// for a transparent one whenever the shared device can give it alpha. Only when it cannot (on Windows, whose
    /// native drivers offer no premultiplied alpha) is it a second device, made on first need and kept for every later
    /// transparent window: the one on the same GPU whose surface offers premultiplied alpha, typically Mesa's Dozen.
    /// Where no device offers it, <see cref="Device"/> again, and the window's swapchain is opaque (see
    /// <see cref="VulkanContext.PresentsAlpha"/>).
    /// </summary>
    /// <remarks>
    /// A second device does not share GPU resources with the first: textures, meshes and buffers made on
    /// <see cref="Device"/> are not valid in a window on the other one. A transparent companion window draws its own
    /// content, so this costs it nothing; it does mean a document cannot move into such a window without
    /// re-uploading.
    /// </remarks>
    public VulkanDevice DeviceFor(SdlVulkanWindow window)
    {
        var shared = Device;
        if (!window.IsTransparent || shared.SupportsPremultipliedAlpha(window.Surface))
            return shared;
        if (!_alphaDeviceSought)
        {
            _alphaDeviceSought = true;
            _alphaDevice = VulkanDevice.TryCreateForPremultipliedAlpha(Instance, window.Surface,
                shared.PhysicalDevice, _msaaSamples, ownsInstance: false);
        }
        return _alphaDevice is { } alpha && alpha.SupportsPremultipliedAlpha(window.Surface) ? alpha : shared;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The device (created ownsInstance: false) leaves the instance alone, so we destroy the
        // instance here after it. Callers must already have disposed every window + context — their
        // swapchains and surfaces reference this device/instance.
        _alphaDevice?.Dispose();
        _device?.Dispose();

        VulkanValidation.DestroyMessenger(Instance, GetApi(Instance));
        GetApi(Instance).vkDestroyInstance();

        Quit();
    }
}
