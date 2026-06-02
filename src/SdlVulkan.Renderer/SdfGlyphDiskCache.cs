using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// Disk-persistent cache of rasterized SDF glyph bitmaps. Survives process restarts so
/// re-opening a document with the same fonts skips the expensive SDF rasterization
/// pass (~10 ms per glyph) entirely. Pairs with <see cref="VkSdfFontAtlas"/>: on first
/// request for a font the atlas pulls every cached glyph in one read and bulk-inserts
/// them; thereafter, freshly rasterized glyphs are appended to disk for the next session.
///
/// <para>One file per font in the configured cache directory, named
/// <c>{font-content-hash}.sdfg</c>. The hash is FNV-1a 64-bit over the font's bytes,
/// so cross-machine path differences don't invalidate the cache, but a font binary
/// change generates a new file (the old file is orphaned; eviction by age is a future
/// concern).</para>
///
/// <para>Format versioning: the file header carries a <see cref="FormatVersion"/>
/// constant; mismatched versions or mismatched rasterSize/spread cause the file to
/// be truncated and rewritten with a fresh header on the next append.</para>
///
/// <para>Crash safety: each entry is written via a single <c>FileStream.Write</c> call
/// preceded by a length prefix. A process crash mid-append leaves at worst one
/// half-written entry at the tail; readers stop at the first malformed entry and treat
/// the rest as missing — they will be re-rasterized and re-appended next session.</para>
/// </summary>
public sealed class SdfGlyphDiskCache : IDisposable
{
    // "SDFG" stored little-endian — visible as the ASCII tag in a hex dump.
    private const uint Magic = 0x47464453;
    // v2: SharpAstro.Fonts fixed EmbeddedSubset glyph selection for raw-keyed (3,0) CJK
    // subsets (mPDF IPAMincho/IPAGothic etc.). The cached SDF bitmap is keyed by
    // (charCode, character, hint) but was rasterized from the GID the OLD selection chose —
    // so v1 caches hold the garbled glyphs. Bumping the version invalidates every stale
    // .sdfg (header mismatch → truncate + rewrite), forcing a re-rasterize with the fixed
    // glyph mapping. Bump this whenever the charCode→GID mapping logic changes.
    private const uint FormatVersion = 2;
    // Header: magic(4) + version(4) + rasterSize(4) + spread(4) + fontHash(8) + reserved(8) = 32 bytes.
    private const int HeaderSize = 32;
    // Per-entry metadata size *after* the 4-byte length prefix:
    // charCode(4) + character(4) + hint(1)+reserved(3) + width(4) + height(4)
    // + advanceX(4) + bearingX(4) + bearingY(4) = 32 bytes.
    private const int EntryMetaSize = 32;
    // Defensive upper bound for a single entry's length field; 128x128 SDF is 16 KB so 16 MB is more than ample.
    private const int MaxReasonableEntryLen = 16 * 1024 * 1024;

    public float RasterSize { get; }
    public float Spread { get; }

    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, ulong> _fontPathToHash = new();
    // Memory-resident fonts (embedded PDF subsets, etc.) live under "mem:..." identifiers
    // and are not file-backed. The caller registers their byte content here so we can
    // hash and cache their glyphs too — without this, the vast majority of glyphs
    // (anything from a PDF) would skip the disk cache entirely.
    private readonly ConcurrentDictionary<string, ulong> _memoryFontHashes = new();
    // Lazy<> guarantees single-init per font even under concurrent first-access — avoids
    // racing two FileStreams open in append mode and writing duplicate headers. With the
    // background writer below, this is now only touched on the writer thread.
    private readonly ConcurrentDictionary<string, Lazy<FileStream?>> _appendStreams = new();
    private volatile bool _disposed;

    // All disk WRITES run on this single background thread, so the render thread never blocks on
    // Write()/Flush() (the fsync is the cold-open hitch). AppendGlyph/AppendGlyphs serialize the
    // entry in-memory on the caller's thread (cheap, no I/O) and hand the bytes off to the queue;
    // the writer owns the FileStreams and coalesces fsyncs. Reads stay synchronous on the caller —
    // a read happens once per font on first use, before any write for that font, so they never race.
    // Bounded so a slow disk / memory pressure can't let the queue grow without limit (~1024 × ~2 KB
    // ≈ 2 MB cap). TryAdd never blocks the caller; on overflow the glyph is dropped (re-rasterized
    // next session). The disk cache is best-effort by design.
    private const int WriteQueueCapacity = 1024;
    private readonly BlockingCollection<(string Font, byte[] Bytes)> _writeQueue = new(WriteQueueCapacity);
    private readonly Thread _writerThread;
    // Set if the writer thread dies (any non-IOException) so producers stop enqueuing into a dead queue.
    private volatile bool _writerDead;
    private long _droppedWrites;

    private volatile bool _pauseWrites;

    /// <summary>When true, appends no-op — the host sets this under critical memory pressure so this
    /// session's glyph rasterizations aren't written to disk (no write traffic competing with OS swap).
    /// Glyphs still render from the in-memory atlas; they just won't be persisted for next session.</summary>
    public bool PauseWrites { get => _pauseWrites; set => _pauseWrites = value; }

    public SdfGlyphDiskCache(string cacheDir, float rasterSize, float spread)
    {
        _cacheDir = cacheDir;
        RasterSize = rasterSize;
        Spread = spread;
        Directory.CreateDirectory(cacheDir);
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "SdfGlyphDiskWriter" };
        _writerThread.Start();
    }

    /// <summary>
    /// Registers an in-memory font's byte content under <paramref name="fontId"/> (typically
    /// a <c>"mem:..."</c> identifier) so its rasterized glyphs can be persisted to disk.
    /// The cache key is a content hash of <paramref name="fontData"/> — the same PDF
    /// re-extracted in a future session will collide with the same cache file (PDF embedded
    /// fonts are byte-stable across extractions).
    /// </summary>
    public void RegisterMemoryFont(string fontId, byte[] fontData)
    {
        if (_disposed) return;
        if (fontData is null || fontData.Length == 0) return;
        _memoryFontHashes.GetOrAdd(fontId, _ => ComputeContentHash(fontData));
    }

    /// <summary>
    /// Loads all previously cached SDF bitmaps for the given font. Returns an empty list
    /// if no cache exists, the file is corrupted, the header parameters (raster size,
    /// spread) don't match this session, or the font file is missing/unreadable.
    /// </summary>
    /// <summary>True once a content hash is resolvable for this font id — i.e. a file font that exists,
    /// or a <c>mem:</c> id that has been registered via <see cref="RegisterMemoryFont"/>. Callers use
    /// this to avoid committing a font to a "already loaded" guard before its bytes are even available.</summary>
    public bool HasHashFor(string fontPath) => !_disposed && TryGetFontHash(fontPath, out _);

    public IReadOnlyList<DiskGlyphEntry> LoadEntriesForFont(string fontPath)
    {
        if (_disposed) return [];
        if (!TryGetFontHash(fontPath, out var hash)) return [];

        var file = Path.Combine(_cacheDir, hash.ToString("x16") + ".sdfg");
        if (!File.Exists(file)) return [];

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadFile(fs, hash);
        }
        catch (IOException)
        {
            // File contention, partial write, etc. Treat as cold cache.
            return [];
        }
    }

    /// <summary>
    /// Appends a freshly rasterized glyph to the cache file for <paramref name="fontPath"/>.
    /// Whitespace / zero-size bitmaps are skipped — they're derived at runtime in the atlas.
    /// </summary>
    public void AppendGlyph(string fontPath, int charCode, Rune character, GlyphMapHint hint, in SdfGlyphBitmap bitmap)
    {
        if (_disposed || PauseWrites) return;
        if (!IsAppendable(in bitmap)) return;
        // Serialize in-memory on the caller (cheap), hand off to the writer thread. The font-hash
        // resolution + file open happen on the writer thread (OpenStream), keeping the caller's
        // thread free of any disk I/O — including the first-use font-hash file read.
        Enqueue(fontPath, SerializeEntry(charCode, character, hint, in bitmap));
    }

    /// <summary>
    /// Batch append for use after a parallel rasterization pass. Same per-entry filtering
    /// as <see cref="AppendGlyph"/> — small or null bitmaps are silently skipped. The whole
    /// batch is packed into one buffer so the writer thread takes a single hand-off.
    /// </summary>
    public void AppendGlyphs(string fontPath, IReadOnlyList<(int CharCode, Rune Character, GlyphMapHint Hint, SdfGlyphBitmap Bitmap)> entries)
    {
        if (_disposed || PauseWrites || entries.Count == 0) return;

        using var ms = new MemoryStream();
        foreach (var e in entries)
        {
            if (!IsAppendable(in e.Bitmap)) continue;
            var b = SerializeEntry(e.CharCode, e.Character, e.Hint, in e.Bitmap);
            ms.Write(b, 0, b.Length);
        }
        if (ms.Length > 0) Enqueue(fontPath, ms.ToArray());
    }

    // Hands a serialized payload to the background writer. Never blocks the caller (render thread):
    // TryAdd drops the glyph if the bounded queue is full or the writer is gone — caching is best-effort.
    private void Enqueue(string fontPath, byte[] bytes)
    {
        if (_disposed || _writerDead || _writeQueue.IsAddingCompleted) return;
        try
        {
            if (!_writeQueue.TryAdd((fontPath, bytes)))
            {
                var n = Interlocked.Increment(ref _droppedWrites);
                // Log only at powers of two so a sustained overflow doesn't spam the console.
                if ((n & (n - 1)) == 0)
                    Console.Error.WriteLine($"[SdfDiskCache] write queue full — dropped {n} glyph write(s) (best-effort cache)");
            }
        }
        catch (InvalidOperationException ex)
        {
            // CompleteAdding raced with this TryAdd during shutdown — the glyph just won't be cached.
            Console.Error.WriteLine($"[SdfDiskCache] dropped glyph write during shutdown: {ex.Message}");
        }
    }

    // Single consumer of the write queue. Owns the per-font append streams, writes serialized
    // payloads, and coalesces fsync-free flushes (flush only once we've caught up with the queue).
    private void WriterLoop()
    {
        var dirty = new HashSet<FileStream>();
        try
        {
            foreach (var (font, bytes) in _writeQueue.GetConsumingEnumerable())
            {
                try
                {
                    var stream = GetOrOpenAppendStream(font);
                    if (stream is null) continue;
                    stream.Write(bytes, 0, bytes.Length);
                    dirty.Add(stream);
                    if (_writeQueue.Count == 0 && dirty.Count > 0)
                    {
                        foreach (var s in dirty) s.Flush();
                        dirty.Clear();
                    }
                }
                catch (IOException ex)
                {
                    // Disk full, lock contention, etc. Caching is best-effort.
                    Console.Error.WriteLine($"[SdfDiskCache] write failed for {font}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // A non-IOException (OOM, ObjectDisposed, etc.) would otherwise kill this thread silently
            // and leave producers enqueuing into a queue with no consumer → unbounded heap growth.
            // Mark dead and complete the queue so Enqueue() short-circuits.
            Console.Error.WriteLine($"[SdfDiskCache] writer thread died: {ex.GetType().Name}: {ex.Message}");
            _writerDead = true;
            // Dispose() only disposes the queue after Join()ing this thread, so CompleteAdding is safe
            // here (and is idempotent if Dispose already called it).
            _writeQueue.CompleteAdding();
        }
        finally
        {
            // Drain: flush + close every open stream so the last burst is durable.
            foreach (var lazy in _appendStreams.Values)
            {
                try { if (lazy.IsValueCreated) lazy.Value?.Dispose(); }
                catch (IOException ex) { Console.Error.WriteLine($"[SdfDiskCache] stream close failed: {ex.Message}"); }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeQueue.CompleteAdding();
        // Let the writer drain the queue and close the files. Bounded so a stuck disk can't hang exit.
        try { _writerThread.Join(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Console.Error.WriteLine($"[SdfDiskCache] writer join failed: {ex.Message}"); }
        _writeQueue.Dispose();
    }

    private static bool IsAppendable(in SdfGlyphBitmap bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return false;
        if (bitmap.Alpha is null) return false;
        return bitmap.Alpha.Length >= bitmap.Width * bitmap.Height;
    }

    private FileStream? GetOrOpenAppendStream(string fontPath)
    {
        return _appendStreams.GetOrAdd(fontPath, p => new Lazy<FileStream?>(
            () => OpenStream(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private FileStream? OpenStream(string fontPath)
    {
        try
        {
            if (!TryGetFontHash(fontPath, out var hash)) return null;
            var file = Path.Combine(_cacheDir, hash.ToString("x16") + ".sdfg");

            // Probe an existing file's header. If it's missing, truncated, on a stale
            // version, or written for different SDF parameters (rasterSize/spread/font),
            // start fresh — otherwise we'd be appending entries with mismatched geometry
            // behind a stale header.
            var resetFile = true;
            if (File.Exists(file))
            {
                using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (probe.Length >= HeaderSize)
                {
                    Span<byte> hdr = stackalloc byte[HeaderSize];
                    probe.ReadExactly(hdr);
                    var magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(0, 4));
                    var version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
                    var rasterSize = BinaryPrimitives.ReadSingleLittleEndian(hdr.Slice(8, 4));
                    var spread = BinaryPrimitives.ReadSingleLittleEndian(hdr.Slice(12, 4));
                    var fontHash = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(16, 8));
                    if (magic == Magic && version == FormatVersion
                        && rasterSize == RasterSize && spread == Spread && fontHash == hash)
                        resetFile = false;
                }
            }

            var mode = resetFile ? FileMode.Create : FileMode.Append;
            var fs = new FileStream(file, mode, FileAccess.Write, FileShare.Read);
            if (resetFile) WriteHeader(fs, hash);
            return fs;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteHeader(FileStream fs, ulong fontHash)
    {
        Span<byte> hdr = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), FormatVersion);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(8, 4), RasterSize);
        BinaryPrimitives.WriteSingleLittleEndian(hdr.Slice(12, 4), Spread);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(16, 8), fontHash);
        // hdr[24..32] reserved (zero)
        fs.Write(hdr);
    }

    // Serializes one glyph entry to a self-contained byte buffer (length-prefixed). Runs on the
    // caller's thread (pure in-memory) so the writer thread only does the actual disk write.
    private static byte[] SerializeEntry(int charCode, Rune character, GlyphMapHint hint, in SdfGlyphBitmap bitmap)
    {
        var alphaLen = bitmap.Width * bitmap.Height;
        // entryLen prefix covers everything after itself: 32-byte metadata block + alpha pixels.
        var entryLen = EntryMetaSize + alphaLen;
        var buf = new byte[4 + entryLen];
        var sp = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(0, 4), entryLen);
        // Metadata layout — keep aligned with the reader in ReadFile().
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(4, 4), charCode);
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(8, 4), character.Value);
        sp[12] = (byte)hint;
        // sp[13..16] reserved (zero)
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(16, 4), bitmap.Width);
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(20, 4), bitmap.Height);
        BinaryPrimitives.WriteSingleLittleEndian(sp.Slice(24, 4), bitmap.AdvanceX);
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(28, 4), bitmap.BearingX);
        BinaryPrimitives.WriteInt32LittleEndian(sp.Slice(32, 4), bitmap.BearingY);
        bitmap.Alpha.AsSpan(0, alphaLen).CopyTo(sp.Slice(36, alphaLen));
        return buf;
    }

    private List<DiskGlyphEntry> ReadFile(FileStream fs, ulong expectedFontHash)
    {
        var result = new List<DiskGlyphEntry>();
        if (fs.Length < HeaderSize) return result;

        Span<byte> hdr = stackalloc byte[HeaderSize];
        fs.ReadExactly(hdr);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(0, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
        var rasterSize = BinaryPrimitives.ReadSingleLittleEndian(hdr.Slice(8, 4));
        var spread = BinaryPrimitives.ReadSingleLittleEndian(hdr.Slice(12, 4));
        var fontHash = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(16, 8));

        if (magic != Magic || version != FormatVersion) return result;
        if (rasterSize != RasterSize || spread != Spread) return result;
        if (fontHash != expectedFontHash) return result;

        // Allocate once and reuse — CA2014 (stackalloc in a loop is a stack-overflow risk).
        Span<byte> lenBuf = stackalloc byte[4];
        Span<byte> meta = stackalloc byte[EntryMetaSize];
        while (fs.Position < fs.Length)
        {
            if (!TryReadExactly(fs, lenBuf)) break;
            var entryLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (entryLen < EntryMetaSize || entryLen > MaxReasonableEntryLen) break;
            if (fs.Position + entryLen > fs.Length) break;

            if (!TryReadExactly(fs, meta)) break;

            var charCode = BinaryPrimitives.ReadInt32LittleEndian(meta.Slice(0, 4));
            var characterValue = BinaryPrimitives.ReadInt32LittleEndian(meta.Slice(4, 4));
            var hint = (GlyphMapHint)meta[8];
            var width = BinaryPrimitives.ReadInt32LittleEndian(meta.Slice(12, 4));
            var height = BinaryPrimitives.ReadInt32LittleEndian(meta.Slice(16, 4));
            var advanceX = BinaryPrimitives.ReadSingleLittleEndian(meta.Slice(20, 4));
            var bearingX = BinaryPrimitives.ReadInt32LittleEndian(meta.Slice(24, 4));
            var bearingY = BinaryPrimitives.ReadInt32LittleEndian(meta.Slice(28, 4));

            var alphaLen = entryLen - EntryMetaSize;
            // Tightly-packed bitmap: alphaLen MUST equal width * height. A mismatch
            // means corruption or a partial write — bail out and skip the rest of the file.
            if (alphaLen != width * height) break;
            var alpha = new byte[alphaLen];
            if (!TryReadExactly(fs, alpha)) break;

            if (!Rune.TryCreate(characterValue, out var character)) continue;
            var bitmap = new SdfGlyphBitmap(alpha, width, height, bearingX, bearingY, advanceX, spread);
            result.Add(new DiskGlyphEntry(charCode, character, hint, bitmap));
        }
        return result;
    }

    private static bool TryReadExactly(FileStream fs, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer.Slice(read));
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Resolves a font identifier (either a real file path or a registered <c>mem:</c> id)
    /// to its content hash. Returns <c>false</c> if the font is a mem-id that was never
    /// registered or the file is missing — in either case the cache is skipped.
    /// </summary>
    private bool TryGetFontHash(string fontPath, out ulong hash)
    {
        // Memory fonts: must be pre-registered via RegisterMemoryFont so we have the bytes
        // available to hash. Without registration there's no way to derive a stable key.
        if (fontPath.StartsWith("mem:", StringComparison.Ordinal))
            return _memoryFontHashes.TryGetValue(fontPath, out hash);

        // File-backed fonts: hash the file contents once and memoize.
        if (_fontPathToHash.TryGetValue(fontPath, out hash)) return true;
        if (!File.Exists(fontPath)) return false;
        hash = _fontPathToHash.GetOrAdd(fontPath, ComputeFileHash);
        return true;
    }

    private static ulong ComputeFileHash(string fontPath)
    {
        // FNV-1a 64-bit over the full font file. Fonts are typically 100 KB-1 MB,
        // so hashing them at first-use is sub-millisecond and the result is stable
        // across machines (which file paths are not — extracted-to-temp embedded
        // fonts get fresh paths every session).
        using var fs = new FileStream(fontPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ComputeFnv1aStream(fs);
    }

    private static ulong ComputeContentHash(byte[] data)
    {
        // FNV-1a 64-bit over the byte buffer; matches ComputeFileHash bit-for-bit so a
        // memory-registered font and a file-extracted copy of the same bytes share a
        // cache entry (useful when the same PDF was opened from disk and from memory).
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        var hash = FnvOffsetBasis;
        for (var i = 0; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= FnvPrime;
        }
        hash ^= (ulong)data.Length;
        hash *= FnvPrime;
        return hash;
    }

    private static ulong ComputeFnv1aStream(Stream stream)
    {
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        var hash = FnvOffsetBasis;
        Span<byte> buf = stackalloc byte[8192];
        long total = 0;
        int n;
        while ((n = stream.Read(buf)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                hash ^= buf[i];
                hash *= FnvPrime;
            }
            total += n;
        }
        // Mix length in too so two fonts with the same prefix but different lengths
        // can't collide. Matches ComputeContentHash so the two paths agree.
        hash ^= (ulong)total;
        hash *= FnvPrime;
        return hash;
    }
}

/// <summary>
/// A single SDF glyph record reconstructed from disk. The <see cref="Bitmap"/> can be
/// fed straight into <c>VkSdfFontAtlas.InsertRasterized</c> the same way a freshly
/// rasterized bitmap would be.
/// </summary>
public readonly record struct DiskGlyphEntry(int CharCode, Rune Character, GlyphMapHint Hint, SdfGlyphBitmap Bitmap);
