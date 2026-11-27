using System;
using System.IO;
using System.Text;
using System.Threading;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>The GPU memory counters of <see cref="VkRenderer.MemoryStats"/> against the atlases' real allocations.</summary>
[Collection("OffscreenGpu")]
public sealed class MemoryCountersTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 128;
    private const uint Height = 64;
    private const float Size = 40f;

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static void Frame(VkRenderer r, VulkanContext ctx, string text, bool sdf)
    {
        r.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var white = new RGBAColor32(255, 255, 255, 255);
        if (sdf) r.BeginSdfGlyphBatch(white, Size); else r.BeginGlyphBatch(white);
        var x = 2f;
        foreach (var ch in text)
        {
            if (sdf) r.AddBatchedSdfGlyph(FontPath, new Rune(ch), -1, x, 8f);
            else r.AddBatchedGlyph(FontPath, Size, new Rune(ch), -1, x, 8f);
            x += 6f;
        }
        r.EndGlyphBatch();
        r.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public void Counters_are_non_zero_after_text_and_match_the_atlases()
    {
        if (gpu.Context is not { } ctx) { Assert.Skip("no Vulkan ICD on this host"); return; }
        ctx.ResizeOffscreen(Width, Height);
        using var renderer = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 128);

        var before = renderer.MemoryStats;
        before.SlugStoreBytes.ShouldBe(0, "no slug draw yet");
        before.SwapchainImageCount.ShouldBe(0, "an offscreen context has no swapchain");

        for (var i = 0; i < 20; i++) { Frame(renderer, ctx, "Hello", sdf: true); Thread.Sleep(10); }
        Frame(renderer, ctx, "Hello", sdf: false);

        var s = renderer.MemoryStats;
        s.VertexRingBytes.ShouldBe(ctx.VertexRingTotalBytes);
        s.VertexRingBytes.ShouldBeGreaterThan(0);
        s.BitmapAtlasBytes.ShouldBe(renderer.FontAtlas!.Bytes);
        s.BitmapAtlasBytes.ShouldBeGreaterThan(0);
        var atlas = renderer.BaseSdfAtlas!;
        s.SdfBasePages.ShouldBe(atlas.PageCount);
        s.SdfBasePageDimension.ShouldBe(atlas.PageDimension);
        s.SdfBaseBytes.ShouldBe((long)atlas.PageCount * atlas.PageDimension * atlas.PageDimension * 4);
        s.SdfBaseBytes.ShouldBeGreaterThan(0);
        s.TotalBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Sdf_bytes_grow_by_one_pages_bytes_when_a_page_is_added()
    {
        if (gpu.Context is not { } ctx) { Assert.Skip("no Vulkan ICD on this host"); return; }
        ctx.ResizeOffscreen(Width, Height);
        using var renderer = new VkRenderer(ctx, Width, Height, sdfInitialAtlasDim: 64);
        var atlas = renderer.BaseSdfAtlas!;

        var startPages = atlas.PageCount;
        var startBytes = renderer.MemoryStats.SdfBaseBytes;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var text = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        while (atlas.PageCount == startPages && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            Frame(renderer, ctx, text, sdf: true);
            Thread.Sleep(10);
        }

        atlas.PageCount.ShouldBeGreaterThan(startPages, "the flood should have appended a page");
        var pageBytes = (long)atlas.PageDimension * atlas.PageDimension * 4;
        renderer.MemoryStats.SdfBaseBytes.ShouldBe(startBytes + (atlas.PageCount - startPages) * pageBytes);
    }
}
