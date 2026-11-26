using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// The device's <see cref="VkPipelineCache"/>: every pipeline the renderer builds goes through it, and an app that
/// names <see cref="PipelineCacheDirectory"/> gets it back from disk on the next launch.
/// </summary>
/// <remarks>
/// <para>
/// The shaders ship as SPIR-V, but turning SPIR-V into the GPU's own code is the driver's work, and without a cache it
/// did that again for every pipeline set: 82 to 99 ms for the first set in a fresh process on the Adreno X1-85, and
/// still 71 to 88 ms for each one after it in the same process, so its own cache was not saving it. Seeded from the
/// 330 KB a previous run saved, the first set took 10 to 13 ms (2026-11-26, issue #391 of the PDF viewer). The main
/// set is built on the render thread before a window's first frame, so that is time a launch no longer waits.
/// </para>
/// <para>
/// One cache per device, created without <c>VK_PIPELINE_CACHE_CREATE_EXTERNALLY_SYNCHRONIZED_BIT</c>, so the driver
/// synchronises it and pipeline sets may still be built on pool threads at once (see <see cref="VkRenderer"/>).
/// What is on disk is checked against this device before the driver sees it (<see cref="PipelineCacheHeader"/>), and
/// the file is named after the device and its driver version, so a driver update starts a new file rather than
/// offering the old one; a file that does not match is not used. It is written to a temporary file and renamed, so a
/// second process or a crash mid-write leaves the old file or the new one, never half of one.
/// </para>
/// </remarks>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// Where devices load their pipeline cache from when created and save it to when disposed (or on
    /// <see cref="SavePipelineCache"/>). Null, the default, keeps the cache in memory for the device's life only. Set
    /// it before the first device is created. The cache is also saved in the background as pipeline sets are built, so
    /// a run that does not end by disposing the device keeps what it built.
    /// </summary>
    public static string? PipelineCacheDirectory { get; set; }

    /// <summary>The cache every pipeline on this device is built through.</summary>
    public VkPipelineCache PipelineCache { get; private set; }

    // The file this device loads and saves, null when there is no directory; and how many bytes it started with,
    // so a run that built nothing new does not rewrite the file.
    private string? _pipelineCacheFile;
    private int _pipelineCacheLoadedBytes;

    // Held by a save while it reads the cache and writes the file, and by the destroy, so the cache is never destroyed
    // under a save. A save that finds it taken skips: the one holding it is saving already.
    private readonly Lock _pipelineCacheSave = new();

    private void CreatePipelineCache()
    {
        InstanceApi.vkGetPhysicalDeviceProperties(PhysicalDevice, out var props);
        var uuid = new ReadOnlySpan<byte>(props.pipelineCacheUUID, (int)Vulkan.VK_UUID_SIZE);

        byte[] seed = [];
        if (PipelineCacheDirectory is { Length: > 0 } dir)
        {
            _pipelineCacheFile = Path.Combine(dir,
                $"pipelines-{props.vendorID:x4}-{props.deviceID:x4}-{props.driverVersion:x8}-{Convert.ToHexStringLower(uuid)}.bin");
            try
            {
                if (File.Exists(_pipelineCacheFile))
                {
                    var data = File.ReadAllBytes(_pipelineCacheFile);
                    if (PipelineCacheHeader.Matches(data, props.vendorID, props.deviceID, uuid))
                    {
                        seed = data;
                    }
                    else
                    {
                        SdlVulkanLog.Logger.PipelineCacheRejected(_pipelineCacheFile, data.Length);
                    }
                }
            }
            catch (IOException ex)
            {
                SdlVulkanLog.Logger.PipelineCacheIoFailed("read", _pipelineCacheFile, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                SdlVulkanLog.Logger.PipelineCacheIoFailed("read", _pipelineCacheFile, ex.Message);
            }
        }

        fixed (byte* p = seed)
        {
            VkPipelineCacheCreateInfo ci = new()
            {
                initialDataSize = (nuint)seed.Length,
                pInitialData = seed.Length > 0 ? p : null,
            };
            if (DeviceApi.vkCreatePipelineCache(&ci, null, out var cache) != VkResult.Success)
            {
                // A driver that refuses even an empty cache is not one to fail a window for: build without it.
                ci = new();
                DeviceApi.vkCreatePipelineCache(&ci, null, out cache);
            }

            PipelineCache = cache;
        }

        _pipelineCacheLoadedBytes = seed.Length;
    }

    /// <summary>
    /// Saves the cache in the background after new pipelines went into it (a pipeline set built). Saving only on
    /// dispose lost everything a run built whenever the process did not end by disposing its device: killed, crashed,
    /// or ended from outside, which is how the PDF viewer's start-up timing ends every run, so it measured no cache at
    /// all (2026-11-26). Never blocks the caller (a set is built on the render thread before a window's first frame):
    /// the write is on a pool thread, and a save already under way means this one is not needed.
    /// </summary>
    internal void NotePipelinesBuilt()
    {
        if (_pipelineCacheFile is null) return;
        _ = Task.Run(() =>
        {
            if (!_pipelineCacheSave.TryEnter()) return;
            try
            {
                SavePipelineCacheCore();
            }
            finally
            {
                _pipelineCacheSave.Exit();
            }
        });
    }

    /// <summary>
    /// Writes the cache to <see cref="PipelineCacheDirectory"/>, when one is set and the cache has grown since it was
    /// loaded or last saved. Done on dispose and in the background after each pipeline set is built; an app may call
    /// it as well. Thread-safe against pipeline builds (the driver synchronises the cache) and against other saves.
    /// </summary>
    public void SavePipelineCache()
    {
        lock (_pipelineCacheSave)
        {
            SavePipelineCacheCore();
        }
    }

    private void SavePipelineCacheCore()
    {
        if (_pipelineCacheFile is not { } file || PipelineCache == VkPipelineCache.Null) return;

        nuint size = 0;
        if (DeviceApi.vkGetPipelineCacheData(PipelineCache, &size, null) != VkResult.Success || size == 0) return;
        if ((int)size <= _pipelineCacheLoadedBytes) return;

        var data = new byte[(int)size];
        fixed (byte* d = data)
        {
            if (DeviceApi.vkGetPipelineCacheData(PipelineCache, &size, d) != VkResult.Success) return;
        }

        var temp = $"{file}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(temp, data.AsSpan(0, (int)size));
            File.Move(temp, file, overwrite: true);
            _pipelineCacheLoadedBytes = (int)size;
        }
        catch (IOException ex)
        {
            SdlVulkanLog.Logger.PipelineCacheIoFailed("write", file, ex.Message);
            TryDelete(temp);
        }
        catch (UnauthorizedAccessException ex)
        {
            SdlVulkanLog.Logger.PipelineCacheIoFailed("write", file, ex.Message);
            TryDelete(temp);
        }

        static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Left behind: a temporary file in the app's own cache folder, replaced by the next save's.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }
    }

    private void DestroyPipelineCache()
    {
        // Waits out a background save, which reads the cache; it is file I/O of a few hundred KB, bounded.
        lock (_pipelineCacheSave)
        {
            if (PipelineCache == VkPipelineCache.Null) return;
            if (!IsGpuStuck) SavePipelineCacheCore();
            DeviceApi.vkDestroyPipelineCache(PipelineCache);
            PipelineCache = VkPipelineCache.Null;
        }
    }
}

/// <summary>
/// Whether saved pipeline-cache data was made by this device: the version-one header Vulkan puts at the front of every
/// cache's data (the header's length, its version, the vendor and device IDs and the cache UUID). Checked before the
/// data reaches the driver, which is required to ignore data that is not its own but is not trusted to.
/// </summary>
internal static class PipelineCacheHeader
{
    /// <summary>The version-one header: four 32-bit words and the 16-byte UUID.</summary>
    internal const int Length = 16 + 16;

    internal static bool Matches(ReadOnlySpan<byte> data, uint vendorId, uint deviceId, ReadOnlySpan<byte> uuid)
    {
        if (data.Length < Length || uuid.Length != 16) return false;

        var headerLength = BitConverter.ToUInt32(data[..4]);
        var version = BitConverter.ToUInt32(data[4..8]);
        return headerLength >= Length && headerLength <= data.Length
            && version == (uint)VkPipelineCacheHeaderVersion.One
            && BitConverter.ToUInt32(data[8..12]) == vendorId
            && BitConverter.ToUInt32(data[12..16]) == deviceId
            && data[16..32].SequenceEqual(uuid);
    }
}
