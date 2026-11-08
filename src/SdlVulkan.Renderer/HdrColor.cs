using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// The RGB primaries of an HDR source's linear light. A small enum of its own rather than a reference to
/// the codec library's colour encoding: this library must not depend on SharpAstro.Codecs, so the app maps
/// its <c>ColorEncoding</c> onto these two.
/// </summary>
public enum HdrPrimaries
{
    /// <summary>ITU-R BT.709 / sRGB primaries (H.273 ColourPrimaries 1), D65 white.</summary>
    Bt709,

    /// <summary>ITU-R BT.2020 primaries (H.273 ColourPrimaries 9), D65 white. A wider gamut than
    /// <see cref="Bt709"/>: its saturated colours have negative <see cref="Bt709"/> components.</summary>
    Bt2020,
}

/// <summary>
/// The CPU maths of HDR image drawing: scales, primaries conversion, peak clamp, the sRGB transfer function and
/// a tone curve for an SDR swapchain. Pure functions, no GPU.
/// <para>
/// <c>Shaders/hdrimage.frag</c> is a transcription of these functions, line for line, and
/// <c>HdrImageRenderTests</c> holds the two to within one 8-bit step, so a change here is a change there.
/// </para>
/// <para>
/// Units. "Scene linear" below means linear light normalised so that <b>1.0 is the reference (SDR) white,
/// <see cref="ReferenceWhiteNits"/> = 203 cd/m²</b> (ITU-R BT.2408). "scRGB" means linear light where
/// <b>1.0 is <see cref="ScRgbUnitNits"/> = 80 cd/m²</b> (the sRGB reference white), with negative components
/// allowed for colours outside BT.709.
/// </para>
/// </summary>
public static class HdrColor
{
    /// <summary>Reference white of an HDR signal in cd/m², ITU-R BT.2408: where a diffuse white page sits.</summary>
    public const float ReferenceWhiteNits = 203f;

    /// <summary>The luminance in cd/m² that an scRGB component of 1.0 stands for (the sRGB reference white).</summary>
    public const float ScRgbUnitNits = 80f;

    /// <summary>Scene-linear value (1.0 = <see cref="ReferenceWhiteNits"/>) below which <see cref="ToneMap"/>
    /// changes nothing. Dimensionless, in (0, 1).</summary>
    public const float ToneKnee = 0.75f;

    /// <summary>
    /// The factor from scene linear (1.0 = <paramref name="referenceWhiteNits"/>) to scRGB (1.0 = 80 cd/m²):
    /// <c>referenceWhiteNits / 80</c>, 203 / 80 = 2.5375 for the default.
    /// </summary>
    /// <param name="referenceWhiteNits">The luminance in cd/m² that a scene-linear 1.0 stands for.</param>
    public static float NitsScale(float referenceWhiteNits = ReferenceWhiteNits)
        => referenceWhiteNits / ScRgbUnitNits;

    /// <summary>
    /// How far above reference white a display or source reaches: <c>peakNits / referenceWhiteNits</c>.
    /// Dimensionless; 1 is an SDR display, ~4.9 a 1000 cd/m² panel.
    /// </summary>
    /// <param name="peakNits">Peak luminance in cd/m².</param>
    /// <param name="referenceWhiteNits">Reference white in cd/m².</param>
    public static float Headroom(float peakNits, float referenceWhiteNits = ReferenceWhiteNits)
        => peakNits / referenceWhiteNits;

    /// <summary>
    /// The scRGB value of a peak luminance: reference white times <paramref name="headroom"/>, in scRGB units
    /// (SDR white times headroom). What <see cref="ClampToPeak"/> clamps to.
    /// </summary>
    /// <param name="headroom">Peak over reference white, dimensionless (see <see cref="Headroom"/>).</param>
    /// <param name="referenceWhiteNits">Reference white in cd/m².</param>
    public static float PeakScRgb(float headroom, float referenceWhiteNits = ReferenceWhiteNits)
        => NitsScale(referenceWhiteNits) * headroom;

    /// <summary>
    /// Clamps the top of an scRGB component to the display's peak. Negative values are left alone: scRGB carries
    /// colours outside BT.709 as negative components, and clamping them would throw the wide gamut away.
    /// </summary>
    /// <param name="scRgb">One scRGB component (1.0 = 80 cd/m²).</param>
    /// <param name="peakScRgb">The peak in the same units, see <see cref="PeakScRgb"/>.</param>
    public static float ClampToPeak(float scRgb, float peakScRgb) => MathF.Min(scRgb, peakScRgb);

    /// <summary>The sRGB EOTF: an encoded component in [0, 1] to linear light in [0, 1] (IEC 61966-2-1).</summary>
    /// <param name="encoded">Non-linear sRGB component, 0 to 1.</param>
    public static float SrgbEotf(float encoded)
        => encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);

    /// <summary>The inverse sRGB EOTF (the encoding): linear light to an encoded component, clamped to [0, 1].</summary>
    /// <param name="linear">Linear component, 1.0 = white of the sRGB range (not 203 cd/m²: the caller scales).</param>
    public static float SrgbInverseEotf(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>
    /// Tone curve for an SDR display: scene-linear light (1.0 = 203 cd/m²) in, display-linear light in [0, 1] out,
    /// per channel. <b>Identity up to <see cref="ToneKnee"/>, then an extended Reinhard roll-off</b> (Reinhard
    /// et al. 2002, eq. 4) over the part above the knee, which reaches exactly 1 at <paramref name="peak"/>.
    /// <para>
    /// With <c>u = (x - k) / (1 - k)</c> and <c>W = (peak - k) / (1 - k)</c> the output is
    /// <c>k + (1 - k) * u * (1 + u / W²) / (1 + u)</c>. It is C1 at the knee (slope 1 on both sides), strictly
    /// increasing, 0 at 0 and 1 at <paramref name="peak"/>; with <paramref name="peak"/> at or below 1 it is the
    /// identity, so an SDR-range source is not touched.
    /// </para>
    /// <para>
    /// Why this and not the BT.2390 EETF: BT.2390 is defined in the PQ domain, and carried over to linear light its
    /// knee goes negative for a source peak above three times white, so it would need a PQ round trip per pixel.
    /// This stays in linear light, is a few multiplies and one divide in the shader, keeps everything under the
    /// knee exact (so the bulk of an image, which sits well under the peak, is not dimmed) and is easy to prove
    /// monotonic. The cost is that it works per channel, so a very bright saturated highlight desaturates towards
    /// white rather than keeping its hue, which is the graceful way to lose it. Values above the peak are clamped to 1.
    /// </para>
    /// </summary>
    /// <param name="linear">Scene-linear component, 1.0 = 203 cd/m². Negative input gives 0.</param>
    /// <param name="peak">The source's peak in scene-linear units (peak cd/m² / 203), dimensionless.</param>
    public static float ToneMap(float linear, float peak)
    {
        if (linear <= 0f) return 0f;
        if (peak <= 1f || linear <= ToneKnee) return MathF.Min(linear, 1f);
        var u = (linear - ToneKnee) / (1f - ToneKnee);
        var w = (peak - ToneKnee) / (1f - ToneKnee);
        var g = u * (1f + u / (w * w)) / (1f + u);
        return MathF.Min(ToneKnee + (1f - ToneKnee) * g, 1f);
    }

    /// <summary>
    /// What <c>hdrimage.frag</c> writes to an SDR (8-bit, sRGB-encoded) target for one pixel: the primaries
    /// converted to BT.709, tone-mapped per channel and sRGB-encoded.
    /// </summary>
    /// <param name="primaries">Primaries of the scene-linear input.</param>
    /// <param name="r">Scene-linear red, 1.0 = 203 cd/m².</param>
    /// <param name="g">Scene-linear green, 1.0 = 203 cd/m².</param>
    /// <param name="b">Scene-linear blue, 1.0 = 203 cd/m².</param>
    /// <param name="peak">Source peak in scene-linear units (peak cd/m² / 203).</param>
    public static (float R, float G, float B) ToSdrEncoded(HdrPrimaries primaries, float r, float g, float b, float peak)
    {
        var (lr, lg, lb) = Apply(ToBt709(primaries), r, g, b);
        return (SrgbInverseEotf(ToneMap(lr, peak)), SrgbInverseEotf(ToneMap(lg, peak)), SrgbInverseEotf(ToneMap(lb, peak)));
    }

    /// <summary>
    /// What <c>hdrimage.frag</c> writes to an scRGB target for one pixel: the primaries converted to BT.709,
    /// scaled by <see cref="NitsScale"/> and clamped to the display's peak.
    /// </summary>
    /// <param name="primaries">Primaries of the scene-linear input.</param>
    /// <param name="r">Scene-linear red, 1.0 = 203 cd/m².</param>
    /// <param name="g">Scene-linear green, 1.0 = 203 cd/m².</param>
    /// <param name="b">Scene-linear blue, 1.0 = 203 cd/m².</param>
    /// <param name="peakScRgb">Display peak in scRGB units (1.0 = 80 cd/m²), see <see cref="PeakScRgb"/>.</param>
    public static (float R, float G, float B) ToScRgb(HdrPrimaries primaries, float r, float g, float b, float peakScRgb)
    {
        var (lr, lg, lb) = Apply(ToBt709(primaries), r, g, b);
        var s = NitsScale();
        return (ClampToPeak(lr * s, peakScRgb), ClampToPeak(lg * s, peakScRgb), ClampToPeak(lb * s, peakScRgb));
    }

    // ---- Primaries matrices ------------------------------------------------------------------------------

    // CIE xy chromaticities as H.273 lists them (ITU-T H.273 table 2): R, G, B, then the D65 white.
    private static double[] Chromaticities(HdrPrimaries p) => p switch
    {
        HdrPrimaries.Bt709 => [0.640, 0.330, 0.300, 0.600, 0.150, 0.060, 0.3127, 0.3290],
        HdrPrimaries.Bt2020 => [0.708, 0.292, 0.170, 0.797, 0.131, 0.046, 0.3127, 0.3290],
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    private static readonly float[] Identity = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f];
    private static readonly float[] Bt2020ToBt709Matrix = RgbToRgb(HdrPrimaries.Bt2020, HdrPrimaries.Bt709);
    private static readonly float[] Bt709ToBt2020Matrix = RgbToRgb(HdrPrimaries.Bt709, HdrPrimaries.Bt2020);

    /// <summary>
    /// The 3x3 matrix, row-major (9 floats), taking linear RGB of <paramref name="from"/> primaries to linear RGB
    /// of <paramref name="to"/> primaries, white (1,1,1) to white. Derived from the primaries' and D65 white's xy
    /// chromaticities by the usual RGB-to-XYZ construction, in double precision. A fresh array: for the hot
    /// path use <see cref="ToBt709"/>, which is cached.
    /// </summary>
    /// <param name="from">Primaries of the input.</param>
    /// <param name="to">Primaries of the output.</param>
    public static float[] RgbToRgbMatrix(HdrPrimaries from, HdrPrimaries to) => (float[])RgbToRgb(from, to).Clone();

    /// <summary>
    /// The cached matrix (row-major, 9 floats) from linear BT.2020 to linear BT.709. Components outside the BT.709
    /// gamut come out negative.
    /// </summary>
    public static ReadOnlySpan<float> Bt2020ToBt709 => Bt2020ToBt709Matrix;

    /// <summary>The cached matrix (row-major, 9 floats) from linear BT.709 to linear BT.2020.</summary>
    public static ReadOnlySpan<float> Bt709ToBt2020 => Bt709ToBt2020Matrix;

    /// <summary>The cached matrix (row-major, 9 floats) taking linear <paramref name="primaries"/> to linear BT.709.
    /// Exactly the identity for BT.709.</summary>
    /// <param name="primaries">Primaries of the input.</param>
    public static ReadOnlySpan<float> ToBt709(HdrPrimaries primaries) => primaries switch
    {
        HdrPrimaries.Bt709 => Identity,
        HdrPrimaries.Bt2020 => Bt2020ToBt709Matrix,
        _ => throw new ArgumentOutOfRangeException(nameof(primaries), primaries, null),
    };

    /// <summary>Multiplies a row-major 3x3 matrix by (r, g, b).</summary>
    /// <param name="m">Nine floats, row-major.</param>
    /// <param name="r">Red component.</param>
    /// <param name="g">Green component.</param>
    /// <param name="b">Blue component.</param>
    public static (float R, float G, float B) Apply(ReadOnlySpan<float> m, float r, float g, float b)
        => (m[0] * r + m[1] * g + m[2] * b,
            m[3] * r + m[4] * g + m[5] * b,
            m[6] * r + m[7] * g + m[8] * b);

    private static float[] RgbToRgb(HdrPrimaries from, HdrPrimaries to)
    {
        if (from == to) return (float[])Identity.Clone();
        var toXyz = RgbToXyz(from);
        var fromXyz = Invert(RgbToXyz(to));
        var m = Multiply(fromXyz, toXyz);
        var f = new float[9];
        for (var i = 0; i < 9; i++) f[i] = (float)m[i];
        return f;
    }

    // Columns of P are each primary's XYZ at unit luminance (x/y, 1, (1-x-y)/y); scaling each column by
    // S = P^-1 * white makes (1,1,1) land exactly on the white point.
    private static double[] RgbToXyz(HdrPrimaries p)
    {
        var c = Chromaticities(p);
        double[] col(int i) => [c[i] / c[i + 1], 1.0, (1.0 - c[i] - c[i + 1]) / c[i + 1]];
        var r = col(0);
        var g = col(2);
        var b = col(4);
        var white = new[] { c[6] / c[7], 1.0, (1.0 - c[6] - c[7]) / c[7] };
        double[] pm = [r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2]];
        var inv = Invert(pm);
        var s = new[]
        {
            inv[0] * white[0] + inv[1] * white[1] + inv[2] * white[2],
            inv[3] * white[0] + inv[4] * white[1] + inv[5] * white[2],
            inv[6] * white[0] + inv[7] * white[1] + inv[8] * white[2],
        };
        return [pm[0] * s[0], pm[1] * s[1], pm[2] * s[2],
                pm[3] * s[0], pm[4] * s[1], pm[5] * s[2],
                pm[6] * s[0], pm[7] * s[1], pm[8] * s[2]];
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var o = new double[9];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            o[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return o;
    }

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        var k = 1.0 / det;
        return [(e * i - f * h) * k, (c * h - b * i) * k, (b * f - c * e) * k,
                (f * g - d * i) * k, (a * i - c * g) * k, (c * d - a * f) * k,
                (d * h - e * g) * k, (b * g - a * h) * k, (a * e - b * d) * k];
    }
}

/// <summary>A swapchain format and colour space, as chosen by <see cref="HdrSurfaceFormat.Choose"/>.</summary>
/// <param name="Format">The swapchain image format.</param>
/// <param name="ColorSpace">The swapchain colour space.</param>
public readonly record struct SurfaceFormatChoice(VkFormat Format, VkColorSpaceKHR ColorSpace)
{
    /// <summary>True for the scRGB choice: a half-float swapchain in extended-sRGB linear.</summary>
    public bool IsScRgb => Format == VkFormat.R16G16B16A16Sfloat && ColorSpace == VkColorSpaceKHR.ExtendedSrgbLinearEXT;
}

/// <summary>
/// The swapchain format choice, as a pure function of what the surface offers. Nothing on the swapchain path
/// asks for HDR yet: <see cref="VulkanDevice"/> still picks through <see cref="PickSdr"/>, whose answers are
/// unchanged.
/// </summary>
public static class HdrSurfaceFormat
{
    /// <summary>
    /// <see cref="VkFormat.R16G16B16A16Sfloat"/> in <see cref="VkColorSpaceKHR.ExtendedSrgbLinearEXT"/> (scRGB) when
    /// <paramref name="hdrRequested"/> and the surface offers exactly that pair; otherwise the SDR choice, which is
    /// <see cref="PickSdr"/> in <see cref="VkColorSpaceKHR.SrgbNonLinear"/>, the one colour space the swapchain
    /// has always been created in. A surface offering only HDR10 (ST.2084) gets the SDR choice too: PQ output
    /// needs a different encode, which is not this path.
    /// </summary>
    /// <param name="offered">What <c>vkGetPhysicalDeviceSurfaceFormatsKHR</c> returned.</param>
    /// <param name="hdrRequested">Whether the app asked for an HDR swapchain.</param>
    public static SurfaceFormatChoice Choose(ReadOnlySpan<VkSurfaceFormatKHR> offered, bool hdrRequested)
    {
        if (hdrRequested)
        {
            foreach (var f in offered)
                if (f.format == VkFormat.R16G16B16A16Sfloat && f.colorSpace == VkColorSpaceKHR.ExtendedSrgbLinearEXT)
                    return new SurfaceFormatChoice(VkFormat.R16G16B16A16Sfloat, VkColorSpaceKHR.ExtendedSrgbLinearEXT);
        }
        return new SurfaceFormatChoice(PickSdr(offered), VkColorSpaceKHR.SrgbNonLinear);
    }

    /// <summary>
    /// The 8-bit colour format the swapchain has always used: B8G8R8A8Unorm when offered (desktop: keeps the readback
    /// and offscreen byte order), else R8G8B8A8Unorm (Android/Mali offers no BGRA), else the surface's first
    /// advertised format. An empty list, or a single legacy Undefined entry (which means "any"), gives B8G8R8A8Unorm.
    /// Moved here unchanged from <c>VulkanDevice.PickSurfaceColorFormat</c> so it can be tested without a surface.
    /// </summary>
    /// <param name="offered">What <c>vkGetPhysicalDeviceSurfaceFormatsKHR</c> returned.</param>
    public static VkFormat PickSdr(ReadOnlySpan<VkSurfaceFormatKHR> offered)
    {
        if (offered.Length == 0)
            return VkFormat.B8G8R8A8Unorm;

        if (offered.Length == 1 && offered[0].format == VkFormat.Undefined)
            return VkFormat.B8G8R8A8Unorm;

        var hasBgra = false;
        var hasRgba = false;
        foreach (var f in offered)
        {
            if (f.format == VkFormat.B8G8R8A8Unorm) hasBgra = true;
            else if (f.format == VkFormat.R8G8B8A8Unorm) hasRgba = true;
        }
        if (hasBgra) return VkFormat.B8G8R8A8Unorm;
        if (hasRgba) return VkFormat.R8G8B8A8Unorm;
        return offered[0].format;
    }
}
