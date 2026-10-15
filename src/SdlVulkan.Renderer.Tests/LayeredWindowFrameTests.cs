using System;
using System.Collections.Generic;
using System.Linq;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// A layered context (<see cref="AlphaPresentation.LayeredWindow"/>) draws through the same <c>BeginFrame</c> /
/// <c>EndFrame</c> as a swapchain one, into an image of its own, and hands every finished frame to its window. The
/// window here is a stand-in that keeps what it is given, and the device is the suite's offscreen one, so the whole
/// frame path runs wherever the suite does: the readback, the bytes the window gets (premultiplied BGRA, rows
/// top-down), a window that changed size reported as a swapchain reports being out of date, and a partial frame that
/// keeps the rest of the image. <c>UpdateLayeredWindow</c> itself is Windows' and is measured by
/// <c>tools/TransparencyProbe</c>.
/// <para>Tests skip when Vulkan isn't loadable on the host.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class LayeredWindowFrameTests(OffscreenGpuFixture gpu)
{
    private static readonly RGBAColor32 Transparent = new(0, 0, 0, 0);
    private static readonly RGBAColor32 OpaqueBlue = new(0, 0, 255, 255);
    private static readonly RGBAColor32 HalfRed = new(255, 0, 0, 128);
    private static readonly RGBAColor32 OpaqueGreen = new(0, 255, 0, 255);

    private static RectInt Rect(int x0, int y0, int x1, int y1) => new(new PointInt(x1, y1), new PointInt(x0, y0));

    /// <summary>The window, as far as the context knows it: a size, and the frames it was given.</summary>
    private sealed class StandInWindow(uint width, uint height) : ILayeredTarget
    {
        public (uint Width, uint Height) PixelSize { get; set; } = (width, height);
        public List<(byte[] Bgra, uint Width, uint Height)> Frames { get; } = [];
        public bool Disposed { get; private set; }

        public bool Present(ReadOnlySpan<byte> bgra, uint width, uint height)
        {
            // What LayeredWindow does too: a frame for a window that has since changed size is not shown.
            if (PixelSize != (width, height))
                return false;
            Frames.Add((bgra[..(int)(width * height * 4)].ToArray(), width, height));
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    // (B, G, R, A), as the window gets them.
    private static (byte B, byte G, byte R, byte A) PixelAt((byte[] Bgra, uint Width, uint Height) frame, int x, int y)
    {
        var at = (y * (int)frame.Width + x) * 4;
        return (frame.Bgra[at], frame.Bgra[at + 1], frame.Bgra[at + 2], frame.Bgra[at + 3]);
    }

    [Fact]
    public void AFrame_ReachesTheWindowAsPremultipliedBgra()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var window = new StandInWindow(64, 48);
        using (var ctx = VulkanContext.CreateLayeredForSharedDevice(shared.GraphicsDevice, window))
        {
            ctx.AlphaPresentation.ShouldBe(AlphaPresentation.LayeredWindow);
            ctx.PresentsAlpha.ShouldBeTrue();
            (ctx.SwapchainWidth, ctx.SwapchainHeight).ShouldBe((64u, 48u), "the image is the window's size");

            using var renderer = new VkRenderer(ctx, 64, 48);
            renderer.BeginFrame(Transparent).ShouldBeTrue();
            renderer.FillRectangle(Rect(0, 0, 16, 16), OpaqueBlue);
            renderer.FillRectangle(Rect(16, 0, 32, 16), HalfRed);
            renderer.FillRectangle(Rect(0, 40, 64, 48), OpaqueGreen);   // the bottom rows: top-down, not flipped
            renderer.EndFrame();

            window.Frames.Count.ShouldBe(1, "EndFrame hands the frame over at once, not at the next frame");
            var frame = window.Frames[0];
            (frame.Width, frame.Height).ShouldBe((64u, 48u));
            PixelAt(frame, 48, 24).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)0), "an untouched pixel is fully transparent");
            PixelAt(frame, 8, 8).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255), "opaque blue, in BGRA order");
            var (b, g, r, a) = PixelAt(frame, 24, 8);
            ((int)r).ShouldBeInRange(126, 130, "half red's colour is premultiplied");
            ((int)a).ShouldBeInRange(126, 130, "half red's alpha is its own");
            (b, g).ShouldBe(((byte)0, (byte)0));
            PixelAt(frame, 8, 44).ShouldBe(((byte)0, (byte)255, (byte)0, (byte)255), "row 44 is near the bottom");
        }
        window.Disposed.ShouldBeTrue("the context disposes the window target with itself");
    }

    [Fact]
    public void AWindowThatChangedSize_IsAResize_AndTheNextFrameHasTheNewSize()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var window = new StandInWindow(64, 48);
        using var ctx = VulkanContext.CreateLayeredForSharedDevice(shared.GraphicsDevice, window);
        using var renderer = new VkRenderer(ctx, 64, 48);
        renderer.BeginFrame(Transparent).ShouldBeTrue();
        renderer.FillRectangle(Rect(0, 0, 8, 8), OpaqueBlue);
        renderer.EndFrame();
        window.Frames.Count.ShouldBe(1);

        // Between frames: the next BeginFrame says so, as an out-of-date swapchain's would, and draws nothing.
        window.PixelSize = (80, 60);
        renderer.BeginFrame(Transparent).ShouldBeFalse();
        renderer.Resize(80, 60);
        renderer.BeginFrame(Transparent).ShouldBeTrue();
        renderer.FillRectangle(Rect(72, 52, 80, 60), OpaqueBlue);
        renderer.EndFrame();
        window.Frames.Count.ShouldBe(2);
        (window.Frames[1].Width, window.Frames[1].Height).ShouldBe((80u, 60u));
        PixelAt(window.Frames[1], 76, 56).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255), "drawn at the new size");

        // During a frame: the frame for the old size is not shown, and the next BeginFrame reports the resize.
        renderer.BeginFrame(Transparent).ShouldBeTrue();
        window.PixelSize = (64, 48);
        renderer.EndFrame();
        window.Frames.Count.ShouldBe(2, "a frame for a size the window no longer has is not put on it");
        renderer.BeginFrame(Transparent).ShouldBeFalse();
    }

    [Fact]
    public void APartialFrame_KeepsTheRestOfTheImage()
    {
        if (gpu.Context is not { } shared)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var window = new StandInWindow(64, 48);
        using var ctx = VulkanContext.CreateLayeredForSharedDevice(shared.GraphicsDevice, window);
        using var renderer = new VkRenderer(ctx, 64, 48);
        renderer.BeginFrame(Transparent).ShouldBeTrue();
        renderer.FillRectangle(Rect(0, 0, 64, 48), OpaqueBlue);
        renderer.EndFrame();

        // One image, so the next frame's image holds this one, and damage can confine the next frame to a corner.
        renderer.AddFrameDamage(0, 0, 16, 16);
        renderer.BeginFrame(Transparent).ShouldBeTrue();
        renderer.FillRectangle(Rect(0, 0, 64, 48), OpaqueGreen);
        renderer.EndFrame();

        ctx.LastFrameWasPartial.ShouldBeTrue("the damage confined the frame");
        var frame = window.Frames[^1];
        PixelAt(frame, 8, 8).ShouldBe(((byte)0, (byte)255, (byte)0, (byte)255), "inside the damage: this frame");
        PixelAt(frame, 40, 30).ShouldBe(((byte)255, (byte)0, (byte)0, (byte)255), "outside it: the frame before");
    }

    [Fact]
    public void TheLayeredFramePath_IsValidationClean()
    {
        // The copy out of the image, the transitions around it and the buffer's hand-over to the host, under the
        // validation layer with synchronization validation: a full frame, a partial one (the load pass over the
        // copied image), a resize and a frame after it. Skips where the layer is not installed; CI's test lane has it.
        if (!ValidatedOffscreen.TryCreate(64, 48, out var device, out var messenger, out var api, out var skip))
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        try
        {
            var window = new StandInWindow(64, 48);
            using (var ctx = VulkanContext.CreateLayeredForSharedDevice(device!.GraphicsDevice, window))
            using (var renderer = new VkRenderer(ctx, 64, 48))
            {
                renderer.BeginFrame(Transparent).ShouldBeTrue();
                renderer.FillRectangle(Rect(0, 0, 64, 48), HalfRed);
                renderer.EndFrame();

                renderer.AddFrameDamage(0, 0, 16, 16);
                renderer.BeginFrame(Transparent).ShouldBeTrue();
                renderer.FillRectangle(Rect(0, 0, 16, 16), OpaqueBlue);
                renderer.EndFrame();

                window.PixelSize = (80, 60);
                renderer.BeginFrame(Transparent).ShouldBeFalse();
                renderer.Resize(80, 60);
                renderer.BeginFrame(Transparent).ShouldBeTrue();
                renderer.FillRectangle(Rect(0, 0, 80, 60), OpaqueGreen);
                renderer.EndFrame();
            }
            window.Frames.Count.ShouldBe(3);

            var problems = ValidatedOffscreen.Messages
                .Where(m => ValidatedOffscreen.IsError(m) || ValidatedOffscreen.IsSyncHazard(m)).ToArray();
            Assert.True(problems.Length == 0,
                $"the layered frame path drew {problems.Length} validation error(s) or hazard(s):\n" +
                string.Join("\n\n", problems));
        }
        finally
        {
            ValidatedOffscreen.Destroy(device, messenger, api);
        }
    }
}
