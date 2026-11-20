using System;
using System.Linq;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="VkTexture.CreateFromYCbCr"/> (<c>ycbcrhdr.comp</c>) against the conversion it transcribes, done here in
/// double precision as SharpAstro.Heif does it on the CPU: random planes of every layout H.265 has, both ranges, the
/// identity matrix, odd sizes and whole-factor reductions, read back texel by texel. The shader works in float, so a
/// result within float error of a half code may round to the neighbouring code; each texel is allowed that and no
/// more, and such texels must stay rare. Skips when there is no Vulkan ICD, like the other render tests.
/// </summary>
[Collection("OffscreenGpu")]
public sealed class YCbCrConversionTests(OffscreenGpuFixture gpu)
{
    public static TheoryData<string, int, int, int, int, int, bool, bool, int> Cases => new()
    {
        // name, width, height, bit depth, shiftX, shiftY, full range, identity, reduce
        { "4:2:2 10-bit full range, Canon's", 61, 37, 10, 1, 0, true, false, 1 },
        { "4:2:0 8-bit narrow range", 33, 19, 8, 1, 1, false, false, 1 },
        { "4:4:4 12-bit narrow range", 17, 23, 12, 0, 0, false, false, 1 },
        { "4:4:4 identity (GBR)", 16, 16, 10, 0, 0, true, true, 1 },
        { "monochrome 10-bit", 29, 11, 10, -1, -1, true, false, 1 },
        { "4:2:2 reduced by 3, remainder dropped", 64, 50, 10, 1, 0, true, false, 3 },
        { "4:2:0 reduced by 4, narrow", 70, 41, 10, 1, 1, false, false, 4 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Conversion_MatchesTheDoublePrecisionCpuReference(string name, int width, int height, int bitDepth,
        int shiftX, int shiftY, bool fullRange, bool identity, int reduce)
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        var random = new Random(width * 31 + height * 7 + bitDepth);
        var max = (1 << bitDepth) - 1;
        var monochrome = shiftX < 0;
        var chromaWidth = monochrome ? 0 : (width + (1 << shiftX) - 1) >> shiftX;
        var chromaHeight = monochrome ? 0 : (height + (1 << shiftY) - 1) >> shiftY;
        // Narrow-range samples stay mostly inside their nominal range, with some outside it, as real streams do.
        ushort Sample() => (ushort)random.Next(0, max + 1);
        var luma = Enumerable.Range(0, width * height).Select(_ => Sample()).ToArray();
        var cb = Enumerable.Range(0, chromaWidth * chromaHeight).Select(_ => Sample()).ToArray();
        var cr = Enumerable.Range(0, chromaWidth * chromaHeight).Select(_ => Sample()).ToArray();
        // Any monotonic table: the renderer does not know what the transfer is. This one spans 0 to 50 like PQ does.
        var table = Enumerable.Range(0, max + 1).Select(c => (float)(50 * Math.Pow(c / (double)max, 2.4))).ToArray();
        const double kr = 0.2627, kb = 0.0593;

        var planes = new YCbCrPlanes
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            Luma = luma,
            Cb = cb,
            Cr = cr,
            ChromaWidth = chromaWidth,
            ChromaShiftX = Math.Max(shiftX, 0),
            ChromaShiftY = Math.Max(shiftY, 0),
            Kr = identity ? 0 : kr,
            Kb = identity ? 0 : kb,
            IdentityMatrix = identity,
            FullRange = fullRange,
            CodeToLinear = table,
        };

        // The reference: HeifDecoder.Colour.ToRgb, then the table, then the block average.
        int[] Codes(int x, int y)
        {
            int yv = luma[y * width + x], cbv = 1 << (bitDepth - 1), crv = cbv;
            if (!monochrome)
            {
                var c = (y >> shiftY) * chromaWidth + (x >> shiftX);
                (cbv, crv) = (cb[c], cr[c]);
            }
            if (identity) return [Math.Min(crv, max), Math.Min(yv, max), Math.Min(cbv, max)];
            double ey, ecb, ecr;
            if (fullRange)
            {
                var mid = 1 << (bitDepth - 1);
                (ey, ecb, ecr) = (yv, cbv - mid, crv - mid);
            }
            else
            {
                var s = bitDepth - 8;
                ey = (yv - (16 << s)) * (double)max / (219 << s);
                ecb = (cbv - (128 << s)) * (double)max / (224 << s);
                ecr = (crv - (128 << s)) * (double)max / (224 << s);
            }
            var er = ey + 2 * (1 - kr) * ecr;
            var eb = ey + 2 * (1 - kb) * ecb;
            var eg = (ey - kr * er - kb * eb) / (1 - kr - kb);
            int Code(double v) => (int)Math.Clamp(Math.Floor(v + 0.5), 0, max);
            return [Code(er), Code(eg), Code(eb)];
        }

        using var texture = VkTexture.CreateFromYCbCr(ctx, planes, reduce);
        (texture.Width, texture.Height).ShouldBe((width / reduce, height / reduce), name);
        var texels = texture.ReadbackRgbaHalf();

        // A texel may differ only by what one block member's channel moving one code would change, and that rarely.
        var maxStep = Enumerable.Range(1, max).Max(c => table[c] - table[c - 1]);
        var block = reduce * reduce;
        var off = 0;
        var cpuPeak = 0f;
        for (var oy = 0; oy < texture.Height; oy++)
        {
            for (var ox = 0; ox < texture.Width; ox++)
            {
                var sum = new double[3];
                for (var dy = 0; dy < reduce; dy++)
                    for (var dx = 0; dx < reduce; dx++)
                    {
                        var codes = Codes(ox * reduce + dx, oy * reduce + dy);
                        for (var ch = 0; ch < 3; ch++) sum[ch] += table[codes[ch]];
                    }
                var t = (oy * texture.Width + ox) * 4;
                for (var ch = 0; ch < 3; ch++)
                {
                    var expected = (float)(sum[ch] / block);
                    cpuPeak = Math.Max(cpuPeak, expected);
                    var actual = (float)texels[t + ch];
                    var halfError = Math.Max(Math.Abs(expected) * 1e-3f, 1e-4f); // half keeps about 11 bits
                    var difference = Math.Abs(actual - expected);
                    if (difference > halfError) off++;
                    difference.ShouldBeLessThanOrEqualTo(halfError + maxStep / block + 1e-6f,
                        $"{name}: texel ({ox}, {oy}) channel {ch}, expected {expected}, got {actual}");
                }
                ((float)texels[t + 3]).ShouldBe(1f);
            }
        }
        var channels = texture.Width * texture.Height * 3;
        off.ShouldBeLessThanOrEqualTo(Math.Max(1, channels / 200), $"{name}: {off} of {channels} channels a code apart");

        // The measured peak is the brightest channel of any texel (before half rounding, so compared loosely).
        texture.MeasuredPeak.ShouldNotBeNull(name);
        texture.MeasuredPeak.Value.ShouldBe(cpuPeak, Math.Max(cpuPeak * 1e-3f, maxStep / block + 1e-6f), name);
    }

    [Fact]
    public void Deferred_RunsInTheFrame_AndItsPeakArrivesOnceTheFrameRetires()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        // A flat grey picture with one bright pixel: the peak is that pixel's code, 1000 of 1023.
        const int width = 40, height = 24, max = 1023;
        var luma = Enumerable.Repeat((ushort)300, width * height).ToArray();
        luma[5 * width + 7] = 1000;
        var chroma = Enumerable.Repeat((ushort)512, width / 2 * height).ToArray();
        var table = Enumerable.Range(0, max + 1).Select(c => c / 100f).ToArray();
        var planes = new YCbCrPlanes
        {
            Width = width, Height = height, BitDepth = 10, Luma = luma, Cb = chroma, Cr = chroma, ChromaWidth = width / 2,
            ChromaShiftX = 1, Kr = 0.2627, Kb = 0.0593, FullRange = true, CodeToLinear = table,
        };

        ctx.ResizeOffscreen(64, 64);
        using var renderer = new VkRenderer(ctx, 64, 64);
        using var texture = VkTexture.CreateDeferredFromYCbCr(ctx, planes);
        texture.IsUploaded.ShouldBeFalse();
        texture.MeasuredPeak.ShouldBeNull();
        ctx.QueueTextureUpload(texture);

        var frames = 0;
        while (texture.MeasuredPeak is null && frames < 10)
        {
            renderer.BeginOffscreenFrame(new DIR.Lib.RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
            if (texture.IsUploaded) renderer.DrawHdrImage(texture, 0f, 0f, width, height, HdrPrimaries.Bt2020, 2000f);
            renderer.EndOffscreenFrame();
            ctx.WaitOffscreenFrameComplete();
            frames++;
        }

        texture.IsUploaded.ShouldBeTrue();
        texture.MeasuredPeak.ShouldNotBeNull($"after {frames} frames").ShouldBe(10f, 1e-6f);
        texture.ReadbackRgbaHalf()[(5 * width + 7) * 4 + 1].ShouldBe((Half)10f);
    }

    /// <summary>
    /// Both paths under the validation layer with synchronization validation: a one-shot conversion read back, then
    /// deferred conversions drawn in the frame that ran them, one disposed before its frame retired and one after.
    /// The barriers (host to compute, compute to fragment, compute to host for the peak) are what is checked here.
    /// </summary>
    [Fact]
    public void BothPaths_AreSilentUnderTheValidationLayer()
    {
        if (!ValidatedOffscreen.TryCreate(64, 64, out var created, out var messenger, out var api, out var skip) || created is not { } ctx)
        {
            Assert.Skip(skip);
            return;
        }

        ValidatedOffscreen.Messages.Clear();
        try
        {
            const int width = 48, height = 32;
            var luma = Enumerable.Range(0, width * height).Select(i => (ushort)(i % 1024)).ToArray();
            var chroma = Enumerable.Range(0, width / 2 * height).Select(i => (ushort)(i * 7 % 1024)).ToArray();
            var planes = new YCbCrPlanes
            {
                Width = width, Height = height, BitDepth = 10, Luma = luma, Cb = chroma, Cr = chroma, ChromaWidth = width / 2,
                ChromaShiftX = 1, Kr = 0.2627, Kb = 0.0593, FullRange = true,
                CodeToLinear = Enumerable.Range(0, 1024).Select(c => c / 1023f).ToArray(),
            };

            using (var once = VkTexture.CreateFromYCbCr(ctx, planes, reduce: 2))
                once.ReadbackRgbaHalf().Length.ShouldBe(24 * 16 * 4);

            using var renderer = new VkRenderer(ctx, 64, 64);
            var kept = VkTexture.CreateDeferredFromYCbCr(ctx, planes);
            var dropped = VkTexture.CreateDeferredFromYCbCr(ctx, planes);
            ctx.QueueTextureUpload(kept);
            ctx.QueueTextureUpload(dropped);
            for (var frame = 0; frame < 4; frame++)
            {
                renderer.BeginOffscreenFrame(new DIR.Lib.RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
                renderer.DrawHdrImage(kept, 0f, 0f, width, height, HdrPrimaries.Bt2020, 1000f);
                if (frame == 0)
                {
                    renderer.DrawHdrImage(dropped, 0f, 0f, width, height, HdrPrimaries.Bt2020, 1000f);
                    dropped.Dispose(); // in the frame that converted and drew it
                }
                renderer.EndOffscreenFrame();
                ctx.WaitOffscreenFrameComplete();
            }
            kept.MeasuredPeak.ShouldNotBeNull();
            kept.Dispose();

            var errors = ValidatedOffscreen.Messages.Where(m => ValidatedOffscreen.IsError(m) || ValidatedOffscreen.IsSyncHazard(m)).ToArray();
            Assert.True(errors.Length == 0, $"{errors.Length} validation error(s):\n{string.Join("\n\n", errors)}");
        }
        finally
        {
            ValidatedOffscreen.Destroy(ctx, messenger, api);
        }
    }

    [Fact]
    public void Planes_ThatDoNotDescribeAPicture_AreRefused_BeforeTheGpu()
    {
        var table = new float[1024];
        Should.Throw<ArgumentException>(() => new YCbCrPlanes
        {
            Width = 4, Height = 2, BitDepth = 10, Luma = new ushort[7], Kr = 0.2627, Kb = 0.0593, FullRange = true,
            CodeToLinear = table,
        }.Validate()).Message.ShouldContain("luma plane");
        Should.Throw<ArgumentException>(() => new YCbCrPlanes
        {
            Width = 4, Height = 2, BitDepth = 10, Luma = new ushort[8], Cb = new ushort[4], Cr = new ushort[3], ChromaWidth = 2,
            ChromaShiftX = 1, Kr = 0.2627, Kb = 0.0593, FullRange = true, CodeToLinear = table,
        }.Validate()).Message.ShouldContain("chroma planes");
        Should.Throw<ArgumentException>(() => new YCbCrPlanes
        {
            Width = 4, Height = 2, BitDepth = 10, Luma = new ushort[8], Kr = 0.2627, Kb = 0.0593, FullRange = true,
            CodeToLinear = new float[256],
        }.Validate()).Message.ShouldContain("code table");
    }
}
