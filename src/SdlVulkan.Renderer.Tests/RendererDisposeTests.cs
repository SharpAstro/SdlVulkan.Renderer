using System;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Disposing a <see cref="VkRenderer"/> right after a frame it submitted: the renderer's pipelines and font atlases go in its
/// own Dispose, which every host calls BEFORE the context's (whose drain used to be the only one), so a frame still in flight
/// held pipelines being destroyed. The validation layer reads it as "vkDestroyPipeline(): can't be called on VkPipeline ...
/// that is currently in use by VkCommandBuffer" (VUID-vkDestroyPipeline-pipeline-00765), as TransparencyProbe's buddy did on
/// an Adreno X1-85 at exit.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class RendererDisposeTests
{
    private const uint Size = 64;
    private static readonly RGBAColor32 Black = new RGBAColor32(0, 0, 0, 255);
    private static readonly TimeSpan Deadman = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task DisposingTheRendererRightAfterASubmittedFrameIsSilentUnderTheValidationLayer()
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
                $"the validation layer reported {errors.Length} error(s) when the renderer was disposed behind a frame in flight; " +
                $"a pipeline destroyed under it reads as 'currently in use by VkCommandBuffer':\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            if (!wedged)
            {
                ValidatedOffscreen.Destroy(ctx, messenger, api);
            }
        }
    }

    // A frame that binds a pipeline, submitted and NOT waited for, then the renderer's Dispose, as a host's teardown does.
    private static Action Sequence(VulkanContext ctx) => () =>
    {
        var renderer = new VkRenderer(ctx, Size, Size);
        Assert.True(renderer.BeginOffscreenFrame(Black));
        renderer.FillEllipse(new RectInt(new PointInt(48, 48), new PointInt(16, 16)), new RGBAColor32(255, 128, 0, 255));
        renderer.EndOffscreenFrame();
        renderer.Dispose();
    };
}
