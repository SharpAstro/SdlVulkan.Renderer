namespace SdlVulkan.Renderer;

/// <summary>
/// A picture as decoded Y'CbCr planes, for <see cref="VkTexture.CreateDeferredFromYCbCr"/> to convert on the GPU
/// into the linear-light float texture <see cref="VkRenderer.DrawHdrImage"/> takes. The renderer knows no codec: a
/// decoder hands over its planes, how its matrix and range read them, and what each R'G'B' code means in light.
/// </summary>
/// <remarks>
/// The conversion is H.273 8.3 in code units at <see cref="BitDepth"/>, per pixel: chroma repeated (no filter);
/// full range takes Y' as is and centres the chroma on 2^(BitDepth-1), narrow range expands them by 219 and 224
/// steps; R' = Y' + 2(1 - Kr)Cr, B' = Y' + 2(1 - Kb)Cb, G' = (Y' - Kr R' - Kb B') / (1 - Kr - Kb), each rounded half
/// up and clamped; then <see cref="CodeToLinear"/> per channel. That is the conversion SharpAstro.Heif's
/// <c>HeifYCbCrImage.ToRgb</c> and <c>ToLinearRgbaFloat</c> do on the CPU, so a GPU result matches it to within a
/// code where float rounding falls the other way. A transfer that is not per channel (HLG) cannot be expressed
/// here and stays on the CPU.
/// </remarks>
public sealed class YCbCrPlanes
{
    /// <summary>Width of the luma plane and of the picture, in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the luma plane and of the picture, in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Bits per sample of every plane, 8 to 16; <see cref="CodeToLinear"/> has 2^BitDepth entries.</summary>
    public required int BitDepth { get; init; }

    /// <summary>Y', <see cref="Width"/> x <see cref="Height"/>, row-major, top row first.</summary>
    public required ReadOnlyMemory<ushort> Luma { get; init; }

    /// <summary>Cb, <see cref="ChromaWidth"/> samples a row; empty for a monochrome picture.</summary>
    public ReadOnlyMemory<ushort> Cb { get; init; }

    /// <summary>Cr, laid out as <see cref="Cb"/>; empty for a monochrome picture.</summary>
    public ReadOnlyMemory<ushort> Cr { get; init; }

    /// <summary>Samples in a chroma row. Unused for a monochrome picture.</summary>
    public int ChromaWidth { get; init; }

    /// <summary>log2 of the horizontal chroma subsampling: 1 for 4:2:0 and 4:2:2, 0 for 4:4:4.</summary>
    public int ChromaShiftX { get; init; }

    /// <summary>log2 of the vertical chroma subsampling: 1 for 4:2:0, 0 for 4:2:2 and 4:4:4.</summary>
    public int ChromaShiftY { get; init; }

    /// <summary>Red luma weight of the matrix (0.2627 for BT.2020, 0.2126 for BT.709).</summary>
    public required double Kr { get; init; }

    /// <summary>Blue luma weight of the matrix (0.0593 for BT.2020, 0.0722 for BT.709).</summary>
    public required double Kb { get; init; }

    /// <summary>The identity matrix (GBR, 4:4:4 only): R' = Cr, G' = Y', B' = Cb.</summary>
    public bool IdentityMatrix { get; init; }

    /// <summary>Whether the samples are full range (else narrow, "video", range).</summary>
    public required bool FullRange { get; init; }

    /// <summary>
    /// Linear light for each R'G'B' code, 2^<see cref="BitDepth"/> entries, in whatever units the texture is to be
    /// drawn in (for <see cref="VkRenderer.DrawHdrImage"/>, 1.0 = 203 cd/m2).
    /// </summary>
    public required ReadOnlyMemory<float> CodeToLinear { get; init; }

    /// <summary>True when there are no chroma planes.</summary>
    public bool IsMonochrome => Cb.IsEmpty;

    internal int ChromaHeight => (Height + (1 << ChromaShiftY) - 1) >> ChromaShiftY;

    internal void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentException($"A {Width}x{Height} picture has no pixels.");
        if (BitDepth is < 8 or > 16)
            throw new ArgumentException($"Bit depth {BitDepth} is outside 8 to 16.");
        if (Luma.Length != (long)Width * Height)
            throw new ArgumentException($"The luma plane holds {Luma.Length} samples, not {Width}x{Height}.");
        if (CodeToLinear.Length != 1 << BitDepth)
            throw new ArgumentException($"The code table holds {CodeToLinear.Length} entries, not 2^{BitDepth}.");
        if (IsMonochrome)
        {
            if (!Cr.IsEmpty) throw new ArgumentException("Cr without Cb.");
            if (IdentityMatrix) throw new ArgumentException("The identity matrix needs chroma planes.");
            return;
        }
        if (ChromaShiftX is < 0 or > 1 || ChromaShiftY is < 0 or > 1)
            throw new ArgumentException($"Chroma subsampling 2^{ChromaShiftX} x 2^{ChromaShiftY} is not one H.265 has.");
        if (IdentityMatrix && (ChromaShiftX | ChromaShiftY) != 0)
            throw new ArgumentException("The identity matrix needs 4:4:4 samples.");
        if (ChromaWidth < (Width + (1 << ChromaShiftX) - 1) >> ChromaShiftX)
            throw new ArgumentException($"A chroma row of {ChromaWidth} samples cannot cover {Width} pixels at shift {ChromaShiftX}.");
        long chroma = (long)ChromaWidth * ChromaHeight;
        if (Cb.Length != chroma || Cr.Length != chroma)
            throw new ArgumentException($"The chroma planes hold {Cb.Length} and {Cr.Length} samples, not {ChromaWidth}x{ChromaHeight}.");
        if (!IdentityMatrix && Math.Abs(1 - Kr - Kb) < 1e-6)
            throw new ArgumentException("Kr + Kb must leave a green weight.");
    }
}
