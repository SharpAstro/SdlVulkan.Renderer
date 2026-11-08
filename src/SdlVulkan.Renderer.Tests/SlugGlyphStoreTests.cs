using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DIR.Lib;
using SdlVulkan.Renderer;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="VkSlugGlyphStore"/> on an offscreen device: what is uploaded is the encoder's blob word for word,
/// the buffer grows without moving a glyph, a miss answers at once and the glyph arrives after the encode drains,
/// a space is known and empty, and a frame that never reached the queue has its upload recorded again.
/// Each scenario runs under the Khronos validation layer (with synchronization validation) when the host has it,
/// and fails on any validation ERROR; without the layer it runs on the shared context and says nothing of validation.
/// </summary>
[Collection("OffscreenGpu")]
public sealed unsafe class SlugGlyphStoreTests(OffscreenGpuFixture gpu)
{
    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static string ThisFile([CallerFilePath] string path = "") => path;

    // ---- scenario host ----

    private void Run(Action<VulkanContext> scenario)
    {
        if (ValidatedOffscreen.TryCreate(128, 128, out var ctx, out var messenger, out var api, out _))
        {
            ValidatedOffscreen.Messages.Clear();
            try
            {
                scenario(ctx!);
                var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
                Assert.True(errors.Length == 0, $"validation errors:\n{string.Join("\n\n", errors)}");
            }
            finally
            {
                ValidatedOffscreen.Destroy(ctx, messenger, api);
            }
            return;
        }

        if (gpu.Context is not { } shared)
        {
            Assert.Skip("no Vulkan ICD available");
            return;
        }
        scenario(shared);
    }

    private static GlyphIdentity Identity(ManagedFontRasterizer rasterizer, char c)
        => rasterizer.ResolveGlyphIdentity(FontPath, new Rune(c), -1, GlyphMapHint.Auto);

    // One frame that uploads, and (unless it is to be left unended, as a dropped frame) finishes on the GPU.
    private static void Frame(VulkanContext ctx, VkSlugGlyphStore store, bool end = true)
    {
        var cmd = ctx.BeginOffscreenFrame();
        store.RecordUploads(cmd);
        ctx.BeginOffscreenRenderPass(cmd, 0f, 0f, 0f, 1f);
        if (!end) return;
        ctx.EndOffscreenFrame(cmd);
        ctx.WaitOffscreenFrameComplete();
    }

    // Asks for the glyphs, waits for the workers, and runs frames until every one is answered.
    private static void Load(VulkanContext ctx, VkSlugGlyphStore store, ManagedFontRasterizer rasterizer, string chars)
    {
        foreach (var c in chars) store.TryGet(FontPath, Identity(rasterizer, c), out _);
        for (var round = 0; round < 200; round++)
        {
            WaitForEncodes(store);
            Frame(ctx, store);
            if (!store.HasPending) return;
        }
        Assert.Fail("glyphs still pending after 200 frames");
    }

    private static void WaitForEncodes(VkSlugGlyphStore store)
    {
        var sw = Stopwatch.StartNew();
        while (store.EncodesInFlight > 0)
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) Assert.Fail("encodes did not finish within 30 s");
            System.Threading.Thread.Sleep(1);
        }
    }

    // The store's words as the device holds them, copied out through a host buffer.
    private static int[] Readback(VulkanContext ctx, VkSlugGlyphStore store, int words)
    {
        var api = ctx.DeviceApi;
        var size = (ulong)words * sizeof(int);
        VkBufferCreateInfo ci = new()
        {
            size = size, usage = VkBufferUsageFlags.TransferDst, sharingMode = VkSharingMode.Exclusive
        };
        api.vkCreateBuffer(&ci, null, out var host).CheckResult();
        api.vkGetBufferMemoryRequirements(host, out var req);
        VkMemoryAllocateInfo ai = new()
        {
            allocationSize = req.size,
            memoryTypeIndex = ctx.FindMemoryType(req.memoryTypeBits,
                VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent)
        };
        api.vkAllocateMemory(&ai, null, out var mem).CheckResult();
        api.vkBindBufferMemory(host, mem, 0);

        var source = store.Buffer;
        ctx.ExecuteOneShot(cmd =>
        {
            VkBufferCopy region = new() { srcOffset = 0, dstOffset = 0, size = size };
            api.vkCmdCopyBuffer(cmd, source, host, 1, &region);
        });

        void* mapped;
        api.vkMapMemory(mem, 0, size, 0, &mapped).CheckResult();
        var result = new Span<int>(mapped, words).ToArray();
        api.vkUnmapMemory(mem);
        api.vkDestroyBuffer(host);
        api.vkFreeMemory(mem);
        return result;
    }

    private static SlugGlyphBlob Encode(ManagedFontRasterizer rasterizer, GlyphIdentity id)
    {
        Assert.True(new SlugGlyphEncoder().TryEncode(rasterizer, FontPath, id, out var blob));
        return blob;
    }

    private static void AssertGlyphOnDevice(VkSlugGlyphStore store, ManagedFontRasterizer rasterizer, int[] device, char c)
    {
        var id = Identity(rasterizer, c);
        Assert.True(store.TryGet(FontPath, id, out var entry), $"'{c}' known");
        var blob = Encode(rasterizer, id);
        Assert.False(entry.IsEmpty);
        Assert.Equal(blob.UnitsPerEm, entry.UnitsPerEm);
        Assert.Equal(blob.MinXEm, entry.MinXEm);
        Assert.Equal(blob.MinYEm, entry.MinYEm);
        Assert.Equal(blob.MaxXEm, entry.MaxXEm);
        Assert.Equal(blob.MaxYEm, entry.MaxYEm);
        Assert.Equal(blob.Words, device.AsSpan((int)entry.WordOffset, blob.Words.Length).ToArray());
    }

    // ---- tests ----

    [Fact]
    public void UploadedWords_equal_the_encoders_blob()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            var store = new VkSlugGlyphStore(ctx, rasterizer);
            try
            {
                const string chars = "AgQ@";
                Load(ctx, store, rasterizer, chars);

                var device = Readback(ctx, store, store.UsedWords);
                foreach (var c in chars) AssertGlyphOnDevice(store, rasterizer, device, c);
                Assert.Equal(0, store.GrowCount);
            }
            finally { store.Dispose(); }
        });
    }

    [Fact]
    public void Growth_keeps_every_earlier_glyphs_offset_and_words()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            // Room for about one glyph: every later one outgrows it.
            var store = new VkSlugGlyphStore(ctx, rasterizer, initialCapacityWords: 512);
            try
            {
                Load(ctx, store, rasterizer, "A");
                Assert.True(store.TryGet(FontPath, Identity(rasterizer, 'A'), out var first));
                var firstBuffer = store.Buffer;
                var firstSet = store.DescriptorSet;

                const string more = "BCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@&%";
                Load(ctx, store, rasterizer, more);

                Assert.True(store.GrowCount >= 1, "the buffer grew");
                Assert.True(store.CapacityWords > 512);
                Assert.NotEqual(firstBuffer, store.Buffer);
                Assert.NotEqual(firstSet, store.DescriptorSet);
                Assert.True(store.TryGet(FontPath, Identity(rasterizer, 'A'), out var firstAgain));
                Assert.Equal(first, firstAgain);

                var device = Readback(ctx, store, store.UsedWords);
                foreach (var c in "A" + more) AssertGlyphOnDevice(store, rasterizer, device, c);

                // Old buffers, memory, sets and staging retire through the deferred schedule: idle frames drain it.
                for (var i = 0; i < 4; i++) Frame(ctx, store);
                Assert.Equal(0, ctx.PendingDeferredDestroys);
            }
            finally { store.Dispose(); }
        });
    }

    [Fact]
    public void A_miss_returns_at_once_and_the_glyph_is_present_after_the_drain()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            // One glyph's worth of words per frame, so the drain is spread over frames.
            var store = new VkSlugGlyphStore(ctx, rasterizer, uploadBudgetWords: 1);
            try
            {
                var a = Identity(rasterizer, 'A');
                var b = Identity(rasterizer, 'B');
                Assert.False(store.TryGet(FontPath, a, out _));
                Assert.True(store.HasPending);
                Assert.False(store.TryGet(FontPath, b, out _));

                WaitForEncodes(store);
                Frame(ctx, store);
                // One glyph made it; the other still waits for its frame, and asking never stalls or loses the first.
                var knownAfterOne = new[] { a, b }.Count(id => store.TryGet(FontPath, id, out _));
                Assert.Equal(1, knownAfterOne);
                Assert.True(store.HasPending);

                Frame(ctx, store);
                Assert.True(store.TryGet(FontPath, a, out _));
                Assert.True(store.TryGet(FontPath, b, out _));
                Assert.False(store.HasPending);
            }
            finally { store.Dispose(); }
        });
    }

    [Fact]
    public void A_space_is_a_hit_that_draws_nothing()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            var store = new VkSlugGlyphStore(ctx, rasterizer);
            try
            {
                Load(ctx, store, rasterizer, " ");
                Assert.True(store.TryGet(FontPath, Identity(rasterizer, ' '), out var entry));
                Assert.True(entry.IsEmpty);
                Assert.True(entry.UnitsPerEm > 0, "a space knows its face, unlike a glyph that failed");
                Assert.Equal(0, store.UsedWords);
                Assert.Equal(0, store.FailedGlyphs);
            }
            finally { store.Dispose(); }
        });
    }

    [Fact]
    public void A_face_that_cannot_be_drawn_answers_empty_instead_of_asking_every_frame()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            var store = new VkSlugGlyphStore(ctx, rasterizer);
            try
            {
                var missing = new GlyphIdentity(5, null);
                Assert.False(store.TryGet("mem:never-registered", missing, out _));
                for (var i = 0; i < 100 && store.HasPending; i++)
                {
                    WaitForEncodes(store);
                    Frame(ctx, store);
                }
                Assert.True(store.TryGet("mem:never-registered", missing, out var entry));
                Assert.True(entry.IsEmpty);
                Assert.Equal(1, store.FailedGlyphs);
                Assert.False(store.HasPending);
            }
            finally { store.Dispose(); }
        });
    }

    [Fact]
    public void A_dropped_frame_has_its_upload_and_growth_recorded_again()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            var store = new VkSlugGlyphStore(ctx, rasterizer, initialCapacityWords: 512);
            try
            {
                Load(ctx, store, rasterizer, "A");
                var growsBefore = store.GrowCount;

                // The next frame uploads (and grows) and is then begun-and-never-ended, which the context
                // treats as a frame that never reached the queue.
                foreach (var c in "BCDEFGH") store.TryGet(FontPath, Identity(rasterizer, c), out _);
                WaitForEncodes(store);
                Frame(ctx, store, end: false);
                Assert.True(store.GrowCount > growsBefore, "the dropped frame grew the buffer");

                // The following frame redoes it all, so everything is on the device in the end.
                for (var i = 0; i < 20; i++)
                {
                    Frame(ctx, store);
                    if (!store.HasPending) break;
                }
                Assert.False(store.HasPending);

                var device = Readback(ctx, store, store.UsedWords);
                foreach (var c in "ABCDEFGH") AssertGlyphOnDevice(store, rasterizer, device, c);

                for (var i = 0; i < 4; i++) Frame(ctx, store);
                Assert.Equal(0, ctx.PendingDeferredDestroys);
            }
            finally { store.Dispose(); }
        });
    }

    [Fact]
    public void The_renderer_owns_one_store_and_uploads_it_in_its_frames()
    {
        Run(ctx =>
        {
            using var renderer = new VkRenderer(ctx, 128, 128);
            var store = renderer.SlugGlyphStore;
            Assert.Same(store, renderer.SlugGlyphStore);
            var id = Identity(renderer.GlyphRasterizer!, 'A');

            Assert.False(store.TryGet(FontPath, id, out _));
            Assert.True(renderer.SlugGlyphsPending);
            Assert.True(renderer.FontAtlasDirty, "a pending Slug glyph asks the loop for another frame");

            var sw = Stopwatch.StartNew();
            while (renderer.SlugGlyphsPending && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                WaitForEncodes(store);
                Assert.True(renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)));
                renderer.EndOffscreenFrame();
                ctx.WaitOffscreenFrameComplete();
            }
            Assert.False(renderer.SlugGlyphsPending);
            Assert.True(store.TryGet(FontPath, id, out var entry));
            Assert.False(entry.IsEmpty);
        });
    }

    [Fact]
    public void The_device_owns_the_slug_layouts_and_pool_room_for_storage_buffers()
    {
        Run(ctx =>
        {
            var device = ctx.GraphicsDevice;
            Assert.NotEqual(VkDescriptorSetLayout.Null, device.SlugDescriptorSetLayout);
            Assert.NotEqual(VkPipelineLayout.Null, device.SlugPipelineLayout);
            Assert.True(device.MaxStorageBufferRange >= 128u * 1024 * 1024, "the spec's minimum");

            // More sets than one pool holds: the chain must deliver storage-buffer sets across pools.
            var sets = new VkDescriptorSet[1100];
            for (var i = 0; i < sets.Length; i++) sets[i] = device.AllocateSlugDescriptorSet();
            Assert.Equal(sets.Length, sets.Distinct().Count());
            foreach (var s in sets) device.FreeSlugDescriptorSet(s);
        });
    }

    // Growth and a stream of uploads, run: the device counts every drain and queue wait the library makes, through the context
    // or not (VulkanDevice.DeviceWaits, read here as VulkanContext.DeviceWaits), and a store that grows twice and uploads
    // over many frames makes none. The source scan below stays as the second guard,
    // for a wait this run does not reach.
    [Fact]
    public void Growth_and_a_stream_of_uploads_make_no_device_wait()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            var store = new VkSlugGlyphStore(ctx, rasterizer, initialCapacityWords: 512, uploadBudgetWords: 1);
            try
            {
                var waitsBefore = ctx.DeviceWaits;
                const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@&%";
                Load(ctx, store, rasterizer, chars);
                for (var i = 0; i < 4; i++) Frame(ctx, store);

                Assert.True(store.GrowCount >= 2, "the run grew the buffer more than once");
                Assert.Equal(waitsBefore, ctx.DeviceWaits);
            }
            finally { store.Dispose(); }
        });
    }

    // The counter is the device's, so the guard above is not blind to a wait made through the device itself (or the
    // offscreen readback's queue wait): each is seen from the context.
    [Fact]
    public void The_wait_count_sees_waits_made_through_the_device_not_only_the_context()
    {
        Run(ctx =>
        {
            var before = ctx.DeviceWaits;
            ctx.GraphicsDevice.WaitIdle();
            Assert.Equal(before + 1, ctx.DeviceWaits);
            Assert.Equal(ctx.GraphicsDevice.DeviceWaits, ctx.DeviceWaits);

            ctx.TryWaitAllFramesIdle("test");
            Assert.Equal(before + 2, ctx.DeviceWaits);
        });
    }

    // A budget of one glyph spreads the uploads over many frames, each of which carries a staging buffer. They are
    // pooled: the store makes a handful, not one per frame, and the words that went through a reused one are right.
    [Fact]
    public void Upload_frames_reuse_their_staging_buffers()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            var store = new VkSlugGlyphStore(ctx, rasterizer, uploadBudgetWords: 1);
            try
            {
                const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
                var churnBefore = ctx.GraphicsDevice.ChurnCounters;
                Load(ctx, store, rasterizer, chars);
                for (var i = 0; i < 4; i++) Frame(ctx, store);

                Assert.Equal(0, store.GrowCount);
                Assert.True(store.StagingBuffersCreated >= 1);
                Assert.True(store.StagingBuffersCreated <= 6,
                    $"{store.StagingBuffersCreated} staging buffers for {chars.Length} glyphs, one frame each");

                var device = Readback(ctx, store, store.UsedWords);
                foreach (var c in chars) AssertGlyphOnDevice(store, rasterizer, device, c);
                Assert.Equal(0, ctx.PendingDeferredDestroys);

                // The device-wide count agrees: far fewer buffers made than the glyphs uploaded one by one
                // (the store's own buffer and the readback's are in it too).
                var created = ctx.GraphicsDevice.ChurnCounters.BuffersCreated - churnBefore.BuffersCreated;
                Assert.True(created < chars.Length / 2, $"{created} buffers created for {chars.Length} one-glyph uploads");
            }
            finally { store.Dispose(); }
        });
    }

    // An encode that outlasts Dispose's join (a slow face) must not take the process down when it ends: the queue is
    // not disposed under it. Before, the worker's next ask of a disposed BlockingCollection threw on a thread nobody
    // catches, which ends the process (here: the test host).
    [Fact]
    public void An_encode_that_outlasts_dispose_ends_quietly()
    {
        Run(ctx =>
        {
            var rasterizer = new ManagedFontRasterizer();
            using var entered = new System.Threading.ManualResetEventSlim();
            using var gate = new System.Threading.ManualResetEventSlim();
            var store = new VkSlugGlyphStore(ctx, rasterizer, workerCount: 1, joinTimeout: TimeSpan.FromMilliseconds(50),
                beforeEncode: _ =>
                {
                    entered.Set();
                    gate.Wait(TimeSpan.FromSeconds(30));
                });

            Assert.False(store.TryGet(FontPath, Identity(rasterizer, 'A'), out _));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the worker took the glyph");

            store.Dispose(); // the join times out: the worker is held inside its encode
            gate.Set();

            // The worker finishes its glyph and goes back for the next one; give it every chance to throw.
            var sw = Stopwatch.StartNew();
            while (store.EncodesInFlight > 0 && sw.Elapsed < TimeSpan.FromSeconds(30))
                System.Threading.Thread.Sleep(1);
            Assert.Equal(0, store.EncodesInFlight);
            System.Threading.Thread.Sleep(250);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        });
    }

    // The store must never stall the device: growth and upload ride the frame's command buffer and the deferred
    // destroy schedule. Growth_and_a_stream_of_uploads_make_no_device_wait runs that; this reads the source for the rest.
    [Fact]
    [Trait("Category", "Invariant")]
    public void The_store_never_waits_for_the_device_or_the_queue()
    {
        var source = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "SdlVulkan.Renderer", "VkSlugGlyphStore.cs");
        if (!File.Exists(source))
        {
            Assert.Skip($"source not at {source} (a build away from its checkout)");
            return;
        }
        // Code only: the comments say what the store does NOT do, and may name it.
        var code = Regex.Replace(File.ReadAllText(source), @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (var forbidden in new[] { "WaitIdle", "TryWaitAllFramesIdle", "TryWaitPriorFramesIdle", "TryDrainDevice", "vkWaitForFences" })
            Assert.DoesNotContain(forbidden, code);
    }
}
