namespace SdlVulkan.Renderer;

/// <summary>A rectangle of an image, in texels.</summary>
internal readonly record struct CopyRect(int X, int Y, int Width, int Height);

/// <summary>
/// What region a buffer→image copy is actually allowed to touch.
///
/// <para>A queue family advertises <c>minImageTransferGranularity</c>, and the offset and extent of
/// every <c>vkCmdCopyBufferToImage</c> region must respect it (VUID-vkCmdCopyBufferToImage-imageOffset-07738).
/// Two values matter. <b>(1,1,1)</b> — what desktop drivers report — permits any rectangle, which is
/// why uploading a dirty band straight from an atlas has always worked. <b>(0,0,0)</b> means the queue
/// can copy <i>whole subresources only</i>: the offset must be zero and the extent must be the image's
/// own. Mesa's <c>dzn</c> (Vulkan over D3D12, the only hardware Vulkan a WSL guest can reach) reports
/// (0,0,0), and the atlases were handing it partial rows — undefined behaviour that validation
/// reports as an error and that a driver is free to turn into corruption.</para>
///
/// <para>Anything in between is a real device too (some mobile parts report (4,4,1) for compressed
/// or multi-planar formats), so the rule snaps rather than special-casing: the offset rounds DOWN to
/// a multiple, the far edge rounds UP, and a rect that reaches the image edge is left there, which
/// the spec permits explicitly. Widening is always safe here because both atlases keep the whole
/// page CPU-side and re-copy the snapped rect from it.</para>
/// </summary>
internal static class ImageTransferGranularity
{
    /// <summary>
    /// The rectangle to upload for a given dirty rectangle. Never smaller than the dirty rect, never
    /// larger than the image, and always legal for a queue with this granularity.
    /// </summary>
    public static CopyRect Snap(CopyRect dirty, uint granularityWidth, uint granularityHeight,
                                int imageWidth, int imageHeight)
    {
        // Whole-subresource only. The whole image is the only legal region, and it contains
        // whatever was dirty.
        if (granularityWidth == 0 || granularityHeight == 0)
            return new CopyRect(0, 0, imageWidth, imageHeight);

        // The common case, kept free of arithmetic: every rectangle is already legal.
        if (granularityWidth == 1 && granularityHeight == 1)
            return dirty;

        var gw = (int)granularityWidth;
        var gh = (int)granularityHeight;

        var x0 = dirty.X - dirty.X % gw;
        var y0 = dirty.Y - dirty.Y % gh;

        var x1 = dirty.X + dirty.Width;
        var y1 = dirty.Y + dirty.Height;
        // Round the far edge up to a multiple, but the image's own edge is legal at any granularity,
        // so clamp there rather than overrunning it.
        x1 = Math.Min(imageWidth, x1 % gw == 0 ? x1 : x1 + (gw - x1 % gw));
        y1 = Math.Min(imageHeight, y1 % gh == 0 ? y1 : y1 + (gh - y1 % gh));

        return new CopyRect(x0, y0, x1 - x0, y1 - y0);
    }
}
