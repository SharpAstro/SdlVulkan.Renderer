using System;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// A texture made from BC1 blocks (<see cref="VkFormat.Bc1RgbUnormBlock"/>) draws the colours its blocks
/// encode. Each 4×4 block here is one solid colour (both endpoints that colour, every index 0), laid out so a
/// block put in the wrong place, a row of blocks read with the wrong pitch, or an RGBA upload of BC bytes shows
/// as the wrong colour. The image is 12×8 pixels, three blocks by two; the device must have the
/// <c>textureCompressionBC</c> feature, which lavapipe and every desktop GPU do.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class Bc1TextureRenderTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 48;

    [Fact]
    public void ABc1TextureDrawsTheColoursItsBlocksEncode()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }
        if (!ctx.GraphicsDevice.SupportsBc1Textures)
        {
            Assert.Skip("This device has no textureCompressionBC");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);

        // Row 0: red, green, blue. Row 1: white, black, red. 565 colours, exact in 8 bits.
        ushort[] colours = [0xF800, 0x07E0, 0x001F, 0xFFFF, 0x0000, 0xF800];
        var blocks = new byte[colours.Length * 8];
        for (var i = 0; i < colours.Length; i++)
        {
            blocks[i * 8] = (byte)colours[i]; blocks[i * 8 + 1] = (byte)(colours[i] >> 8);
            blocks[i * 8 + 2] = (byte)colours[i]; blocks[i * 8 + 3] = (byte)(colours[i] >> 8);
            // indices 0: every pixel is colour 0
        }

        using var texture = VkTexture.CreateDeferred(ctx, blocks, 12, 8, VkFormat.Bc1RgbUnormBlock);
        texture.ImageBytes.ShouldBe(48L, "six blocks of eight bytes, where the RGBA is 384");
        renderer.BeginOffscreenFrame(new RGBAColor32(128, 128, 128, 255)).ShouldBeTrue();
        texture.RecordUpload(renderer.CurrentCommandBuffer);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        // The texture fills the 48×36 top of the target: each block covers 16×18 pixels.
        renderer.BeginOffscreenFrame(new RGBAColor32(128, 128, 128, 255)).ShouldBeTrue();
        renderer.DrawTexturedQuad(texture.DescriptorSet, 0f, 0f, 48f, 0f, 0f, 36f, 48f, 36f);
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();
        var rgba = ctx.ReadbackOffscreenRgba();

        (int, int, int) At(int x, int y) { var i = (y * (int)Size + x) * 4; return (rgba[i], rgba[i + 1], rgba[i + 2]); }
        At(8, 9).ShouldBe((255, 0, 0), "block 0,0 is red");
        At(24, 9).ShouldBe((0, 255, 0), "block 1,0 is green");
        At(40, 9).ShouldBe((0, 0, 255), "block 2,0 is blue");
        At(8, 27).ShouldBe((255, 255, 255), "block 0,1 is white");
        At(24, 27).ShouldBe((0, 0, 0), "block 1,1 is black");
        At(40, 27).ShouldBe((255, 0, 0), "block 2,1 is red");
        At(24, 42).ShouldBe((128, 128, 128), "below the texture is the clear");
    }

    [Fact]
    public void ABc1ImageIsAnEighthOfItsRgba()
    {
        // 8 bytes a 4×4 block; a partial edge block counts whole.
        VkTexture.StagingBytes(VkFormat.Bc1RgbUnormBlock, 3164, 4096).ShouldBe(791L * 1024 * 8);
        VkTexture.StagingBytes(VkFormat.Bc1RgbUnormBlock, 5, 5).ShouldBe(4L * 8);
        VkTexture.StagingBytes(VkFormat.R8G8B8A8Unorm, 5, 5).ShouldBe(100L);
    }
}
