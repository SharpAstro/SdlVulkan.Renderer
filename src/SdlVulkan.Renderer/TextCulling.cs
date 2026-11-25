using System;
using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// Whether a text can put any ink inside the region a pass may paint, asked BEFORE the text is shaped.
/// </summary>
/// <remarks>
/// <para>
/// A frame that repaints part of a window (<c>VulkanContext.Damage.cs</c>) still has the app draw everything: the
/// scissor is what confines the paint. For rectangles that costs nothing worth saving. For text it is the frame: every
/// label is shaped, measured and batched glyph by glyph, and then discarded pixel by pixel. An atlas repainting its
/// clock's rectangle once a second spent three quarters of that frame's render time on labels nowhere near the clock
/// (TianWen, 2026-11-25).
/// </para>
/// <para>
/// The bound is deliberately generous, since a text wrongly dropped is a hole in the repainted region and a text
/// wrongly kept costs what it always did. No glyph advances more than two ems, so a line of <c>n</c> UTF-16 units is
/// no wider than <c>2 * n * fontSize</c>, placed by its alignment and padded by an em for bearings and overshoot;
/// and the block of lines is padded by an em above and below for ascent and descent. The whole text's length stands
/// in for its longest line.
/// </para>
/// </remarks>
internal static class TextCulling
{
    /// <summary>The widest a glyph is taken to advance, in ems.</summary>
    internal const float MaxAdvanceEm = 2f;

    /// <param name="textLength">The text's length in UTF-16 units, an upper bound on its longest line's glyphs.</param>
    /// <param name="lineCount">Its number of lines.</param>
    /// <param name="fontSize">The font size, in pixels.</param>
    /// <param name="lineHeight">The height of one line, in pixels.</param>
    /// <param name="horizontal">How a line is placed across the layout box.</param>
    /// <param name="layoutX">The layout box, in the pass's pixels.</param>
    /// <param name="layoutY">The layout box, in the pass's pixels.</param>
    /// <param name="layoutW">The layout box, in the pass's pixels.</param>
    /// <param name="layoutH">The layout box, in the pass's pixels.</param>
    /// <param name="regionX">The region the pass may paint.</param>
    /// <param name="regionY">The region the pass may paint.</param>
    /// <param name="regionW">The region the pass may paint.</param>
    /// <param name="regionH">The region the pass may paint.</param>
    internal static bool MayReach(
        int textLength, int lineCount, float fontSize, float lineHeight, TextAlign horizontal,
        float layoutX, float layoutY, float layoutW, float layoutH,
        int regionX, int regionY, int regionW, int regionH)
    {
        // Anything not finite is not for this rule to judge.
        if (!float.IsFinite(layoutX) || !float.IsFinite(layoutY) || !float.IsFinite(layoutW) || !float.IsFinite(layoutH)
            || !(fontSize > 0f) || !float.IsFinite(fontSize))
        {
            return true;
        }

        var pad = fontSize;
        var widest = MaxAdvanceEm * textLength * fontSize;
        float x0, x1;
        switch (horizontal)
        {
            case TextAlign.Near:
                x0 = layoutX - pad;
                x1 = layoutX + widest + pad;
                break;
            case TextAlign.Far:
                x0 = layoutX + layoutW - widest - pad;
                x1 = layoutX + layoutW + pad;
                break;
            default:
                var centre = layoutX + (layoutW * 0.5f);
                x0 = centre - (widest * 0.5f) - pad;
                x1 = centre + (widest * 0.5f) + pad;
                break;
        }

        // Wherever the block sits in the box (top, middle, bottom), it lies between these.
        var tall = lineCount * lineHeight;
        var y0 = MathF.Min(layoutY, layoutY + layoutH - tall) - pad;
        var y1 = MathF.Max(layoutY + layoutH, layoutY + tall) + pad;

        return x1 > regionX && x0 < regionX + regionW && y1 > regionY && y0 < regionY + regionH;
    }
}
