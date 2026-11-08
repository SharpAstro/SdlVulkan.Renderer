using System;
using System.IO;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The HDR path's frame, through an offscreen context made with <c>hdr: true</c> (the same SDR layer and composite pass as
/// an scRGB swapchain, on a float target the tests can read): an SDR UI scene comes out of the composite equal to the plain
/// SDR frame, <see cref="VkRenderer.DrawHdrImage"/> writes scRGB clamped to the display's peak beneath what is drawn
/// after it, and an SDR context has none of the HDR state at all.
/// <para>The HDR context shares the fixture's device (the offscreen device's passes are B8G8R8A8 on either path, which is
/// the point), so the suite still makes one instance. Skips when there is no Vulkan ICD.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class HdrCompositeTests(OffscreenGpuFixture gpu)
{
    private const uint Width = 160;
    private const uint Height = 96;

    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf");

    private static RectInt Rect(int x0, int y0, int x1, int y1) => new(new PointInt(x1, y1), new PointInt(x0, y0));

    private static VulkanContext HdrContext(VulkanContext shared, uint width, uint height)
        => VulkanContext.CreateOffscreenOnDevice(shared.GraphicsDevice, ownsDevice: false, width, height, hdr: true);

    /// <summary>An SDR UI scene: boxes (one half transparent), a rounded box, an ellipse, lines, text and an image.</summary>
    private static void Scene(VkRenderer r, VkTexture image)
    {
        r.FillRectangle(Rect(8, 8, 60, 30), new RGBAColor32(200, 40, 40, 255));
        r.FillRectangle(Rect(40, 20, 100, 44), new RGBAColor32(40, 90, 220, 128));
        r.FillRoundedRectangle(Rect(70, 50, 150, 88), new RGBAColor32(230, 230, 235, 255), 8f);
        r.FillEllipse(Rect(10, 50, 60, 90), new RGBAColor32(60, 180, 90, 255));
        r.DrawLine(100, 10, 150, 40, new RGBAColor32(250, 200, 40, 255), 2);
        r.DrawText("HDR 12 Ag", FontPath, 14f, new RGBAColor32(20, 20, 30, 255),
            Rect(70, 50, 150, 88), TextAlign.Center, TextAlign.Center);
        r.DrawTexture(image.DescriptorSet, 112, 6, 40, 40);
    }

    // An 8x8 ramp, BGRA, so the image has smooth mid-tones as well as the primaries.
    private static byte[] Ramp()
    {
        var px = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var o = (y * 8 + x) * 4;
            px[o] = (byte)(x * 36);          // B
            px[o + 1] = (byte)(y * 36);      // G
            px[o + 2] = (byte)(255 - x * 30); // R
            px[o + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void AnSdrScene_ThroughTheComposite_EqualsThePlainSdrFrame_WithinOneStep()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        shared.ResizeOffscreen(Width, Height);
        using var sdrRenderer = new VkRenderer(shared, Width, Height);
        using var image = VkTexture.CreateFromBgra(shared, Ramp(), 8, 8);
        var clear = new RGBAColor32(24, 28, 36, 255);

        sdrRenderer.BeginOffscreenFrame(clear).ShouldBeTrue();
        Scene(sdrRenderer, image);
        sdrRenderer.EndOffscreenFrame();
        shared.WaitOffscreenFrameComplete();
        var sdr = shared.ReadbackOffscreenRgba();

        using var hdrCtx = HdrContext(shared, Width, Height);
        hdrCtx.HdrActive.ShouldBeTrue();
        hdrCtx.SdrWhiteScRgb = 1f;
        using var hdrRenderer = new VkRenderer(hdrCtx, Width, Height);
        hdrRenderer.BeginOffscreenFrame(clear).ShouldBeTrue();
        Scene(hdrRenderer, image);
        hdrRenderer.EndOffscreenFrame();
        hdrCtx.WaitOffscreenFrameComplete();
        var scRgb = hdrCtx.ReadbackOffscreenScRgb();
        var layer = hdrCtx.ReadbackOffscreenRgba();

        scRgb.Length.ShouldBe((int)(Width * Height * 4));
        // The layer IS the plain SDR frame: every pipeline drew into it exactly as it draws into an SDR target.
        layer.ShouldBe(sdr, "the HDR context renders the frame bit-identically into its SDR layer");

        // The composite, decoded on the CPU with SDR white 1.0 (sRGB encode of the linear value), is the SDR frame.
        var lit = 0;
        var worst = 0;
        for (var i = 0; i < (int)(Width * Height); i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var encoded = (int)MathF.Round(HdrColor.SrgbInverseEotf(scRgb[i * 4 + c]) * 255f);
                var diff = Math.Abs(encoded - sdr[i * 4 + c]);
                worst = Math.Max(worst, diff);
                if (diff > 1)
                    Assert.Fail($"pixel {i % Width},{i / Width} channel {c}: composite decodes to {encoded}, SDR frame has {sdr[i * 4 + c]}");
            }
            scRgb[i * 4 + 3].ShouldBe(1f, 0.001f, "an opaque frame composites opaque");
            if (sdr[i * 4] != clear.Red) lit++;
        }
        lit.ShouldBeGreaterThan(1500, "the scene has to draw something for the comparison to mean anything");
        worst.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void TheCompositeScalesTheLayerToSdrWhite()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        using var hdrCtx = HdrContext(shared, 16, 16);
        hdrCtx.SdrWhiteScRgb = 2.5f;
        using var renderer = new VkRenderer(hdrCtx, 16, 16);
        renderer.BeginOffscreenFrame(new RGBAColor32(255, 255, 255, 255)).ShouldBeTrue();
        renderer.EndOffscreenFrame();
        hdrCtx.WaitOffscreenFrameComplete();
        var scRgb = hdrCtx.ReadbackOffscreenScRgb();

        // sRGB white is linear 1.0, which the composite puts at SDR white.
        scRgb[0].ShouldBe(2.5f, 0.01f);
        scRgb[1].ShouldBe(2.5f, 0.01f);
        scRgb[2].ShouldBe(2.5f, 0.01f);

        // The float target follows a resize (it is rebuilt with the SDR layer), and SDR white is read per frame.
        hdrCtx.ResizeOffscreen(32, 8);
        hdrCtx.HdrActive.ShouldBeTrue();
        hdrCtx.SdrWhiteScRgb = 1f;
        renderer.BeginOffscreenFrame(new RGBAColor32(255, 255, 255, 255)).ShouldBeTrue();
        renderer.EndOffscreenFrame();
        hdrCtx.WaitOffscreenFrameComplete();
        var resized = hdrCtx.ReadbackOffscreenScRgb();
        resized.Length.ShouldBe(32 * 8 * 4);
        resized[0].ShouldBe(1f, 0.01f);
    }

    [Fact]
    public void DrawHdrImage_AboveSdrWhite_ReadsBackScRgbClampedToThePeak_BeneathWhatIsDrawnAfter()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        const uint size = 48;
        using var hdrCtx = HdrContext(shared, size, size);
        hdrCtx.SdrWhiteScRgb = 1f;
        hdrCtx.HdrHeadroom = 2f;                       // the display reaches twice SDR white: a peak of 2.0 scRGB
        hdrCtx.HdrPeakScRgb.ShouldBe(2f);
        using var renderer = new VkRenderer(hdrCtx, size, size);
        hdrCtx.HdrCompositeImagePipeline.ShouldBe(VkPipeline.Null, "no HDR image pipeline until one is drawn");

        // Scene-linear (1.0 = 203 cd/m2): red far above the peak, green under it, blue low.
        using var hdr = VkTexture.CreateFromRgbaFloat(shared, [4f, 0.5f, 0.1f, 1f], 1, 1);
        var scale = HdrColor.NitsScale();              // 203 / 80
        var halfRed = new RGBAColor32(255, 0, 0, 128);

        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        // Drawn BEFORE the image, over its rectangle: the image covers it (painter's order).
        renderer.FillRectangle(Rect(0, 0, 16, 16), new RGBAColor32(0, 255, 0, 255));
        renderer.DrawHdrImage(hdr, 0f, 0f, 32f, 16f, HdrPrimaries.Bt709);
        // Drawn AFTER it, half transparent over its right half: lies over the image.
        renderer.FillRectangle(Rect(16, 0, 32, 16), halfRed);
        // A clipped image: only its left 8 pixels may show.
        renderer.SetScissor(0, 24, 8, 16);
        renderer.DrawHdrImage(hdr, 0f, 24f, 24f, 16f, HdrPrimaries.Bt709);
        renderer.ResetScissor();
        renderer.EndOffscreenFrame();
        hdrCtx.WaitOffscreenFrameComplete();
        var scRgb = hdrCtx.ReadbackOffscreenScRgb();
        var layer = hdrCtx.ReadbackOffscreenRgba();

        hdrCtx.HdrCompositeImagePipeline.ShouldNotBe(VkPipeline.Null);
        float[] At(int x, int y)
        {
            var o = (y * (int)size + x) * 4;
            return [scRgb[o], scRgb[o + 1], scRgb[o + 2], scRgb[o + 3]];
        }

        // The image alone: red is 4 * 2.5375 = 10.15, clamped to the peak 2.0; green 1.27; blue 0.2538.
        var plain = At(8, 8);
        plain[0].ShouldBe(2f, 0.02f, "red clamps to the display peak, not to SDR white and not to 10.15");
        plain[1].ShouldBe(0.5f * scale, 0.02f);
        plain[2].ShouldBe(0.1f * scale, 0.01f);
        plain[3].ShouldBe(1f, 0.001f);
        // It left alpha 0 in its rectangle of the SDR layer, which is what lets it show through the composite.
        layer[(8 * (int)size + 8) * 4 + 3].ShouldBe((byte)0);

        // The half-transparent red drawn after it: 0.502 of red SDR white over the image, so the image keeps 0.498.
        var a = 128f / 255f;
        var over = At(24, 8);
        over[0].ShouldBe(2f * (1f - a) + a, 0.02f);
        over[1].ShouldBe(0.5f * scale * (1f - a), 0.02f);
        over[2].ShouldBe(0.1f * scale * (1f - a), 0.02f);

        // Negative colour is never produced from BT.709 content, and nothing outside the image changed.
        var outside = At(40, 40);
        outside[0].ShouldBe(0f, 0.001f);
        outside[3].ShouldBe(1f, 0.001f);

        // The scissor held: inside the clip the image shows, beside it only the (black) background.
        At(4, 32)[0].ShouldBe(2f, 0.02f, "inside the clip");
        At(12, 32)[0].ShouldBe(0f, 0.001f, "outside the clip: the image does not leak past it");
        layer[(32 * (int)size + 12) * 4 + 3].ShouldBe((byte)255, "and no hole was punched outside the clip");
    }

    [Fact]
    public void AnSdrContext_HasNoHdrState_AndTheSamePassesAndFormatAsAnHdrOne()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        // Nothing HDR exists on a context that did not ask: no layer, no composite pass, no pipeline.
        shared.HdrRequested.ShouldBeFalse();
        shared.HdrActive.ShouldBeFalse();
        shared.HdrLayerCount.ShouldBe(0);
        shared.HdrCompositePass.ShouldBe(VkRenderPass.Null);
        shared.HdrCompositeLayerPipeline.ShouldBe(VkPipeline.Null);
        shared.HdrCompositeImagePipeline.ShouldBe(VkPipeline.Null);
        shared.PresentFormat.ShouldBe(shared.SwapchainFormat);

        // Asking changes nothing about the device: the format and every pass are the shared device's own, B8G8R8A8.
        using var hdrCtx = HdrContext(shared, 32, 32);
        hdrCtx.HdrRequested.ShouldBeTrue();
        hdrCtx.HdrActive.ShouldBeTrue();
        hdrCtx.HdrLayerCount.ShouldBe(1);
        hdrCtx.HdrCompositePass.ShouldNotBe(VkRenderPass.Null);
        hdrCtx.PresentFormat.ShouldBe(VkFormat.R16G16B16A16Sfloat);
        shared.GraphicsDevice.ColorFormat.ShouldBe(VkFormat.B8G8R8A8Unorm);
        hdrCtx.GraphicsDevice.ColorFormat.ShouldBe(shared.GraphicsDevice.ColorFormat);
        hdrCtx.RenderPass.ShouldBe(shared.RenderPass);
        hdrCtx.RenderPassNoDepth.ShouldBe(shared.RenderPassNoDepth);
        hdrCtx.SingleSampleRenderPass.ShouldBe(shared.SingleSampleRenderPass);
        hdrCtx.OffscreenFormat.ShouldBe(shared.OffscreenFormat);
        shared.HdrActive.ShouldBeFalse("the HDR context did not change the SDR one");
    }
}
