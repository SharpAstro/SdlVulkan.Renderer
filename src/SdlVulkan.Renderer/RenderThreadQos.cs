using System;
using System.Runtime.InteropServices;

namespace SdlVulkan.Renderer;

/// <summary>
/// Asks the operating system to run the render thread at full speed when it runs at all.
/// </summary>
/// <remarks>
/// <para>
/// A hybrid processor (Intel's 12th generation and later: an i7-1255U has two performance cores and eight efficiency
/// cores) runs a thread on an efficiency core at about half the speed, and Windows puts a thread there when it judges
/// its work to be in the background: a thread of a window that is not the foreground window is one. Every layer of such
/// a frame took twice as long, and those were the only paints over 8 ms left in a minute of panning the atlas
/// (traced 2026-11-25: 21 of them at an 4.2 ms median; none, at 7 ms at most, with the process kept to the performance
/// cores).
/// </para>
/// <para>
/// Turning execution-speed throttling off for the thread (<c>SetThreadInformation</c>, <c>ThreadPowerThrottling</c>
/// with the control bit set and the state bit clear) is Windows' high quality-of-service opt-out. It costs nothing at
/// rest: the loop waits for events when nothing is owed, and a waiting thread runs on no core. A no-op on every other
/// operating system, and where the call is refused (before Windows 10 1709).
/// </para>
/// </remarks>
internal static partial class RenderThreadQos
{
    private const int ThreadPowerThrottling = 3;
    private const uint ThreadPowerThrottlingCurrentVersion = 1;
    private const uint ThreadPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint thread, int informationClass, ref ThreadPowerThrottlingState information, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    /// <summary>Asks for full speed for the calling thread; whether the request was taken.</summary>
    public static bool RequestForCurrentThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var state = new ThreadPowerThrottlingState
            {
                Version = ThreadPowerThrottlingCurrentVersion,
                ControlMask = ThreadPowerThrottlingExecutionSpeed,
                StateMask = 0,
            };
            return SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref state,
                (uint)Marshal.SizeOf<ThreadPowerThrottlingState>());
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
