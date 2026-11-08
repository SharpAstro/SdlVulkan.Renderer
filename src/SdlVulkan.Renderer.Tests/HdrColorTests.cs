using System.Numerics;
using SdlVulkan.Renderer;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The CPU maths of HDR: pure, in memory, no GPU. The primaries matrix is derived HERE, independently, from the
/// H.273 chromaticities (with System.Numerics rather than the library's own routine) and compared with the library's.
/// </summary>
public sealed class HdrColorTests
{
    // ITU-T H.273 table 2: R, G, B xy, then D65.
    private static readonly double[] Bt709Xy = [0.640, 0.330, 0.300, 0.600, 0.150, 0.060];
    private static readonly double[] Bt2020Xy = [0.708, 0.292, 0.170, 0.797, 0.131, 0.046];
    private const double D65X = 0.3127, D65Y = 0.3290;

    // RGB -> XYZ for the given primaries, built with Matrix4x4 (row-vector convention: xyz = rgb * A).
    private static Matrix4x4 RgbToXyz(double[] xy)
    {
        Vector3 Col(int i) => new((float)(xy[i] / xy[i + 1]), 1f, (float)((1 - xy[i] - xy[i + 1]) / xy[i + 1]));
        var r = Col(0);
        var g = Col(2);
        var b = Col(4);
        var white = new Vector3((float)(D65X / D65Y), 1f, (float)((1 - D65X - D65Y) / D65Y));
        // Rows r, g, b: solve s * P = white.
        var p = new Matrix4x4(r.X, r.Y, r.Z, 0, g.X, g.Y, g.Z, 0, b.X, b.Y, b.Z, 0, 0, 0, 0, 1);
        Matrix4x4.Invert(p, out var pInv).ShouldBeTrue();
        var s = Vector3.Transform(white, pInv);
        return new Matrix4x4(r.X * s.X, r.Y * s.X, r.Z * s.X, 0, g.X * s.Y, g.Y * s.Y, g.Z * s.Y, 0,
            b.X * s.Z, b.Y * s.Z, b.Z * s.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>Row-major 3x3 (column-vector convention: out = M * in) from BT.2020 to BT.709, from H.273.</summary>
    private static float[] ExpectedBt2020ToBt709()
    {
        // Row-vector: rgb709 = rgb2020 * A2020 * inv(A709).
        Matrix4x4.Invert(RgbToXyz(Bt709Xy), out var inv709).ShouldBeTrue();
        var m = RgbToXyz(Bt2020Xy) * inv709; // out_j = sum_i in_i * m[i, j]
        return [m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, m.M13, m.M23, m.M33];
    }

    [Fact]
    public void NitsScale_Is_203_Over_80()
    {
        HdrColor.NitsScale().ShouldBe(203f / 80f, 1e-6f);
        HdrColor.NitsScale().ShouldBe(2.5375f, 1e-6f);
        HdrColor.NitsScale(80f).ShouldBe(1f, 1e-6f);
        HdrColor.PeakScRgb(HdrColor.Headroom(1000f)).ShouldBe(1000f / 80f, 1e-4f);
    }

    [Fact]
    public void Bt2020ToBt709_MatchesTheMatrixDerivedFromH273()
    {
        var expected = ExpectedBt2020ToBt709();
        var actual = HdrColor.RgbToRgbMatrix(HdrPrimaries.Bt2020, HdrPrimaries.Bt709);
        for (var i = 0; i < 9; i++) actual[i].ShouldBe(expected[i], 2e-4f, $"element {i}");
        HdrColor.Bt2020ToBt709.ToArray().ShouldBe(actual);

        // And the well-known published values (ITU-R BT.2087), to catch a derivation wrong in the same way twice.
        float[] known = [1.6605f, -0.5876f, -0.0728f, -0.1246f, 1.1329f, -0.0083f, -0.0182f, -0.1006f, 1.1187f];
        for (var i = 0; i < 9; i++) actual[i].ShouldBe(known[i], 1e-3f, $"published element {i}");
    }

    [Fact]
    public void Bt2020ToBt709_RowsSumToOne_SoWhiteStaysWhite()
    {
        var m = HdrColor.Bt2020ToBt709;
        for (var row = 0; row < 3; row++)
            (m[row * 3] + m[row * 3 + 1] + m[row * 3 + 2]).ShouldBe(1f, 1e-5f, $"row {row}");
        var (r, g, b) = HdrColor.Apply(m, 1f, 1f, 1f);
        r.ShouldBe(1f, 1e-5f);
        g.ShouldBe(1f, 1e-5f);
        b.ShouldBe(1f, 1e-5f);
    }

    [Fact]
    public void SaturatedBt2020Green_GoesNegativeInBt709()
    {
        var (r, g, b) = HdrColor.Apply(HdrColor.Bt2020ToBt709, 0f, 1f, 0f);
        r.ShouldBeLessThan(0f);
        b.ShouldBeLessThan(0f);
        g.ShouldBeGreaterThan(1f);
    }

    [Fact]
    public void SamePrimaries_IsExactlyTheIdentity_AndARoundTripReturnsTheColour()
    {
        HdrColor.ToBt709(HdrPrimaries.Bt709).ToArray().ShouldBe([1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f]);
        var there = HdrColor.Apply(HdrColor.Bt709ToBt2020, 0.2f, 0.5f, 0.8f);
        var (r, g, b) = HdrColor.Apply(HdrColor.Bt2020ToBt709, there.R, there.G, there.B);
        r.ShouldBe(0.2f, 1e-4f);
        g.ShouldBe(0.5f, 1e-4f);
        b.ShouldBe(0.8f, 1e-4f);
    }

    [Fact]
    public void ClampToPeak_ClampsTheTopOnly()
    {
        var peak = HdrColor.PeakScRgb(HdrColor.Headroom(1000f)); // 12.5 scRGB
        HdrColor.ClampToPeak(20f, peak).ShouldBe(peak);
        HdrColor.ClampToPeak(3f, peak).ShouldBe(3f);
        HdrColor.ClampToPeak(peak, peak).ShouldBe(peak);
        HdrColor.ClampToPeak(-0.4f, peak).ShouldBe(-0.4f, "a negative scRGB component is a colour outside BT.709, kept");
    }

    [Fact]
    public void Srgb_RoundTrips_AndHasItsKnownPoints()
    {
        for (var i = 0; i <= 1000; i++)
        {
            var v = i / 1000f;
            HdrColor.SrgbInverseEotf(HdrColor.SrgbEotf(v)).ShouldBe(v, 1e-5f);
        }
        HdrColor.SrgbEotf(0f).ShouldBe(0f);
        HdrColor.SrgbEotf(1f).ShouldBe(1f, 1e-6f);
        HdrColor.SrgbEotf(0.5f).ShouldBe(0.21404f, 1e-4f);
        HdrColor.SrgbInverseEotf(0.18f).ShouldBe(0.46135f, 1e-4f);
        HdrColor.SrgbInverseEotf(-1f).ShouldBe(0f);
        HdrColor.SrgbInverseEotf(7f).ShouldBe(1f, 1e-6f);
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(4.926f)] // a 1000 cd/m2 source
    [InlineData(10f)]
    public void ToneMap_MapsZeroToZero_AndThePeakToOne_StrictlyIncreasing(float peak)
    {
        HdrColor.ToneMap(0f, peak).ShouldBe(0f);
        HdrColor.ToneMap(peak, peak).ShouldBe(1f, 1e-5f);
        HdrColor.ToneMap(peak * 3f, peak).ShouldBe(1f, "above the peak is clamped");

        var previous = -1f;
        const int steps = 4000;
        for (var i = 0; i <= steps; i++)
        {
            var x = peak * i / steps;
            var y = HdrColor.ToneMap(x, peak);
            y.ShouldBeGreaterThan(previous, $"at x = {x}");
            previous = y;
        }
    }

    [Fact]
    public void ToneMap_IsIdentityUnderTheKnee_AndForAPeakAtOrBelowWhite()
    {
        for (var i = 0; i <= 20; i++)
        {
            var x = HdrColor.ToneKnee * i / 20f;
            HdrColor.ToneMap(x, 5f).ShouldBe(x, 1e-6f);
        }
        for (var i = 0; i <= 20; i++)
        {
            var x = i / 20f;
            HdrColor.ToneMap(x, 1f).ShouldBe(x, 1e-6f);
            HdrColor.ToneMap(x, 0.5f).ShouldBe(x, 1e-6f);
        }
        HdrColor.ToneMap(-3f, 5f).ShouldBe(0f);
    }

    [Fact]
    public void ToneMap_HasNoKinkAtTheKnee()
    {
        const float peak = 5f, h = 1e-3f;
        var below = (HdrColor.ToneMap(HdrColor.ToneKnee, peak) - HdrColor.ToneMap(HdrColor.ToneKnee - h, peak)) / h;
        var above = (HdrColor.ToneMap(HdrColor.ToneKnee + h, peak) - HdrColor.ToneMap(HdrColor.ToneKnee, peak)) / h;
        below.ShouldBe(1f, 1e-2f);
        above.ShouldBe(1f, 2e-2f);
    }

    [Fact]
    public void ToSdrEncoded_WhiteAndBlackSurvive_ForBothPrimaries()
    {
        foreach (var p in new[] { HdrPrimaries.Bt709, HdrPrimaries.Bt2020 })
        {
            HdrColor.ToSdrEncoded(p, 0f, 0f, 0f, 4.926f).ShouldBe((0f, 0f, 0f));
            // SDR-peak source: reference white is the display's white.
            var white = HdrColor.ToSdrEncoded(p, 1f, 1f, 1f, 1f);
            white.R.ShouldBe(1f, 1e-4f);
            white.G.ShouldBe(1f, 1e-4f);
            white.B.ShouldBe(1f, 1e-4f);
        }
    }

    [Fact]
    public void ToScRgb_ScalesAndClamps()
    {
        var (r, g, b) = HdrColor.ToScRgb(HdrPrimaries.Bt709, 1f, 2f, 100f, HdrColor.PeakScRgb(HdrColor.Headroom(1000f)));
        r.ShouldBe(2.5375f, 1e-5f);
        g.ShouldBe(5.075f, 1e-5f);
        b.ShouldBe(12.5f, 1e-5f);
    }
}
