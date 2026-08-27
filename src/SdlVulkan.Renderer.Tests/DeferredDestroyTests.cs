using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Deferred destruction's contract: an object handed to <see cref="VulkanContext.DeferDestroy(Action)"/>
/// is destroyed only once every frame that could reference it has retired, which is the frame being
/// recorded and every frame in flight, and NOT before. Disposing a <see cref="VkTexture"/> in the same
/// frame that drew it is the shape that used to fault the GPU (a frame submitted against a destroyed
/// view: "vkCmdBindDescriptorSets(): ... invalid state ... VkImageView was destroyed", then
/// VK_ERROR_DEVICE_LOST), so that is the sequence run here, once on the plain fixture for the schedule
/// and once under the validation layer for the silence.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class DeferredDestroyTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int TexDim = 8;
    private static readonly RGBAColor32 Black = new RGBAColor32(0, 0, 0, 255);
    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(60);

    [Fact]
    public void ADeferredDestroyRunsOnlyAfterEveryFrameThatCouldReferenceItHasRetired()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);
        var texture = VkTexture.CreateFromBgra(ctx, SolidTexture(255, 0, 0), TexDim, TexDim);

        var destroyed = 0;

        // Frame 1 draws the texture and then disposes it, in that order, inside the same frame: the
        // command buffer already holds the descriptor set when the dispose runs.
        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        renderer.DrawTexture(texture.DescriptorSet, 0f, 0f, Size, Size);
        texture.Dispose();
        renderer.DeferDestroy(() => destroyed++);
        renderer.PendingDeferredDestroys.ShouldBeGreaterThanOrEqualTo(2, "the texture's handles and the marker are queued, not destroyed");
        destroyed.ShouldBe(0, "nothing may be destroyed while the frame that bound it is still being recorded");
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        // The entry is stamped with frame 1's ordinal and retires when the ordinal has advanced by
        // MaxFramesInFlight, i.e. at the BeginFrame of frame 1 + MaxFramesInFlight. Every frame before
        // that must leave it pending, however idle the GPU actually is.
        for (var frame = 2; frame <= VulkanContext.MaxFramesInFlight; frame++)
        {
            renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
            destroyed.ShouldBe(0, $"frame {frame} of {VulkanContext.MaxFramesInFlight} in flight: the frame that bound the texture has not provably retired");
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }

        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        destroyed.ShouldBe(1, "the fence wait that opened this frame retired the binding frame, so the destroy runs here");
        renderer.PendingDeferredDestroys.ShouldBe(0);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
    }

    [Fact]
    public async Task DisposingATextureInTheFrameThatDrewItIsSilentUnderTheValidationLayer()
    {
        if (!ValidatedOffscreen.TryCreate(Size, Size, out var ctx, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        var wedged = false;
        try
        {
            var run = Task.Run(Sequence(ctx!), TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(run, Task.Delay(Deadman, TestContext.Current.CancellationToken)) == run;
            if (!finished)
            {
                wedged = true;
                Assert.Fail($"deadman: the sequence did not finish within {Deadman.TotalSeconds:0}s, possible GPU wedge. " +
                            $"Validation messages so far:\n{ValidatedOffscreen.DumpMessages()}");
            }
            await run;

            var errors = ValidatedOffscreen.Messages.Where(ValidatedOffscreen.IsError).ToArray();
            Assert.True(errors.Length == 0,
                $"the validation layer reported {errors.Length} error(s) while textures were disposed in the frames that drew them; " +
                $"a destroy that is not deferred reads as 'invalid state ... was destroyed':\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            if (!wedged)
            {
                ValidatedOffscreen.Destroy(ctx, messenger, api);
            }
        }
    }

    // Draw a fresh texture, dispose it in the same frame, and repeat over more frames than there are in
    // flight, so every slot sees a texture retire behind it while a newer one is bound.
    private static Action Sequence(VulkanContext ctx) => () =>
    {
        using var renderer = new VkRenderer(ctx, Size, Size);
        for (var frame = 0; frame < 2 * VulkanContext.MaxFramesInFlight + 2; frame++)
        {
            var texture = VkTexture.CreateFromBgra(ctx, SolidTexture((byte)(40 * frame), 128, 255), TexDim, TexDim);
            if (!renderer.BeginOffscreenFrame(Black))
            {
                texture.Dispose();
                continue;
            }
            renderer.DrawTexture(texture.DescriptorSet, 0f, 0f, Size, Size);
            texture.Dispose();
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
        }
        // Whatever is still pending is flushed by the context's own teardown; nothing here must wait.
    };

    private static byte[] SolidTexture(byte r, byte g, byte b)
    {
        var data = new byte[TexDim * TexDim * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
            data[i + 3] = 255;
        }
        return data;
    }
}
