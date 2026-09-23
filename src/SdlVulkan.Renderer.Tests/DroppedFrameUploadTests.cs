using System;
using System.IO;
using System.Text;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Work recorded into a frame is provisional until the frame reaches the queue
/// (<see cref="VulkanContext.OnFrameDropped"/>). The contract itself needs no fault and runs everywhere;
/// the uploads it exists for are driven through a faked rejection in <see cref="DroppedFrameUploadTests"/>.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class FrameRollbackContractTests(OffscreenGpuFixture gpu)
{
    private VulkanContext? Context()
    {
        if (gpu.Context is not { } ctx) return null;
        ctx.ResizeOffscreen(16, 16);
        return ctx;
    }

    private static void EndEmptyFrame(VulkanContext ctx, VkCommandBuffer cmd)
    {
        ctx.BeginOffscreenRenderPass(cmd, 0, 0, 0, 1);
        ctx.EndOffscreenFrame(cmd);
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public void AnAcceptedFrameRunsNoRollback()
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var ran = 0;
        var cmd = ctx.BeginOffscreenFrame();
        ctx.OnFrameDropped(cmd, () => ran++).ShouldBeTrue();
        EndEmptyFrame(ctx, cmd);

        // Nor later: an accepted frame's rollbacks are gone, not merely deferred.
        EndEmptyFrame(ctx, ctx.BeginOffscreenFrame());
        ran.ShouldBe(0);
    }

    [Fact]
    public void AFrameBegunAndNeverEndedIsUndoneWhenTheNextOneStarts()
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var ran = 0;
        var abandoned = ctx.BeginOffscreenFrame();
        ctx.OnFrameDropped(abandoned, () => ran++).ShouldBeTrue();

        // An exception between begin and end leaves exactly this: the next frame resets the command
        // buffer that held the work.
        var next = ctx.BeginOffscreenFrame();
        ran.ShouldBe(1);

        EndEmptyFrame(ctx, next);
        ran.ShouldBe(1);
    }

    [Fact]
    public void AQueuedTextureUploadIsRecordedAtTheStartOfTheNextFrame()
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        // Solid blue, B8G8R8A8.
        var pixels = new byte[2 * 2 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255; pixels[i + 1] = 0; pixels[i + 2] = 0; pixels[i + 3] = 255;
        }
        using var texture = VkTexture.CreateDeferred(ctx, pixels, 2, 2);
        using var renderer = new VkRenderer(ctx, 16, 16);

        // Queued from outside any frame, as a draw path's cache adopting a decoded picture would be.
        ctx.QueueTextureUpload(texture);
        texture.IsUploaded.ShouldBeFalse();

        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        // Recorded by the frame's start, before this frame draws anything.
        texture.IsUploaded.ShouldBeTrue();
        renderer.DrawTexture(texture.DescriptorSet, 0, 0, 16, 16);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        var centre = (8 * 16 + 8) * 4;
        new RGBAColor32(rgba[centre], rgba[centre + 1], rgba[centre + 2], rgba[centre + 3])
            .ShouldBe(new RGBAColor32(0, 0, 255, 255));
    }

    [Fact]
    public void ACachedLayerLeftOpenByAFrameThatNeverEndedDoesNotOutliveIt()
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        var black = new RGBAColor32(0, 0, 0, 255);
        var ink = new RGBAColor32(255, 255, 0, 255);
        using var renderer = new VkRenderer(ctx, 16, 16);
        renderer.EnsureCachedLayerTargets(8, 8).ShouldBeTrue();
        try
        {
            // The layer is opened where it must be, before the frame's own render pass (OnPreRenderPass), and
            // the drawing inside it throws: the frame is left begun, with the layer's pass open, and nothing
            // ends either.
            renderer.OnPreRenderPass = _ =>
            {
                renderer.BeginCachedLayer(8, 8, black).ShouldBeTrue();
                throw new InvalidOperationException("drawing into the layer failed");
            };
            Should.Throw<InvalidOperationException>(() => renderer.BeginOffscreenFrame(black));

            // The next frame may open the layer again, and draws at ITS scale, not the 8 x 8 layer's: a fill of
            // the whole frame reaches the far corner.
            var reopened = false;
            renderer.OnPreRenderPass = _ =>
            {
                reopened = renderer.BeginCachedLayer(8, 8, black);
                renderer.EndCachedLayer();
            };
            renderer.BeginOffscreenFrame(black).ShouldBeTrue();
            reopened.ShouldBeTrue();
            renderer.FillRectangle(new RectInt(new PointInt(16, 16), new PointInt(0, 0)), ink);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();

            var rgba = ctx.ReadbackOffscreenRgba();
            var corner = (15 * 16 + 15) * 4;
            new RGBAColor32(rgba[corner], rgba[corner + 1], rgba[corner + 2], rgba[corner + 3]).ShouldBe(ink);
        }
        finally
        {
            // The targets belong to the shared fixture's context and outlive this renderer: left at 8 x 8,
            // a later test asking for a larger layer is refused (EnsureCachedLayerTargets answers false).
            renderer.ReleaseCachedLayerTargets();
        }
    }

    [Fact]
    public void AOneShotCommandBufferRegistersNothing()
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        // A one-shot's submit is synchronous, so its caller sees any failure itself; a rollback there
        // would run against whichever frame happened to be recorded next.
        bool? registered = null;
        ctx.ExecuteOneShot(cmd => registered = ctx.OnFrameDropped(cmd, static () => { }));
        registered.ShouldBe(false);
    }
}

#if DEBUG
/// <summary>
/// The uploads a dropped frame used to lose for good, reproduced with a faked rejection
/// (<see cref="GpuFaultInjection"/>): first seen live, as a notification written during a rejected-submit
/// storm that was missing every letter first drawn then. Each test renders the same thing twice, once on
/// a clean path and once through a dropped frame, and requires the two to match.
/// DEBUG-only, like the fault switch.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class DroppedFrameUploadTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 96;
    private const uint Height = 64;
    private const float Size = 36f;

    private static readonly RGBAColor32 Black = new RGBAColor32(0, 0, 0, 255);
    private static readonly RGBAColor32 White = new RGBAColor32(255, 255, 255, 255);
    private static readonly Rune[] Glyphs = [new Rune('W'), new Rune('y')];

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private VulkanContext? Context()
    {
        if (gpu.Context is not { } ctx) return null;
        ctx.ResizeOffscreen(Width, Height);
        ctx.FaultInjection.Clear();
        return ctx;
    }

    /// <summary>A renderer whose glyphs are rasterized before each frame's flush, so a frame's flush is the
    /// one that uploads them (the draw path never rasterizes on the render thread).</summary>
    private static VkRenderer GlyphRenderer(VulkanContext ctx, bool sdf)
    {
        var renderer = new VkRenderer(ctx, Width, Height);
        renderer.OnPreFlush = () =>
        {
            foreach (var glyph in Glyphs)
            {
                if (sdf) renderer.PreWarmSdfGlyph(FontPath, Size, glyph);
                else renderer.PreWarmGlyph(FontPath, Size, glyph);
            }
        };
        return renderer;
    }

    private static byte[] RenderGlyphs(VulkanContext ctx, VkRenderer renderer, bool sdf)
    {
        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        if (sdf) renderer.BeginSdfGlyphBatch(White, Size);
        else renderer.BeginGlyphBatch(White);
        var x = 10f;
        foreach (var glyph in Glyphs)
        {
            if (sdf) renderer.AddBatchedSdfGlyphAtBaseline(FontPath, glyph, -1, baselineX: x, baselineY: 46f);
            else renderer.AddBatchedGlyphAtBaseline(FontPath, Size, glyph, -1, baselineX: x, baselineY: 46f);
            x += 40f;
        }
        renderer.EndGlyphBatch();
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        return ctx.ReadbackOffscreenRgba();
    }

    private static int LitPixels(byte[] rgba)
    {
        var lit = 0;
        for (var i = 0; i < rgba.Length; i += 4)
            if (rgba[i] > 32) lit++;
        return lit;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGlyphWhoseUploadRodeADroppedFrameIsUploadedByTheNext(bool sdf)
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        byte[] expected;
        using (var clean = GlyphRenderer(ctx, sdf))
            expected = RenderGlyphs(ctx, clean, sdf);
        // Two blank frames would match each other, which is not the claim.
        LitPixels(expected).ShouldBeGreaterThan(50);

        var fault = ctx.FaultInjection;
        using var renderer = GlyphRenderer(ctx, sdf);
        try
        {
            // The frame that rasterizes and flushes the glyphs is refused, and takes the upload with it.
            fault.RejectSubmits();
            Should.Throw<VkException>(() => RenderGlyphs(ctx, renderer, sdf));
            fault.Clear();

            // Nothing new to rasterize now; what this frame shows depends on the upload being owed again.
            var actual = RenderGlyphs(ctx, renderer, sdf);
            LitPixels(actual).ShouldBe(LitPixels(expected));
            actual.ShouldBe(expected);
        }
        finally
        {
            fault.Clear();
        }
    }

    [Fact]
    public void ADeferredTextureWhoseUploadRodeADroppedFrameArrivesOnTheNextWithoutBeingAskedAgain()
    {
        if (Context() is not { } ctx)
        {
            Assert.Skip("No Vulkan ICD available on this host.");
            return;
        }

        // Solid green, B8G8R8A8.
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0; pixels[i + 1] = 255; pixels[i + 2] = 0; pixels[i + 3] = 255;
        }

        var fault = ctx.FaultInjection;
        using var texture = VkTexture.CreateDeferred(ctx, pixels, 4, 4);
        using var renderer = new VkRenderer(ctx, Width, Height);
        // Recorded ONCE, the way a consumer uploads a texture it just made. Nothing here asks again.
        var asked = false;
        renderer.OnPreRenderPass = cmd =>
        {
            if (asked) return;
            asked = true;
            texture.RecordUpload(cmd);
        };

        byte[] Draw()
        {
            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            renderer.DrawTexture(texture.DescriptorSet, 0, 0, Width, Height);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            return ctx.ReadbackOffscreenRgba();
        }

        try
        {
            fault.RejectSubmits();
            Should.Throw<VkException>(() => Draw());
            // The image was never written: saying otherwise is what left it blank for good.
            texture.IsUploaded.ShouldBeFalse();
            fault.Clear();

            var rgba = Draw();
            texture.IsUploaded.ShouldBeTrue();
            var centre = (int)((Height / 2 * Width + Width / 2) * 4);
            new RGBAColor32(rgba[centre], rgba[centre + 1], rgba[centre + 2], rgba[centre + 3])
                .ShouldBe(new RGBAColor32(0, 255, 0, 255));
        }
        finally
        {
            fault.Clear();
        }
    }
}
#endif
