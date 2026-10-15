using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SdlVulkan.Renderer;

/// <summary>
/// Where a layered context's frames go (see <c>VulkanContext.Layered.cs</c>): the window, as far as the context needs
/// to know it. <see cref="LayeredWindow"/> on Windows; the tests stand in for it.
/// </summary>
internal interface ILayeredTarget : IDisposable
{
    /// <summary>The window's client area now, in pixels: the size a frame for it must have.</summary>
    (uint Width, uint Height) PixelSize { get; }

    /// <summary>
    /// Shows one finished frame: premultiplied BGRA, rows top-down and tightly packed. False when it was not shown,
    /// because the window changed size after the frame began or the system refused it.
    /// </summary>
    bool Present(ReadOnlySpan<byte> bgra, uint width, uint height);
}

/// <summary>
/// A window made layered (<c>WS_EX_LAYERED</c>) and fed with <c>UpdateLayeredWindow</c>: every frame is a 32-bit
/// premultiplied bitmap that the window manager blends per pixel with what is behind the window, and clicks pass
/// through wherever a pixel's alpha is zero. Vulkan's presentation is not involved, so any device can draw for it,
/// which is what lets a transparent window keep soft edges on a native driver that offers no premultiplied alpha.
/// </summary>
/// <remarks>
/// The window stays layered once its context is gone. Taking the style off would hand it back to ordinary
/// redirection with nothing painted, a black flash on its way to being destroyed.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class LayeredWindow : ILayeredTarget
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const uint UlwAlpha = 0x2;
    private const byte AcSrcOver = 0, AcSrcAlpha = 1;

    private readonly nint _hwnd;
    private readonly nint _dc;
    private nint _bitmap, _previous;
    private byte* _bits;
    private uint _width, _height;
    private bool _failureLogged;
    private bool _disposed;

    public LayeredWindow(nint hwnd)
    {
        _hwnd = hwnd;
        var style = (long)GetWindowLongPtrW(hwnd, GwlExStyle);
        if ((style & WsExLayered) == 0)
            SetWindowLongPtrW(hwnd, GwlExStyle, (nint)(style | WsExLayered));
        // Compatible with the screen, which is all a DIB section selected into it needs.
        _dc = CreateCompatibleDC(nint.Zero);
    }

    public (uint Width, uint Height) PixelSize
    {
        get
        {
            GetClientRect(_hwnd, out var r);
            return ((uint)Math.Max(0, r.Right - r.Left), (uint)Math.Max(0, r.Bottom - r.Top));
        }
    }

    public bool Present(ReadOnlySpan<byte> bgra, uint width, uint height)
    {
        // The size goes to UpdateLayeredWindow, which would resize the window to the frame: a window the app resized
        // after this frame began must not be put back. The next frame begins at the new size (VulkanContext.BeginFrame
        // reports it as a swapchain reports being out of date).
        if (PixelSize != (width, height))
            return false;
        if (!EnsureBitmap(width, height))
            return false;
        bgra[..(int)(width * height * 4)].CopyTo(new Span<byte>(_bits, (int)(width * height * 4)));

        // No destination point: the window stays where it is, wherever the app or the person moved it.
        Point source = default;
        Size size = new() { Cx = (int)width, Cy = (int)height };
        BlendFunction blend = new() { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
        if (UpdateLayeredWindow(_hwnd, nint.Zero, null, &size, _dc, &source, 0, &blend, UlwAlpha))
            return true;
        if (!_failureLogged)
        {
            _failureLogged = true;
            SdlVulkanLog.Logger.LayeredWindowUpdateFailed(Marshal.GetLastPInvokeError());
        }
        return false;
    }

    // A top-down 32-bit DIB section selected into the memory DC, remade when the window's size changes.
    private bool EnsureBitmap(uint width, uint height)
    {
        if (_bitmap != nint.Zero && _width == width && _height == height)
            return true;
        ReleaseBitmap();
        BitmapInfoHeader header = new()
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = (int)width,
            Height = -(int)height,   // negative: rows top-down, as the readback has them
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = CreateDIBSection(_dc, &header, 0 /* DIB_RGB_COLORS */, out var bits, nint.Zero, 0);
        if (_bitmap == nint.Zero)
            return false;
        _bits = (byte*)bits;
        _previous = SelectObject(_dc, _bitmap);
        _width = width;
        _height = height;
        return true;
    }

    private void ReleaseBitmap()
    {
        if (_bitmap == nint.Zero)
            return;
        SelectObject(_dc, _previous);
        DeleteObject(_bitmap);
        _bitmap = nint.Zero;
        _bits = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseBitmap();
        DeleteDC(_dc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint hwnd, nint screenDc, Point* destination, Size* size,
        nint sourceDc, Point* source, uint colorKey, BlendFunction* blend, uint flags);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* header, uint usage, out nint bits,
        nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);
}
