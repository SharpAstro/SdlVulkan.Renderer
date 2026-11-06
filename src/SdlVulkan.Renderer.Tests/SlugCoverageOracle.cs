using System;
using DIR.Lib;

namespace SdlVulkan.Renderer.Tests;

// Ported from DIR.Lib's test project, SharpAstro/DIR.Lib src/DIR.Lib.Tests/SlugCoverageOracle.cs (DIR.Lib 11.48,
// SharpAstro/DIR.Lib#188), with two differences: the namespace and this note, and the Coverage overload that takes a
// pixels-per-em for x and another for y (the original takes one), which the instanced tests need for a glyph stretched
// horizontally or rotated; the one-em overload calls it with the same value twice, so the coverage maths itself is the
// original's line for line. It is internal to that test project and not in the package, so it is copied here: slug.frag
// is a GLSL port of the same function, and SlugTextRenderTests / SlugInstancedRenderTests hold the GPU's output to this
// CPU one. If the shader's coverage maths changes, change both.

/// <summary>
/// The Slug fragment shader's coverage function, ported to C# and reading nothing but a <see cref="SlugGlyphBlob"/>'s
/// <see cref="SlugGlyphBlob.Words"/>: the band lookup, the sorted curve lists, the early-out, the root solve and the
/// combination of the horizontal and the vertical ray all go through the layout the GPU will read, so a test of it is a
/// test of that layout. Eric Lengyel's Slug algorithm (patent dedicated to the public domain from 2026-03-17),
/// following the structure of the pixel shader in his paper and in HarfBuzz's <c>hb-gpu</c> shader; written fresh
/// here against this repo's own blob.
/// </summary>
/// <remarks>
/// <para>Per sample a ray runs along x through the glyph's horizontal band and another along y through its vertical
/// band. For each curve the shader classifies which of the two roots of the curve's quadratic in the ray's direction
/// count (from the signs of the three control points' distance to the ray, a table lookup), solves for them, and adds
/// a coverage contribution of <c>saturate(root * pixelsPerEm + 0.5)</c>: a crossing more than half a pixel ahead counts
/// fully, one more than half a pixel behind not at all, so an edge through the pixel blends. The two rays are combined
/// weighted by how close their crossings were to the pixel, and the sum of signed contributions is the winding number,
/// which nonzero winding clamps to 1.</para>
/// <para>A sample whose along-ray coordinate is at or past the band's split walks the list ordered by DESCENDING max
/// and casts its ray towards +x (+y), stopping at the first curve wholly behind the pixel. A sample before the split
/// walks the ASCENDING list and casts towards -x (-y), stopping at the first curve wholly ahead of it. Both give the
/// winding number (a closed outline crosses a line as often one way as the other), so <see cref="ListChoice"/> lets
/// a test force either, or skip the bands for every curve, and require the same answer.</para>
/// <para>Everything is single precision, as on a GPU.</para>
/// </remarks>
internal static class SlugCoverageOracle
{
    /// <summary>Which curves a sample walks.</summary>
    public enum ListChoice
    {
        /// <summary>The band's split picks the list, as the shader does.</summary>
        Split,
        /// <summary>Always the descending list (ray towards +x / +y).</summary>
        Descending,
        /// <summary>Always the ascending list (ray towards -x / -y).</summary>
        Ascending,
        /// <summary>Every curve of the glyph with no band and no early-out: what the answer is without the acceleration.</summary>
        AllCurves,
    }

    private const float Epsilon = 1f / 65536f;

    /// <summary>
    /// Coverage in [0, 1] of the glyph under nonzero winding at (<paramref name="x"/>, <paramref name="y"/>), em units
    /// with y up, for a pixel <c>1 / pixelsPerEm</c> of an em wide.
    /// </summary>
    public static float Coverage(SlugGlyphBlob blob, float x, float y, float pixelsPerEm, ListChoice choice = ListChoice.Split)
        => Coverage(blob, x, y, pixelsPerEm, pixelsPerEm, choice);

    /// <summary>
    /// <see cref="Coverage(SlugGlyphBlob, float, float, float, ListChoice)"/> for a pixel that spans a different em along
    /// x than along y (a glyph stretched horizontally): <paramref name="pixelsPerEmX"/> pixels to an em along x,
    /// <paramref name="pixelsPerEmY"/> along y. The shader takes these from the derivatives of the em coordinate.
    /// </summary>
    public static float Coverage(SlugGlyphBlob blob, float x, float y, float pixelsPerEmX, float pixelsPerEmY,
        ListChoice choice = ListChoice.Split)
    {
        var w = blob.Words;
        if (blob.IsEmpty) return 0f;

        Axis(w, true, x, y, pixelsPerEmX, choice, out float xcov, out float xwgt);
        Axis(w, false, x, y, pixelsPerEmY, choice, out float ycov, out float ywgt);

        // Lengyel's CalcCoverage: the weighted mean of the two rays, but never below the lesser of the two, which is what
        // keeps a thin stem that one ray grazes from fading.
        float coverage = MathF.Max(
            MathF.Abs(xcov * xwgt + ycov * ywgt) / MathF.Max(xwgt + ywgt, Epsilon),
            MathF.Min(MathF.Abs(xcov), MathF.Abs(ycov)));
        return Saturate(coverage);
    }

    /// <summary>
    /// A pixel grid: pixel (i, j) has its centre at em (<paramref name="originX"/> + (i + 0.5) / ppe,
    /// <paramref name="originY"/> - (j + 0.5) / ppe), so row 0 is the top. Row-major.
    /// </summary>
    public static float[] Render(SlugGlyphBlob blob, float pixelsPerEm, float originX, float originY, int width, int height,
        ListChoice choice = ListChoice.Split)
    {
        var img = new float[width * height];
        for (int j = 0; j < height; j++)
            for (int i = 0; i < width; i++)
                img[j * width + i] = Coverage(blob, originX + (i + 0.5f) / pixelsPerEm, originY - (j + 0.5f) / pixelsPerEm, pixelsPerEm, choice);
        return img;
    }

    private static float Saturate(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    // One ray. The vertical ray is the horizontal one on the curves with x and y swapped (u = y, v = x); the swap is a
    // reflection, so it flips the sign of every contribution, and the vertical sums are negated to agree with the
    // horizontal ones about the winding direction.
    private static void Axis(int[] w, bool horizontal, float sx, float sy, float ppe, ListChoice choice, out float cov, out float wgt)
    {
        cov = 0f;
        wgt = 0f;
        int upem = w[1];
        float scale = 1f / (SlugGlyphEncoder.UnitsPerFontUnit * upem); // blob units to em
        int hBands = w[3], vBands = w[4], bandOffset = w[5], curveOffset = w[7];

        // The sample in the ray's own frame: u along the ray, v across it.
        float su = horizontal ? sx : sy, sv = horizontal ? sy : sx;

        if (choice == ListChoice.AllCurves)
        {
            for (int c = 0; c < w[2]; c++)
                Walk(w, curveOffset + c * SlugGlyphBlob.CurveWords, horizontal, scale, su, sv, ppe, false, false, ref cov, ref wgt);
            if (!horizontal) cov = -cov;
            return;
        }

        // The band: the glyph's extent across the ray split into equal strips, clamped so a sample half a pixel outside the
        // glyph, where the quad is expanded to, still reads the end band.
        int bands = horizontal ? hBands : vBands;
        int crossMin = horizontal ? w[9] : w[8], crossMax = horizontal ? w[11] : w[10];
        double vBlob = sv / (double)scale;
        long span = crossMax - crossMin;
        int band = span <= 0 ? 0 : (int)Math.Clamp(Math.Floor((vBlob - crossMin) * bands / span), 0, bands - 1);

        int header = bandOffset + (horizontal ? band : hBands + band) * SlugGlyphBlob.BandHeaderWords;
        int count = w[header], descStart = w[header + 1], ascStart = w[header + 2], split = w[header + 3];
        double uBlob = su / (double)scale;

        bool descending = choice switch
        {
            ListChoice.Descending => true,
            ListChoice.Ascending => false,
            _ => uBlob >= split,
        };
        int start = descending ? descStart : ascStart;
        for (int k = 0; k < count; k++)
        {
            // The early-out is inside Walk: it reports a curve wholly behind (ahead of) the pixel by returning false.
            if (!Walk(w, w[start + k], horizontal, scale, su, sv, ppe, !descending, true, ref cov, ref wgt)) break;
        }
        if (!horizontal) cov = -cov;
    }

    // One curve against the ray through (su, sv). mirrored casts the ray towards -u instead (the curve's u negated, and
    // the sums negated back); earlyOut ends the walk at a curve that cannot contribute and, in a sorted list, nor can any
    // after it. False when it did.
    private static bool Walk(int[] w, int curve, bool horizontal, float scale, float su, float sv, float ppe, bool mirrored,
        bool earlyOut, ref float cov, ref float wgt)
    {
        // The three points relative to the sample, in em, in the ray's frame.
        int a = horizontal ? 0 : 1, b = horizontal ? 1 : 0;
        float p1u = w[curve + a] * scale - su, p1v = w[curve + b] * scale - sv;
        float p2u = w[curve + 2 + a] * scale - su, p2v = w[curve + 2 + b] * scale - sv;
        float p3u = w[curve + 4 + a] * scale - su, p3v = w[curve + 4 + b] * scale - sv;
        if (mirrored)
        {
            p1u = -p1u; p2u = -p2u; p3u = -p3u;
        }

        // Wholly behind the pixel's footprint along the ray: this curve, and in the sorted list every later one, adds 0.
        if (earlyOut && MathF.Max(MathF.Max(p1u, p2u), p3u) * ppe < -0.5f) return false;

        uint code = RootCode(p1v, p2v, p3v);
        if (code == 0) return true;

        // Solve a t^2 - 2 b t + p1 = 0 in v, the ray's across coordinate, then read u(t) at the roots.
        float au = p1u - 2f * p2u + p3u, av = p1v - 2f * p2v + p3v;
        float bu = p1u - p2u, bv = p1v - p2v;
        float t1, t2;
        if (MathF.Abs(av) < Epsilon)
        {
            // Nearly linear (a line, or a quadratic whose control is its midpoint): t = p1 / (2 b).
            t1 = t2 = p1v * (0.5f / bv);
        }
        else
        {
            float ra = 1f / av;
            float d = MathF.Sqrt(MathF.Max(bv * bv - av * p1v, 0f));
            t1 = (bv - d) * ra;
            t2 = (bv + d) * ra;
        }
        float r1 = ((au * t1 - bu * 2f) * t1 + p1u) * ppe;
        float r2 = ((au * t2 - bu * 2f) * t2 + p1u) * ppe;

        // The first root adds, the second subtracts; mirrored, the contributions are those of the ray the other way and
        // the sign is restored, which is the same as flipping the pair.
        if ((code & 1u) != 0)
        {
            cov += mirrored ? -Saturate(r1 + 0.5f) : Saturate(r1 + 0.5f);
            wgt = MathF.Max(wgt, Saturate(1f - MathF.Abs(r1) * 2f));
        }
        if (code > 1u)
        {
            cov -= mirrored ? -Saturate(r2 + 0.5f) : Saturate(r2 + 0.5f);
            wgt = MathF.Max(wgt, Saturate(1f - MathF.Abs(r2) * 2f));
        }
        return true;
    }

    // Lengyel's root-eligibility table: from the signs of the three points' across coordinates (a set bit is negative),
    // bit 0 of the result says the first root counts and bit 8 the second. 0x2E74 packs the eight cases for each.
    private static uint RootCode(float y1, float y2, float y3)
    {
        uint shift = (y1 < 0f ? 1u : 0u) | (y2 < 0f ? 2u : 0u) | (y3 < 0f ? 4u : 0u);
        return (0x2E74u >> (int)shift) & 0x0101u;
    }
}
