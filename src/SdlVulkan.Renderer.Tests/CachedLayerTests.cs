using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The cached layer's contract: content rendered into a slot survives into LATER frames, so a frame
/// that changed nothing but its chrome can blit instead of re-rendering. That persistence is the whole
/// reason the class exists, and it is the one property a single-frame test cannot see.
/// </summary>
/// <remarks>
/// <para>The third frame is the decisive one. It comes back round to the first slot, renders NOTHING
/// into the layer, and blits it anyway -- and the two colour blocks still have to be there. A cache
/// that quietly re-rendered every frame, or whose image did not outlive the render pass that wrote it,
/// passes the first two frames and fails only here.</para>
/// <para>The second frame pins the per-slot rule that makes the design safe rather than merely fast:
/// a second target exists, has never been rendered, and is therefore NOT legal to sample yet. Sampling
/// it regardless is undefined content rather than an error -- nothing would throw and nothing would
/// warn -- which is exactly why <see cref="VkRenderer.IsCachedLayerSlotRendered"/> is part of the API
/// instead of being left to the caller to remember.</para>
/// </remarks>
[Collection("OffscreenGpu")]
public sealed class CachedLayerTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Half = (int)Size / 2;
    private const int Quarter = (int)Size / 4;
    private const int ThreeQuarters = 3 * (int)Size / 4;

    private static readonly RGBAColor32 Red = new RGBAColor32(255, 0, 0, 255);
    private static readonly RGBAColor32 Green = new RGBAColor32(0, 255, 0, 255);
    private static readonly RGBAColor32 Black = new RGBAColor32(0, 0, 0, 255);

    [Fact]
    public void ACachedSlotStillDrawsOnAFrameThatRenderedNothingIntoIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);

        // The offscreen context belongs to the shared collection fixture; never dispose it here.
        using var renderer = new VkRenderer(ctx, Size, Size);

        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();
        renderer.CachedLayerTargetReady.ShouldBeTrue();
        renderer.CachedLayerSlotCount.ShouldBe(VulkanContext.MaxFramesInFlight,
            "there must be one target per frame in flight, or a re-render races the frame still sampling it");

        var slotsSeen = new List<int>();
        var layerRenders = 0;
        var renderLayerThisFrame = true;

        // Exactly the production shape: the layer is recorded before the main render pass opens,
        // because render passes cannot nest.
        renderer.OnPreRenderPass += _ =>
        {
            slotsSeen.Add(renderer.CachedLayerSlot);
            if (!renderLayerThisFrame) return;

            renderer.BeginCachedLayer(Size, Size, Black).ShouldBeTrue();
            renderer.FillRectangle(new RectInt(new PointInt(Half, (int)Size), new PointInt(0, 0)), Red);
            renderer.FillRectangle(new RectInt(new PointInt((int)Size, (int)Size), new PointInt(Half, 0)), Green);
            renderer.EndCachedLayer();
            layerRenders++;
        };

        // Frame 1: render the layer, then blit it.
        var first = BlitFrame(renderer, ctx);
        layerRenders.ShouldBe(1);
        ChannelsAt(first, Quarter, Half).ShouldBe((255, 0, 0), "left half of the cached layer");
        ChannelsAt(first, ThreeQuarters, Half).ShouldBe((0, 255, 0), "right half of the cached layer");

        // Frame 2: a different slot, which has never been rendered.
        var secondSlot = (slotsSeen[0] + 1) % VulkanContext.MaxFramesInFlight;
        renderer.IsCachedLayerSlotRendered(secondSlot).ShouldBeFalse(
            "an unrendered slot is still in Undefined layout, so it must not be reported sampleable");
        var second = BlitFrame(renderer, ctx);
        layerRenders.ShouldBe(2);
        slotsSeen[1].ShouldNotBe(slotsSeen[0], "consecutive frames must take different slots");
        ChannelsAt(second, Quarter, Half).ShouldBe((255, 0, 0));

        // Frame 3, the point of all this: back to the first slot, render NOTHING into the layer, and
        // blit it regardless.
        renderLayerThisFrame = false;
        var third = BlitFrame(renderer, ctx);
        layerRenders.ShouldBe(2, "the third frame must not have re-rendered the layer");
        slotsSeen[2].ShouldBe(slotsSeen[0], "the slots must cycle, so frame 3 reuses frame 1's");
        renderer.IsCachedLayerSlotRendered(slotsSeen[2]).ShouldBeTrue();
        ChannelsAt(third, Quarter, Half)
            .ShouldBe((255, 0, 0), "a cached slot must still hold its content on a frame that rendered none");
        ChannelsAt(third, ThreeQuarters, Half).ShouldBe((0, 255, 0));
    }

    [Fact]
    public void CapacityIsFixedAndASubRectMayNotExceedIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);

        renderer.EnsureCachedLayerTargets(Size, Size).ShouldBeTrue();

        // Already allocated: a request within capacity is satisfied, a larger one is refused rather
        // than silently reallocating on the render thread.
        renderer.EnsureCachedLayerTargets(Size / 2, Size / 2).ShouldBeTrue();
        renderer.EnsureCachedLayerTargets(Size * 2, Size).ShouldBeFalse(
            "growing capacity needs ReleaseCachedLayerTargets first, which drains before freeing");

        var refused = false;
        renderer.OnPreRenderPass += _ =>
        {
            // Oversize: records nothing and says so, rather than beginning a pass whose render area
            // exceeds the framebuffer.
            refused = !renderer.BeginCachedLayer(Size * 2, Size, Black);
        };
        BlitFrame(renderer, ctx, blitLayer: false);
        refused.ShouldBeTrue();

        // Release drains and tears down, so the capacity can then change.
        renderer.ReleaseCachedLayerTargets();
        renderer.CachedLayerTargetReady.ShouldBeFalse();
        renderer.EnsureCachedLayerTargets(Size * 2, Size).ShouldBeTrue();
        renderer.ReleaseCachedLayerTargets();
    }

    /// <summary>Runs one frame that blits the current cached-layer slot over the whole target.</summary>
    private static byte[] BlitFrame(VkRenderer renderer, VulkanContext ctx, bool blitLayer = true)
    {
        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        if (blitLayer)
        {
            var slot = renderer.CachedLayerSlot;
            renderer.DrawTexture(renderer.CachedLayerDescriptorSet(slot), 0f, 0f, Size, Size);
        }
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        rgba.Length.ShouldBe((int)(Size * Size * 4));
        return rgba;
    }

    private static (int R, int G, int B) ChannelsAt(byte[] rgba, int x, int y)
    {
        var i = (y * (int)Size + x) * 4;
        // The blocks are pure; only linear filtering at the seam lands in between, and the sample
        // points sit well clear of it.
        static int Snap(byte v) => v > 127 ? 255 : 0;
        return (Snap(rgba[i]), Snap(rgba[i + 1]), Snap(rgba[i + 2]));
    }
}
