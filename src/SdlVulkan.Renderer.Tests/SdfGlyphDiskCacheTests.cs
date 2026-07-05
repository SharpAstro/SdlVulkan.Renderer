using System;
using System.IO;
using System.Linq;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Round-trips the v4 (glyph-identity-keyed) <c>.sdfg</c> on-disk format through a
/// write → close → reopen → read cycle. Two entry shapes must survive intact: an OpenType
/// glyph keyed by numeric id (no name) and a Type1 glyph keyed by PostScript glyph name.
/// The format carries a variable-length name field between the fixed metadata and the pixel
/// payload, so a stride/offset bug would desync every entry after the first named one — this
/// is the only coverage of that serialization (the GPU render test uses no disk cache).
/// Also asserts the header self-heal: reopening with mismatched raster params reads nothing.
/// </summary>
public sealed class SdfGlyphDiskCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdfg-test-" + Guid.NewGuid().ToString("N"));
    private const string FontId = "mem:diskcache-test";
    private static readonly byte[] FontBytes = { 1, 2, 3, 4, 5, 6, 7, 8 };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // 2x2 RGBA, distinct per-byte so a byte-order / row-stride bug shows up as a mismatch.
    private static MtsdfGlyphBitmap MakeBitmap(byte fill)
    {
        var rgba = new byte[2 * 2 * 4];
        for (var i = 0; i < rgba.Length; i++) rgba[i] = (byte)(fill + i);
        return new MtsdfGlyphBitmap(rgba, 2, 2, BearingX: 1, BearingY: -3, AdvanceX: 7.5f, Spread: 4f);
    }

    [Fact]
    public void RoundTrips_GidKeyed_And_NameKeyed_Entries()
    {
        var ot = MakeBitmap(10);
        var t1 = MakeBitmap(100);

        using (var cache = new SdfGlyphDiskCache(_dir, rasterSize: 64f, spread: 4f))
        {
            cache.RegisterMemoryFont(FontId, FontBytes);
            cache.AppendGlyph(FontId, gid: 42, name: null, in ot);     // OpenType — keyed by gid
            cache.AppendGlyph(FontId, gid: 0, name: "fi", in t1);      // Type1 — keyed by glyph name
        } // Dispose drains the background writer + closes the file

        using var reader = new SdfGlyphDiskCache(_dir, rasterSize: 64f, spread: 4f);
        reader.RegisterMemoryFont(FontId, FontBytes);
        var entries = reader.LoadEntriesForFont(FontId);

        entries.Count.ShouldBe(2);

        var otEntry = entries.Single(e => e.Name is null);
        otEntry.Gid.ShouldBe(42u);
        otEntry.Bitmap.Width.ShouldBe(2);
        otEntry.Bitmap.Height.ShouldBe(2);
        otEntry.Bitmap.BearingX.ShouldBe(1);
        otEntry.Bitmap.BearingY.ShouldBe(-3);
        otEntry.Bitmap.AdvanceX.ShouldBe(7.5f);
        otEntry.Bitmap.Rgba.ShouldBe(ot.Rgba);

        var t1Entry = entries.Single(e => e.Name is not null);
        t1Entry.Name.ShouldBe("fi");
        t1Entry.Gid.ShouldBe(0u);
        t1Entry.Bitmap.Rgba.ShouldBe(t1.Rgba);
    }

    [Fact]
    public void MismatchedRasterParams_SelfHeals_ToEmpty()
    {
        var ot = MakeBitmap(10);
        using (var cache = new SdfGlyphDiskCache(_dir, rasterSize: 64f, spread: 4f))
        {
            cache.RegisterMemoryFont(FontId, FontBytes);
            cache.AppendGlyph(FontId, gid: 42, name: null, in ot);
        }

        // Different raster size → header mismatch → the stale file reads as a cold (empty) cache.
        using var reader = new SdfGlyphDiskCache(_dir, rasterSize: 32f, spread: 4f);
        reader.RegisterMemoryFont(FontId, FontBytes);
        reader.LoadEntriesForFont(FontId).ShouldBeEmpty();
    }
}
