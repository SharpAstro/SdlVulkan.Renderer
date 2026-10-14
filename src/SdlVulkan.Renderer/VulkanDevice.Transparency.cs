using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Device choice for a window whose pixels blend with what is behind it.
/// </summary>
/// <remarks>
/// <para>Vulkan decides that per swapchain, through the composite-alpha modes the surface offers, and what a surface
/// offers depends on the device as much as on the window. Measured on Windows: the native NVIDIA, Intel and Adreno
/// drivers offer <c>OPAQUE</c> only (Intel adds <c>INHERIT</c>) for every kind of window, layered and
/// no-redirection-bitmap included. Mesa's Dozen, Vulkan over Direct3D 12 as Microsoft's "OpenCL, OpenGL, and Vulkan
/// Compatibility Pack" installs it, offers <c>PRE_MULTIPLIED</c> and means it: its Win32 presentation goes through
/// DirectComposition with <c>DXGI_ALPHA_MODE_PREMULTIPLIED</c>, and a screen capture over a known backdrop matched
/// the premultiplied prediction to within a fraction of one 8-bit step, on NVIDIA, Intel, WARP and the Adreno.</para>
/// <para>So the native device is asked first and kept whenever it can do it, which Wayland, macOS and Android
/// drivers usually can. A second device is made only when it cannot: the one on the SAME GPU, matched by adapter
/// LUID (Dozen reports the LUID of the D3D12 adapter it runs on, which is the native driver's), whose surface offers
/// <c>PRE_MULTIPLIED</c>; failing that, the best other device that does. A translation layer is slower than the
/// native driver and its resources are not the native device's, so it is the exception, never the default.</para>
/// </remarks>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// Whether a swapchain this device makes on <paramref name="surface"/> can carry alpha to the compositor: the
    /// device's queue can present there and the surface offers <c>PRE_MULTIPLIED</c> composite alpha for it.
    /// </summary>
    public bool SupportsPremultipliedAlpha(VkSurfaceKHR surface)
    {
        InstanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(PhysicalDevice, GraphicsQueueFamily, surface, out var presentable);
        return presentable && OffersPremultipliedAlpha(InstanceApi, PhysicalDevice, surface);
    }

    /// <summary>
    /// Creates a device for a transparent window's surface: the device <see cref="Create"/> would pick when its
    /// surface offers premultiplied alpha; otherwise a device on the same GPU (or failing that, any other) whose
    /// surface does; otherwise <see cref="Create"/>'s pick after all, on which the window's swapchain is opaque (see
    /// <see cref="VulkanContext.PresentsAlpha"/>).
    /// </summary>
    public static VulkanDevice CreateForTransparentSurface(VkInstance instance, VkSurfaceKHR surface,
        VkSampleCountFlags msaaSamples = VkSampleCountFlags.Count1, bool ownsInstance = true)
    {
        var instanceApi = GetApi(instance);
        var native = PickPhysicalDevice(instanceApi, surface, out var nativeFamily, log: false);
        var count = DeviceCount(instanceApi);
        if (OffersPremultipliedAlpha(instanceApi, native, surface))
        {
            LogSelectedDevice(instanceApi, native, nativeFamily, count);
            return CreateOn(instance, instanceApi, native, nativeFamily, surface, msaaSamples, ownsInstance);
        }

        if (TryPickForPremultipliedAlpha(instanceApi, surface, native, out var alpha, out var alphaFamily))
        {
            SdlVulkanLog.Logger.TransparentWindowDevice(DeviceName(instanceApi, native), DeviceName(instanceApi, alpha));
            LogSelectedDevice(instanceApi, alpha, alphaFamily, count);
            return CreateOn(instance, instanceApi, alpha, alphaFamily, surface, msaaSamples, ownsInstance);
        }

        SdlVulkanLog.Logger.TransparentWindowOpaque(DeviceName(instanceApi, native));
        LogSelectedDevice(instanceApi, native, nativeFamily, count);
        return CreateOn(instance, instanceApi, native, nativeFamily, surface, msaaSamples, ownsInstance);
    }

    /// <summary>
    /// The extra device <see cref="SdlVulkanApp"/> gives its transparent windows when the shared device offers no
    /// premultiplied alpha on <paramref name="surface"/>, preferring the GPU of <paramref name="sameGpuAs"/> (the
    /// shared device's); null when no other device offers it either.
    /// </summary>
    internal static VulkanDevice? TryCreateForPremultipliedAlpha(VkInstance instance, VkSurfaceKHR surface,
        VkPhysicalDevice sameGpuAs, VkSampleCountFlags msaaSamples, bool ownsInstance)
    {
        var instanceApi = GetApi(instance);
        if (!TryPickForPremultipliedAlpha(instanceApi, surface, sameGpuAs, out var alpha, out var family))
        {
            SdlVulkanLog.Logger.TransparentWindowOpaque(DeviceName(instanceApi, sameGpuAs));
            return null;
        }
        SdlVulkanLog.Logger.TransparentWindowDevice(DeviceName(instanceApi, sameGpuAs), DeviceName(instanceApi, alpha));
        LogSelectedDevice(instanceApi, alpha, family, DeviceCount(instanceApi));
        return CreateOn(instance, instanceApi, alpha, family, surface, msaaSamples, ownsInstance);
    }

    /// <summary>One physical device as the transparent-window picker sees it.</summary>
    /// <param name="CanPresent">It has a graphics queue that can present to the window's surface.</param>
    /// <param name="OffersPremultiplied">The surface offers <c>PRE_MULTIPLIED</c> composite alpha for it.</param>
    /// <param name="SameGpu">Its adapter LUID is the native device's.</param>
    /// <param name="Type">Its device type.</param>
    internal readonly record struct AlphaCandidate(bool CanPresent, bool OffersPremultiplied, bool SameGpu,
        VkPhysicalDeviceType Type);

    /// <summary>
    /// Which candidate a transparent window should use when the native device offers no premultiplied alpha, or -1
    /// for none. The same GPU first, since a device on another adapter would move the window's work there too; then
    /// discrete, integrated, virtual, other, CPU; the loader's enumeration order breaks ties.
    /// </summary>
    internal static int PickAlphaCandidate(ReadOnlySpan<AlphaCandidate> candidates)
    {
        var best = -1;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (!candidates[i].CanPresent || !candidates[i].OffersPremultiplied)
                continue;
            if (best < 0 || Rank(candidates[i]) < Rank(candidates[best]))
                best = i;
        }
        return best;

        static int Rank(in AlphaCandidate c) => (c.SameGpu ? 0 : 10) + c.Type switch
        {
            VkPhysicalDeviceType.DiscreteGpu => 0,
            VkPhysicalDeviceType.IntegratedGpu => 1,
            VkPhysicalDeviceType.VirtualGpu => 2,
            VkPhysicalDeviceType.Cpu => 4,
            _ => 3,
        };
    }

    private static bool TryPickForPremultipliedAlpha(VkInstanceApi instanceApi, VkSurfaceKHR surface,
        VkPhysicalDevice sameGpuAs, out VkPhysicalDevice device, out uint queueFamily)
    {
        uint count = 0;
        instanceApi.vkEnumeratePhysicalDevices(&count, null);
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* pDevices = devices)
            instanceApi.vkEnumeratePhysicalDevices(&count, pDevices);

        var luid = AdapterLuid(instanceApi, sameGpuAs);
        var candidates = new AlphaCandidate[count];
        var families = new uint[count];
        for (var i = 0; i < count; i++)
        {
            // The native device has already said no; asking it again would only pick it back.
            if (devices[i].Handle == sameGpuAs.Handle)
                continue;
            instanceApi.vkGetPhysicalDeviceProperties(devices[i], out var props);
            var canPresent = TryFindGraphicsQueue(instanceApi, devices[i], surface, out families[i]);
            candidates[i] = new AlphaCandidate(
                canPresent,
                // Surface capabilities may only be queried for a device that can present to the surface.
                canPresent && OffersPremultipliedAlpha(instanceApi, devices[i], surface),
                luid is { } native && AdapterLuid(instanceApi, devices[i]) == native,
                props.deviceType);
        }

        var pick = PickAlphaCandidate(candidates);
        device = pick >= 0 ? devices[pick] : VkPhysicalDevice.Null;
        queueFamily = pick >= 0 ? families[pick] : 0;
        return pick >= 0;
    }

    private static bool OffersPremultipliedAlpha(VkInstanceApi instanceApi, VkPhysicalDevice device, VkSurfaceKHR surface)
        => instanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(device, surface, out var caps) == VkResult.Success
           && (caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.PreMultiplied) != 0;

    // The adapter LUID (core 1.1, VkPhysicalDeviceIDProperties), or null where the device does not report one: a 1.0
    // device, whose pNext chain would be invalid usage, or a platform without LUIDs (they are a Windows notion).
    private static ulong? AdapterLuid(VkInstanceApi instanceApi, VkPhysicalDevice device)
    {
        instanceApi.vkGetPhysicalDeviceProperties(device, out var basic);
        if (new VkVersion(basic.apiVersion) < VkVersion.Version_1_1)
            return null;
        VkPhysicalDeviceIDProperties id = new();
        VkPhysicalDeviceProperties2 props = new() { pNext = &id };
        instanceApi.vkGetPhysicalDeviceProperties2(device, &props);
        if (!id.deviceLUIDValid)
            return null;
        ulong value = 0;
        for (var i = 7; i >= 0; i--)
            value = (value << 8) | id.deviceLUID[i];
        return value;
    }

    private static uint DeviceCount(VkInstanceApi instanceApi)
    {
        uint count = 0;
        instanceApi.vkEnumeratePhysicalDevices(&count, null);
        return count;
    }

    private static string DeviceName(VkInstanceApi instanceApi, VkPhysicalDevice device)
    {
        instanceApi.vkGetPhysicalDeviceProperties(device, out var props);
        return VkStringInterop.ConvertToManaged(props.deviceName) ?? "<unknown>";
    }
}
