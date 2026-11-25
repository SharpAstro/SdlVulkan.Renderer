using System.Runtime.InteropServices;
using System.Text;

namespace SdlVulkan.Renderer;

/// <summary>
/// What the first line of an iOS app's log says about the app and the device, read without UIKit (this assembly is
/// plain <c>net10.0</c>, see <see cref="SdlVulkanIosApp"/>): the bundle's version from CoreFoundation, the model and
/// the OS version from <c>sysctl</c>. The libraries are named by absolute path, as Apple's own .NET bindings name
/// them. Every answer is null when it cannot be read, and nothing here throws: on a desktop the libraries are simply
/// absent.
/// </summary>
internal static unsafe class AppleAppInfo
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const uint CFStringEncodingUtf8 = 0x08000100;

    /// <summary>
    /// The header line: "Chess 1.0 (1) on iPad15,3 iPadOS 26.0.1, started 2026-11-25T11:00:00.0000000+11:00". A part
    /// that cannot be read says so rather than leaving the line out.
    /// </summary>
    internal static string Describe(string title, DateTimeOffset started)
    {
        var version = InfoString("CFBundleShortVersionString");
        var build = InfoString("CFBundleVersion");
        var versionText = version is null ? "(version unknown)" : build is null || build == version ? version : $"{version} ({build})";
        var machine = Sysctl("hw.machine");
        var os = machine is not null && machine.StartsWith("iPad", StringComparison.Ordinal) ? "iPadOS" : "iOS";
        var osVersion = Sysctl("kern.osproductversion") ?? Environment.OSVersion.Version.ToString();
        return $"{title} {versionText} on {machine ?? "an unknown device"} {os} {osVersion}, started {started:O}";
    }

    /// <summary>A string value from the main bundle's Info.plist, or null.</summary>
    internal static string? InfoString(string key)
    {
        try
        {
            var bundle = CFBundleGetMainBundle();
            if (bundle == 0)
                return null;
            var keyBytes = Encoding.UTF8.GetBytes(key + "\0");
            nint cfKey;
            fixed (byte* k = keyBytes)
                cfKey = CFStringCreateWithCString(0, k, CFStringEncodingUtf8);
            if (cfKey == 0)
                return null;
            try
            {
                // Get rule: the value belongs to the bundle and is not released here.
                var value = CFBundleGetValueForInfoDictionaryKey(bundle, cfKey);
                if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID())
                    return null;
                var buffer = stackalloc byte[256];
                return CFStringGetCString(value, buffer, 256, CFStringEncodingUtf8) != 0
                    ? Marshal.PtrToStringUTF8((nint)buffer)
                    : null;
            }
            finally
            {
                CFRelease(cfKey);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A string-valued <c>sysctl</c> ("hw.machine" is the model, "iPad15,3"), or null.</summary>
    internal static string? Sysctl(string name)
    {
        try
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* n = nameBytes)
            {
                nuint length = 0;
                if (SysctlByName(n, null, &length, null, 0) != 0 || length == 0 || length > 4096)
                    return null;
                var buffer = stackalloc byte[(int)length];
                if (SysctlByName(n, buffer, &length, null, 0) != 0)
                    return null;
                var text = Encoding.UTF8.GetString(buffer, (int)length).TrimEnd('\0');
                return text.Length > 0 ? text : null;
            }
        }
        catch
        {
            return null;
        }
    }

    // Blittable signatures only, so no marshalling stub is needed under Mono's ahead-of-time compiler.
    [DllImport(CoreFoundation)]
    private static extern nint CFBundleGetMainBundle();

    [DllImport(CoreFoundation)]
    private static extern nint CFBundleGetValueForInfoDictionaryKey(nint bundle, nint key);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, byte* cString, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nuint CFGetTypeID(nint cf);

    [DllImport(CoreFoundation)]
    private static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern byte CFStringGetCString(nint theString, byte* buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    [DllImport(LibSystem, EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(byte* name, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);
}
