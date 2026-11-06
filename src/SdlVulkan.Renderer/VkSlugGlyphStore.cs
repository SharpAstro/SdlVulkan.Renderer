using System.Collections.Concurrent;
using DIR.Lib;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// The GPU half of Slug text (see DIR.Lib's <see cref="SlugGlyphBlob"/> for the layout, <see cref="SlugGlyphEncoder"/>
/// for the CPU half): ONE growable device-local storage buffer of 32-bit words holding every glyph's blob back to
/// back, keyed by glyph identity (font path + <see cref="GlyphIdentity"/>, the key <see cref="VkSdfFontAtlas"/> uses
/// for its cells). A draw binds the one set (<see cref="DescriptorSet"/>) and addresses a glyph by the word offset
/// <see cref="TryGet"/> returns, so the text of a frame needs no per-page or per-glyph rebinding: one draw per
/// colour is possible, which the atlas-page split of the MTSDF path does not allow.
/// </summary>
/// <remarks>
/// <para><b>Never blocks.</b> <see cref="TryGet"/> on a glyph not seen yet returns false and queues the encode on
/// worker threads (one <see cref="SlugGlyphEncoder"/> per worker, which is not thread-safe; the shared
/// <see cref="ManagedFontRasterizer"/> is, as the SDF atlas's parallel rasterization already relies on). The
/// caller skips that glyph, draws the rest of its frame, and asks for another frame while
/// <see cref="HasPending"/>: a miss costs the frame nothing but the glyph itself.</para>
/// <para><b>Deferred upload.</b> <see cref="RecordUploads"/> runs in every frame's command buffer before the render
/// pass (like the atlases' <c>Flush</c>): it drains finished encodes (bounded per frame), gives each blob its word
/// offset, writes them into a staging buffer and records one copy into the device buffer, then a buffer barrier
/// from transfer write to vertex and fragment shader read. Offsets are assigned in the order of the drain and
/// never change.</para>
/// <para><b>Growth without a stall.</b> A full buffer is replaced by one twice the size (up to the device's
/// <c>maxStorageBufferRange</c>): the old words are copied across in the same command buffer, a NEW descriptor set
/// is written for the new buffer (a set an in-flight frame may hold is never updated), and the old buffer, memory
/// and set go to <see cref="VulkanContext.DeferDestroy(VkImageView, VkImage, VkDeviceMemory, VkBuffer, VkDescriptorSet)"/>.
/// Nothing here waits for the device or the queue.</para>
/// <para><b>Dropped frames.</b> Work recorded into a frame is provisional until it reaches the queue
/// (<see cref="VulkanContext.OnFrameDropped"/>): the staging buffers and the buffers a growth copied from are
/// held until the next frame confirms the previous one was not dropped, and are recorded again if it was.</para>
/// <para>Render thread only, apart from the workers' own queues.</para>
/// </remarks>
internal sealed unsafe class VkSlugGlyphStore : IDisposable
{
    /// <summary>The words a store starts with (256 KiB): a few hundred Latin glyphs.</summary>
    public const int DefaultInitialCapacityWords = 64 * 1024;

    /// <summary>Most words uploaded in one frame (256 KiB), so a cold page of CJK spreads its upload over frames
    /// instead of one long copy. At least one glyph is always taken, however large.</summary>
    public const int DefaultUploadBudgetWords = 64 * 1024;

    /// <summary>Glyph identity: the face and the glyph in it.</summary>
    public readonly record struct GlyphKey(string FontPath, GlyphIdentity Identity);

    /// <summary>Where a glyph is in the buffer and how big it is.</summary>
    /// <param name="WordOffset">Word offset of the glyph's blob in the buffer; the shader adds the blob's own
    /// offsets to it. Meaningless when <paramref name="IsEmpty"/>.</param>
    /// <param name="UnitsPerEm">Units per em of the face the glyph came from.</param>
    /// <param name="IsEmpty">Draws nothing: a glyph with no outline (a space), one the face cannot draw, or one
    /// too large for the buffer even at its ceiling.</param>
    public readonly record struct Entry(
        uint WordOffset, float MinXEm, float MinYEm, float MaxXEm, float MaxYEm, int UnitsPerEm, bool IsEmpty)
    {
        public static Entry Nothing => new(0, 0, 0, 0, 0, 0, true);
    }

    private readonly VulkanContext _ctx;
    private readonly VulkanDevice _device;
    private readonly ManagedFontRasterizer _rasterizer;
    private readonly int _maxWords;
    private readonly int _uploadBudgetWords;
    private readonly TimeSpan _joinTimeout;
    private readonly Action<GlyphKey>? _beforeEncode;
    private bool _disposed;

    // ---- the buffer ----
    private VkBuffer _buffer;
    private VkDeviceMemory _memory;
    private VkDescriptorSet _set;
    private int _capacityWords;
    // Words handed out to blobs, uploaded or not yet: the next blob goes at this offset.
    private int _usedWords;

    // ---- lookup (render thread) ----
    private readonly Dictionary<GlyphKey, Entry> _entries = new();
    // Asked for and not yet drained into _entries (queued, being encoded, or encoded and waiting for a frame).
    private readonly HashSet<GlyphKey> _requested = new();

    // ---- encoding (workers) ----
    private readonly BlockingCollection<GlyphKey> _queue = new();
    private readonly ConcurrentQueue<Encoded> _results = new();
    private readonly List<Thread> _workers = new();
    private readonly int _workerCount;
    private int _encodesInFlight;

    private readonly record struct Encoded(GlyphKey Key, SlugGlyphBlob Blob, bool Ok);

    // ---- upload bookkeeping ----
    // What one frame's command buffer carries. Kept until the NEXT frame shows this one reached the queue.
    private sealed class FrameWork
    {
        public long Ordinal = -1;
        public bool Dropped;
        public readonly List<Batch> Batches = new();
        public readonly List<Growth> Growths = new();
    }

    // Blobs written to one staging buffer, to land at dstWord.
    private sealed class Batch
    {
        public Staging Staging = null!;
        public int DstWord;
        public int Words;
    }

    // A host-visible buffer the words are written into before the copy. Mapped for good (coherent memory), and kept
    // in _freeStaging once its frame has retired instead of being destroyed: a streaming upload would otherwise
    // allocate and free device memory every frame it carries anything.
    private sealed class Staging
    {
        public VkBuffer Buffer;
        public VkDeviceMemory Memory;
        public void* Mapped;
        public ulong CapacityBytes;
    }

    /// <summary>Smallest staging buffer made (64 KiB); bigger ones are rounded up to a power of two, so the few that
    /// exist fit most uploads.</summary>
    private const ulong MinStagingBytes = 64 * 1024;

    /// <summary>Most retired staging buffers kept for the next upload; further ones are destroyed.</summary>
    private const int MaxFreeStaging = 4;

    private readonly List<Staging> _freeStaging = new();

    /// <summary>Staging buffers created over the store's life: flat once uploads are steady (they are reused), where a
    /// buffer per upload would count one per frame that uploaded. For tests.</summary>
    internal int StagingBuffersCreated { get; private set; }

    // A buffer replaced by a bigger one: its first copyWords words were copied across.
    private sealed class Growth
    {
        public VkBuffer OldBuffer;
        public VkDeviceMemory OldMemory;
        public VkDescriptorSet OldSet;
        public VkBuffer NewBuffer;
        public int CopyWords;
    }

    private FrameWork? _last;

    /// <param name="initialCapacityWords">Starting size of the buffer in 32-bit words; doubled on growth. Small values
    /// exist for tests that outgrow it.</param>
    /// <param name="uploadBudgetWords">Most words drained into one frame (at least one glyph always is).</param>
    /// <param name="workerCount">Threads encoding misses, each with its own encoder.</param>
    /// <param name="joinTimeout">How long <see cref="Dispose"/> waits for each encoder thread to finish the glyph it is
    /// on (default 5 s).</param>
    /// <param name="beforeEncode">Called on the worker just before each glyph is encoded. For tests that hold an
    /// encode in flight.</param>
    internal VkSlugGlyphStore(VulkanContext ctx, ManagedFontRasterizer rasterizer,
        int initialCapacityWords = DefaultInitialCapacityWords,
        int uploadBudgetWords = DefaultUploadBudgetWords, int workerCount = 2,
        TimeSpan? joinTimeout = null, Action<GlyphKey>? beforeEncode = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(rasterizer);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacityWords, SlugGlyphBlob.HeaderWords);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _ctx = ctx;
        _device = ctx.GraphicsDevice;
        _rasterizer = rasterizer;
        _workerCount = workerCount;
        _uploadBudgetWords = Math.Max(1, uploadBudgetWords);
        _joinTimeout = joinTimeout ?? TimeSpan.FromSeconds(5);
        _beforeEncode = beforeEncode;
        // The ceiling: what one storage-buffer descriptor can address, and what a 32-bit word index can.
        _maxWords = (int)Math.Min(_device.MaxStorageBufferRange / sizeof(uint), (uint)int.MaxValue / sizeof(uint));
        _capacityWords = Math.Min(initialCapacityWords, _maxWords);
        (_buffer, _memory) = CreateBuffer(_capacityWords);
        _set = _device.AllocateSlugDescriptorSet();
        _device.UpdateSlugDescriptorSet(_set, _buffer, (ulong)_capacityWords * sizeof(uint));
    }

    // ---- surface for the draw path ----

    /// <summary>The storage buffer the glyph words are in. Changes when the store grows, and only inside
    /// <see cref="RecordUploads"/>: read it after that, every frame.</summary>
    public VkBuffer Buffer => _buffer;

    /// <summary>The set binding <see cref="Buffer"/> (<see cref="VulkanDevice.SlugDescriptorSetLayout"/>). A new
    /// one after a growth; bind this frame's, never a remembered one.</summary>
    public VkDescriptorSet DescriptorSet => _set;

    public int CapacityWords => _capacityWords;
    public int UsedWords => _usedWords;

    /// <summary>The most words the buffer can hold on this device: <c>maxStorageBufferRange</c> / 4.</summary>
    public int MaxWords => _maxWords;

    /// <summary>How many times the buffer has been replaced by a bigger one.</summary>
    public int GrowCount { get; private set; }

    /// <summary>Glyphs the face could not draw, or that did not fit even at <see cref="MaxWords"/>: they answer
    /// as empty so a draw skips them instead of asking again every frame.</summary>
    public int FailedGlyphs { get; private set; }

    /// <summary>Glyphs with an answer (including empty ones).</summary>
    public int GlyphCount => _entries.Count;

    /// <summary>Encodes queued or running on the workers, not yet handed to the render thread. For tests.</summary>
    internal int EncodesInFlight => Volatile.Read(ref _encodesInFlight);

    /// <summary>
    /// True while a glyph asked for is not yet in the buffer, or a dropped frame's upload is waiting to be recorded
    /// again: the caller wants another frame. Same role as the SDF atlas's dirty flag.
    /// </summary>
    public bool HasPending => _requested.Count > 0 || _last is { Dropped: true };

    /// <summary>
    /// The glyph's place in the buffer. True when it is known (an empty glyph, a space, is known and draws
    /// nothing). False for a glyph not seen before, which is queued for encoding and answers true after a
    /// frame's <see cref="RecordUploads"/> has taken its blob; false again while it waits. Never blocks.
    /// </summary>
    public bool TryGet(string fontPath, GlyphIdentity identity, out Entry entry)
    {
        var key = new GlyphKey(fontPath, identity);
        if (_entries.TryGetValue(key, out entry)) return true;
        entry = default;
        if (_disposed || !_requested.Add(key)) return false;

        StartWorkers();
        Interlocked.Increment(ref _encodesInFlight);
        _queue.Add(key);
        return false;
    }

    /// <summary>Forgets glyphs that failed (a face not registered yet, say), so asking for them again retries.</summary>
    public void ForgetFailures()
    {
        // Failed entries are the empty ones with no em; a real space keeps its units per em.
        List<GlyphKey>? failed = null;
        foreach (var (key, entry) in _entries)
            if (entry.IsEmpty && entry.UnitsPerEm == 0) (failed ??= new()).Add(key);
        if (failed is null) return;
        foreach (var key in failed) _entries.Remove(key);
        FailedGlyphs = 0;
    }

    // ---- encoding ----

    private void StartWorkers()
    {
        if (_workers.Count > 0) return;
        for (var i = 0; i < _workerCount; i++)
        {
            // Dedicated background threads: they block on the queue between misses, which a pool thread should not.
            var worker = new Thread(WorkerLoop) { IsBackground = true, Name = $"SlugGlyphEncoder {i}" };
            _workers.Add(worker);
            worker.Start();
        }
    }

    private void WorkerLoop()
    {
        // One encoder per worker: it keeps scratch between glyphs and is not thread-safe.
        var encoder = new SlugGlyphEncoder();
        try
        {
            foreach (var key in _queue.GetConsumingEnumerable())
            {
                SlugGlyphBlob blob = default;
                var ok = false;
                try
                {
                    _beforeEncode?.Invoke(key);
                    ok = encoder.TryEncode(_rasterizer, key.FontPath, key.Identity, out blob);
                }
                catch (Exception)
                {
                    // A face that throws (unreadable file, a disposed rasterizer) is a glyph that cannot be drawn.
                }
                _results.Enqueue(new Encoded(key, blob, ok));
                Interlocked.Decrement(ref _encodesInFlight);
            }
        }
        catch (ObjectDisposedException)
        {
            // Dispose gave up waiting for this thread (an encode outlasted its join) and the queue went with it. Nothing
            // waits for the thread any more: it ends quietly rather than take the process down on an unhandled throw.
        }
    }

    // ---- upload ----

    /// <summary>
    /// Records this frame's upload into <paramref name="cmd"/>, before the render pass: the previous frame's
    /// provisional work settled, finished encodes drained into the buffer (growing it first if they do not fit), and
    /// the barrier that makes the words visible to the shaders. Call once per frame, after the frame has begun.
    /// </summary>
    public void RecordUploads(VkCommandBuffer cmd)
    {
        if (_disposed) return;

        var ordinal = _ctx.FramesBegun;
        FrameWork? work = null;
        if (_last is { } last)
        {
            if (last.Ordinal == ordinal)
            {
                // The same frame asking twice: add to what it carries.
                work = last;
            }
            else if (last.Dropped)
            {
                // It never reached the queue: everything it carried, growths included, is recorded again, first.
                last.Dropped = false;
                work = last;
                RecordGrowths(cmd, work.Growths);
                RecordBatches(cmd, work.Batches);
            }
            else
            {
                Release(last);
                _last = null;
            }
        }

        var drained = DrainResults();
        if (drained.Count > 0)
        {
            work ??= new FrameWork();
            Append(cmd, work, drained);
        }

        if (work is null) return;

        if (work.Ordinal != ordinal)
        {
            // A new frame carries this work (a fresh one, or a dropped frame's again): it is provisional
            // until that frame reaches the queue.
            work.Ordinal = ordinal;
            work.Dropped = false;
            _last = work;
            var carried = work;
            _ctx.OnFrameDropped(cmd, () => carried.Dropped = true);
        }
        ShaderReadBarrier(cmd);
    }

    // Takes finished encodes up to the frame's budget, files each under its key, and returns the blobs that
    // need words in the buffer (empty and failed glyphs need none).
    private List<SlugGlyphBlob> DrainResults()
    {
        var blobs = new List<SlugGlyphBlob>();
        var words = 0;
        while (words < _uploadBudgetWords && _results.TryDequeue(out var r))
        {
            _requested.Remove(r.Key);
            if (!r.Ok)
            {
                _entries[r.Key] = Entry.Nothing;
                FailedGlyphs++;
                continue;
            }

            var blob = r.Blob;
            if (blob.IsEmpty)
            {
                _entries[r.Key] = new Entry(0, 0, 0, 0, 0, blob.UnitsPerEm, true);
                continue;
            }

            var length = blob.Words.Length;
            if (length > _maxWords - _usedWords)
            {
                // Not even the ceiling holds it. Answer as empty rather than ask every frame.
                _entries[r.Key] = Entry.Nothing;
                FailedGlyphs++;
                continue;
            }

            // The offset is fixed here and for good; a growth keeps every offset.
            _entries[r.Key] = new Entry((uint)_usedWords, blob.MinXEm, blob.MinYEm, blob.MaxXEm, blob.MaxYEm,
                blob.UnitsPerEm, false);
            _usedWords += length;
            words += length;
            blobs.Add(blob);
        }
        return blobs;
    }

    // Grows if the blobs do not fit, then writes them. _usedWords already counts them.
    private void Append(VkCommandBuffer cmd, FrameWork work, List<SlugGlyphBlob> blobs)
    {
        var total = 0;
        foreach (var b in blobs) total += b.Words.Length;
        var firstWord = _usedWords - total;

        if (_usedWords > _capacityWords)
            Grow(cmd, work, copyWords: firstWord, neededWords: _usedWords);

        var batch = CreateBatch(blobs, total, firstWord);
        work.Batches.Add(batch);
        RecordBatches(cmd, [batch]);
    }

    private void Grow(VkCommandBuffer cmd, FrameWork work, int copyWords, int neededWords)
    {
        var newCapacity = _capacityWords;
        while (newCapacity < neededWords)
            newCapacity = (int)Math.Min((long)newCapacity * 2, _maxWords);

        var (buffer, memory) = CreateBuffer(newCapacity);
        var set = _device.AllocateSlugDescriptorSet();
        _device.UpdateSlugDescriptorSet(set, buffer, (ulong)newCapacity * sizeof(uint));

        var growth = new Growth
        {
            OldBuffer = _buffer, OldMemory = _memory, OldSet = _set, NewBuffer = buffer, CopyWords = copyWords
        };
        work.Growths.Add(growth);
        _buffer = buffer;
        _memory = memory;
        _set = set;
        _capacityWords = newCapacity;
        GrowCount++;
        RecordGrowths(cmd, [growth]);
    }

    private void RecordGrowths(VkCommandBuffer cmd, List<Growth> growths)
    {
        var api = _ctx.DeviceApi;
        foreach (var g in growths)
        {
            if (g.CopyWords <= 0) continue;
            // The words being copied were written by earlier frames' transfers (or earlier in this command buffer).
            TransferBarrier(cmd, g.OldBuffer);
            VkBufferCopy region = new() { srcOffset = 0, dstOffset = 0, size = (ulong)g.CopyWords * sizeof(uint) };
            api.vkCmdCopyBuffer(cmd, g.OldBuffer, g.NewBuffer, 1, &region);
        }
    }

    private void RecordBatches(VkCommandBuffer cmd, List<Batch> batches)
    {
        var api = _ctx.DeviceApi;
        foreach (var b in batches)
        {
            VkBufferCopy region = new()
            {
                srcOffset = 0, dstOffset = (ulong)b.DstWord * sizeof(uint), size = (ulong)b.Words * sizeof(uint)
            };
            api.vkCmdCopyBuffer(cmd, b.Staging.Buffer, _buffer, 1, &region);
        }
    }

    // Transfer write -> transfer read on one buffer, before it is copied from.
    private void TransferBarrier(VkCommandBuffer cmd, VkBuffer buffer)
    {
        VkBufferMemoryBarrier barrier = new()
        {
            srcAccessMask = VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.TransferRead,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            buffer = buffer,
            offset = 0,
            size = VK_WHOLE_SIZE
        };
        _ctx.DeviceApi.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Transfer, 0,
            0, null, 1, &barrier, 0, null);
    }

    // The copies above made visible to the vertex and fragment stages that read the words.
    private void ShaderReadBarrier(VkCommandBuffer cmd)
    {
        VkBufferMemoryBarrier barrier = new()
        {
            srcAccessMask = VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.ShaderRead,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            buffer = _buffer,
            offset = 0,
            size = VK_WHOLE_SIZE
        };
        _ctx.DeviceApi.vkCmdPipelineBarrier(cmd, VkPipelineStageFlags.Transfer,
            VkPipelineStageFlags.VertexShader | VkPipelineStageFlags.FragmentShader, 0,
            0, null, 1, &barrier, 0, null);
    }

    // ---- resources ----

    private (VkBuffer, VkDeviceMemory) CreateBuffer(int words)
    {
        var api = _ctx.DeviceApi;
        var size = (ulong)words * sizeof(uint);
        VkBufferCreateInfo bufCI = new()
        {
            size = size,
            usage = VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out var buffer).CheckResult();
        _device.NoteBufferCreated();

        api.vkGetBufferMemoryRequirements(buffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal)
        };
        api.vkAllocateMemory(&allocInfo, null, out var memory).CheckResult();
        api.vkBindBufferMemory(buffer, memory, 0);
        return (buffer, memory);
    }

    private Batch CreateBatch(List<SlugGlyphBlob> blobs, int words, int dstWord)
    {
        var staging = AcquireStaging((ulong)words * sizeof(uint));
        var dst = new Span<int>(staging.Mapped, words);
        var at = 0;
        foreach (var blob in blobs)
        {
            blob.Words.CopyTo(dst[at..]);
            at += blob.Words.Length;
        }
        return new Batch { Staging = staging, DstWord = dstWord, Words = words };
    }

    // The smallest retired buffer that holds the upload, else a new one.
    private Staging AcquireStaging(ulong bytes)
    {
        var best = -1;
        for (var i = 0; i < _freeStaging.Count; i++)
            if (_freeStaging[i].CapacityBytes >= bytes
                && (best < 0 || _freeStaging[i].CapacityBytes < _freeStaging[best].CapacityBytes))
                best = i;
        if (best >= 0)
        {
            var reused = _freeStaging[best];
            _freeStaging.RemoveAt(best);
            return reused;
        }

        var capacity = MinStagingBytes;
        while (capacity < bytes) capacity *= 2;

        var api = _ctx.DeviceApi;
        VkBufferCreateInfo bufCI = new()
        {
            size = capacity,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&bufCI, null, out var buffer).CheckResult();
        _device.NoteBufferCreated();

        api.vkGetBufferMemoryRequirements(buffer, out var memReqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = memReqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(memReqs.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&allocInfo, null, out var memory).CheckResult();
        api.vkBindBufferMemory(buffer, memory, 0);

        void* mapped;
        api.vkMapMemory(memory, 0, capacity, 0, &mapped).CheckResult();
        StagingBuffersCreated++;
        return new Staging { Buffer = buffer, Memory = memory, Mapped = mapped, CapacityBytes = capacity };
    }

    // The frame that carried this work reached the queue: nothing can need it recorded again. Deferred, not
    // destroyed: "submitted" is not "finished", and the old buffer and set may be bound by frames still running.
    // A staging buffer is not destroyed once its frame has retired but handed back for the next upload.
    private void Release(FrameWork work)
    {
        foreach (var b in work.Batches)
        {
            var staging = b.Staging;
            _ctx.DeferDestroy(() => Recycle(staging));
        }
        foreach (var g in work.Growths)
        {
            _ctx.DeferDestroy(buffer: g.OldBuffer, memory: g.OldMemory);
            var set = g.OldSet;
            var device = _device;
            _ctx.DeferDestroy(() =>
            {
                device.FreeSlugDescriptorSet(set);
                device.NoteBufferDestroyed();
            });
        }
        work.Batches.Clear();
        work.Growths.Clear();
    }

    // A retired staging buffer goes back to the pool, or is destroyed when the pool is full or the store is gone.
    private void Recycle(Staging staging)
    {
        if (_disposed || _freeStaging.Count >= MaxFreeStaging)
        {
            DestroyStaging(staging);
            return;
        }
        _freeStaging.Add(staging);
    }

    private void DestroyStaging(Staging staging)
    {
        var api = _ctx.DeviceApi;
        api.vkDestroyBuffer(staging.Buffer);
        api.vkFreeMemory(staging.Memory);
        _device.NoteBufferDestroyed();
    }

    /// <summary>
    /// Destroys everything. The caller has drained the GPU (<c>VkRenderer.Dispose</c> does, as it does before
    /// destroying the atlases); the store itself waits for nothing but its own encoder threads.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();
        var allStopped = true;
        foreach (var worker in _workers)
            allStopped &= worker.Join(_joinTimeout);
        // A worker still inside an encode (a slow face) is left to finish alone: it asks the queue for its next glyph
        // when it is done, and a queue disposed under it would throw on a thread nobody catches. Left undisposed the
        // queue holds nothing but memory, which the collector takes once the worker has gone.
        if (allStopped) _queue.Dispose();

        var api = _ctx.DeviceApi;
        if (_last is { } last)
        {
            foreach (var b in last.Batches)
                DestroyStaging(b.Staging);
            foreach (var g in last.Growths)
            {
                api.vkDestroyBuffer(g.OldBuffer);
                api.vkFreeMemory(g.OldMemory);
                _device.NoteBufferDestroyed();
                _device.FreeSlugDescriptorSet(g.OldSet);
            }
            _last = null;
        }
        foreach (var staging in _freeStaging)
            DestroyStaging(staging);
        _freeStaging.Clear();
        api.vkDestroyBuffer(_buffer);
        api.vkFreeMemory(_memory);
        _device.NoteBufferDestroyed();
        _device.FreeSlugDescriptorSet(_set);
        _buffer = VkBuffer.Null;
        _memory = VkDeviceMemory.Null;
        _set = VkDescriptorSet.Null;
    }
}
