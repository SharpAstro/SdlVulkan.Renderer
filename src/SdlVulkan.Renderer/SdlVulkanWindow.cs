using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using static SDL3.SDL;

namespace SdlVulkan.Renderer;

/// <summary>
/// Owns the SDL window and Vulkan instance/surface lifecycle.
/// </summary>
public sealed unsafe class SdlVulkanWindow : IDisposable
{
    public nint Handle { get; }
    public VkInstance Instance { get; }
    public VkSurfaceKHR Surface { get; }

    private SdlVulkanWindow(nint handle, VkInstance instance, VkSurfaceKHR surface)
    {
        Handle = handle;
        Instance = instance;
        Surface = surface;
    }

    public static SdlVulkanWindow Create(string title, int width, int height)
    {
        if (!Init(InitFlags.Video | InitFlags.Events))
            throw new InvalidOperationException($"SDL_Init failed: {GetError()}");

        VulkanLoadLibrary(null);
        vkInitialize().CheckResult();

        var window = CreateWindow(title, width, height,
            WindowFlags.Vulkan | WindowFlags.Resizable);
        if (window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {GetError()}");

        var instance = CreateVulkanInstance();

        nint surfaceHandle;
        if (!VulkanCreateSurface(window, instance.Handle, nint.Zero, out surfaceHandle))
            throw new InvalidOperationException($"SDL_Vulkan_CreateSurface failed: {GetError()}");
        var surface = new VkSurfaceKHR((ulong)surfaceHandle);

        return new SdlVulkanWindow(window, instance, surface);
    }

    public void GetSizeInPixels(out int w, out int h) => GetWindowSizeInPixels(Handle, out w, out h);

    public void ToggleFullscreen()
    {
        var flags = GetWindowFlags(Handle);
        SetWindowFullscreen(Handle, (flags & WindowFlags.Fullscreen) == 0);
    }

    public void Dispose()
    {
        DestroyWindow(Handle);
        Quit();
    }

    private static VkInstance CreateVulkanInstance()
    {
        var sdlExtensionNames = VulkanGetInstanceExtensions(out var extensionCount)
            ?? throw new InvalidOperationException("SDL_Vulkan_GetInstanceExtensions failed");
        using var extensionArray = new VkStringArray(sdlExtensionNames);

        VkInstanceCreateInfo instanceCI = new()
        {
            enabledExtensionCount = extensionCount,
            ppEnabledExtensionNames = extensionArray
        };

#if DEBUG
        const string validationLayerName = "VK_LAYER_KHRONOS_validation";
        uint layerCount = 0;
        vkEnumerateInstanceLayerProperties(&layerCount, null);
        var layerProps = new VkLayerProperties[layerCount];
        fixed (VkLayerProperties* pLayerProps = layerProps)
            vkEnumerateInstanceLayerProperties(&layerCount, pLayerProps);
        bool hasValidation = false;
        foreach (var layer in layerProps)
        {
            if (VkStringInterop.ConvertToManaged(layer.layerName) == validationLayerName)
            {
                hasValidation = true;
                break;
            }
        }
        using var validationLayers = hasValidation ? new VkStringArray([validationLayerName]) : default;
        if (hasValidation)
        {
            instanceCI.enabledLayerCount = validationLayers.Length;
            instanceCI.ppEnabledLayerNames = validationLayers;
        }
#endif

        vkCreateInstance(&instanceCI, null, out var instance).CheckResult();
        return instance;
    }
}
