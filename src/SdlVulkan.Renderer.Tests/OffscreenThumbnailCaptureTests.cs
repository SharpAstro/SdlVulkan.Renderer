using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// That a live-device thumbnail capture completes on an offscreen context.
///
/// <para>The capture's copy rides the frame fence, and the swapchain <c>BeginFrame</c> snapshots it
/// once its fence has been waited. <c>BeginOffscreenFrame</c> mirrors that frame's contract (the wait,
/// GPU timing, deferred destroys) but left that one step out, so on an offscreen context a capture was
/// recorded and never came back: <see cref="VkRenderer.ThumbnailCaptureBusy"/> stayed true and no later
/// capture could start. A headless consumer could not use the capture at all, nor test anything
/// built on it.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class OffscreenThumbnailCaptureTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const uint CaptureSize = 16;

    [Fact]
    public void ACaptureRecordedOffscreenComesBack()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);
        renderer.EnsureThumbnailTarget(CaptureSize, CaptureSize).ShouldBeTrue();

        // A colour the main frame never uses, so the readback can only be the capture's own clear.
        var green = new RGBAColor32(20, 200, 40, 255);
        var record = true;
        renderer.OnPreRenderPass = _ =>
        {
            if (!record) return;
            record = false;
            renderer.BeginThumbnailCapture(CaptureSize, CaptureSize, green).ShouldBeTrue();
            renderer.EndThumbnailCapture();
        };

        byte[]? rgba = null;
        int width = 0, height = 0;
        try
        {
            // The copy is snapshotted when its fence index comes round again, MaxFramesInFlight frames on.
            for (var frame = 0; frame < 8 && rgba is null; frame++)
            {
                renderer.BeginOffscreenFrame(new RGBAColor32(255, 255, 255, 255)).ShouldBeTrue();
                renderer.EndOffscreenFrame();
                ctx.WaitOffscreenFrameComplete();
                if (renderer.TryGetThumbnailCapture(out var got, out var w, out var h))
                    (rgba, width, height) = (got, w, h);
            }
        }
        finally
        {
            renderer.OnPreRenderPass = null;
        }

        rgba.ShouldNotBeNull("the capture never came back from an offscreen frame");
        (width, height).ShouldBe(((int)CaptureSize, (int)CaptureSize));
        var centre = ((height / 2) * width + width / 2) * 4;
        (rgba[centre], rgba[centre + 1], rgba[centre + 2]).ShouldBe(((byte)20, (byte)200, (byte)40));
        renderer.ThumbnailCaptureBusy.ShouldBeFalse();
    }
}
