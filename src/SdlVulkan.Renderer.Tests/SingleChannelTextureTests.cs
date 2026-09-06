using System;
using DIR.Lib;
using Shouldly;
using Vortice.Vulkan;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// That <see cref="VkTexture.CreateDeferred"/> sizes its staging buffer by the format it was given.
///
/// <para>It used to assume four bytes a pixel whatever the format, which failed in two directions and
/// neither is obvious. Wider than four bytes and the buffer is too small, so the copy throws and no
/// such format uploads at all. Narrower and the buffer is merely oversized: the bytes still land where
/// the image copy reads them, because Vulkan takes the copy extent from the image, so a single-channel
/// texture rendered perfectly while quietly costing four times the staging it needed.</para>
///
/// <para>Hence two tests. The wide one is the regression: it throws before the fix and passes after.
/// The single-channel one does not fail without the fix and is not pretending to -- it pins that an R8
/// coverage mask reaches the masked pipeline and varies across its own width, which nothing else
/// covered, and it is the case the wasted staging was hurting.</para>
/// </summary>
[Collection("OffscreenGpu")]
public sealed class SingleChannelTextureTests(OffscreenGpuFixture gpu)
{
    private const uint Size = 64;
    private const int Dim = 8;

    [Fact]
    public void AFormatWiderThanFourBytesUploadsAtAll()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        // Eight bytes a texel. Before the fix the staging buffer was sized at four, and the copy into
        // it threw for being too short -- so this is the assertion that the size comes from the format.
        var pixels = new byte[Dim * Dim * 8];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i & 0xFF);

        var ex = Record.Exception(() =>
        {
            using var tex = VkTexture.CreateDeferred(ctx, pixels, Dim, Dim, VkFormat.R16G16B16A16Sfloat);
            ctx.ExecuteOneShot(cmd => tex.RecordUpload(cmd));
            tex.CleanupStaging();
        });
        ex.ShouldBeNull();
    }

    [Fact]
    public void AnR8TextureUploadsItsOwnBytes()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        ctx.ResizeOffscreen(Size, Size);
        using var renderer = new VkRenderer(ctx, Size, Size);

        // Opaque white, so what reaches the target is the coverage and nothing else.
        var white = new byte[Dim * Dim * 4];
        Array.Fill(white, (byte)255);
        using var texture = VkTexture.CreateFromBgra(ctx, white, Dim, Dim);

        // A left-to-right ramp, one byte per texel.
        var ramp = new byte[Dim * Dim];
        for (var y = 0; y < Dim; y++)
        for (var x = 0; x < Dim; x++)
            ramp[y * Dim + x] = (byte)(x * 255 / (Dim - 1));
        using var mask = VkTexture.CreateDeferred(ctx, ramp, Dim, Dim, VkFormat.R8Unorm);
        // CreateDeferred records nothing by itself -- the name is the contract. Upload it the way the
        // immediate helper does, since there is no frame command buffer to fold it into here.
        ctx.ExecuteOneShot(cmd => mask.RecordUpload(cmd));
        mask.CleanupStaging();

        var maskedSet = texture.CreateMaskedDescriptorSet(mask);
        try
        {
            renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            renderer.DrawMaskedQuad(maskedSet, 0f, 0f, Size, 0f, 0f, Size, Size, Size);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            var rgba = ctx.ReadbackOffscreenRgba();

            // Read the ramp across the middle row. Monotonic, dark at the left, bright at the right.
            var y = (int)Size / 2;
            int Lum(int x) => rgba[(y * (int)Size + x) * 4];

            var left = Lum(1);
            var right = Lum((int)Size - 2);
            right.ShouldBeGreaterThan(left + 180,
                $"expected a dark-to-light ramp, got left={left} right={right}. Equal ends mean the "
                + "coverage never varied, which is what a mis-strided upload produces");

            var mid = Lum((int)Size / 2);
            mid.ShouldBeInRange(left + 20, right - 20,
                $"the ramp's middle should sit between its ends, got {mid} against {left}..{right}");
        }
        finally
        {
            ctx.FreeMaskedDescriptorSet(maskedSet);
        }
    }
}
