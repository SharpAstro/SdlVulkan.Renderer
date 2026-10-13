namespace SdlVulkan.Renderer;

/// <summary>A rectangle in window coordinates, which are desktop coordinates for a window's position.</summary>
internal readonly record struct WindowRect(int X, int Y, int Width, int Height);

/// <summary>
/// Where a window that changes its own size goes: the geometry of <see cref="SdlVulkanWindow.ResizeInPlace(int, int)"/>,
/// kept apart from SDL so it can be tested without a display.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>Where a window of <paramref name="width"/> x <paramref name="height"/> goes in place of
    /// <paramref name="current"/>. The corner nearest the display's own corner stays put, so a window parked bottom
    /// right grows up and to the left; the result is kept inside <paramref name="usable"/> (the display less its
    /// taskbar).</summary>
    public static WindowRect Place(WindowRect current, int width, int height, WindowRect usable)
    {
        var w = Math.Min(width, usable.Width);
        var h = Math.Min(height, usable.Height);
        var anchorRight = current.X + current.Width / 2 > usable.X + usable.Width / 2;
        var anchorBottom = current.Y + current.Height / 2 > usable.Y + usable.Height / 2;
        var x = anchorRight ? current.X + current.Width - w : current.X;
        var y = anchorBottom ? current.Y + current.Height - h : current.Y;
        x = Math.Clamp(x, usable.X, usable.X + usable.Width - w);
        y = Math.Clamp(y, usable.Y, usable.Y + usable.Height - h);
        return new WindowRect(x, y, w, h);
    }
}
