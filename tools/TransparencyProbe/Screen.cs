using System.IO.Compression;
using System.Runtime.InteropServices;
using static SDL3.SDL;

namespace TransparencyProbe;

/// <summary>
/// A known picture to look through: a checkerboard window drawn with SDL's own renderer, always on top, in the
/// middle of the primary display. Whatever transparent window is opened over it afterwards is judged by comparing
/// a capture of the screen with the window against <see cref="Before"/>, the capture without it.
/// </summary>
internal sealed class Backdrop : IDisposable
{
    public const int CheckerSize = 20;
    private readonly nint _window;
    private readonly nint _renderer;

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public nint Hwnd { get; }

    /// <summary>The screen over the backdrop, before anything is opened on top of it. BGRA, top-down.</summary>
    public byte[] Before { get; }

    public Backdrop(int width, int height)
    {
        Width = width;
        Height = height;
        GetDisplayUsableBounds(GetPrimaryDisplay(), out var usable);
        var x = usable.X + (usable.W - width) / 2;
        var y = usable.Y + (usable.H - height) / 2;
        _window = CreateWindow("TransparencyProbe backdrop", width, height, WindowFlags.Borderless | WindowFlags.AlwaysOnTop);
        SetWindowPosition(_window, x, y);
        _renderer = CreateRenderer(_window, null!);
        for (var i = 0; i < 20; i++)
        {
            PumpEvents();
            Paint();
            Thread.Sleep(16);
        }
        Hwnd = GetPointerProperty(GetWindowProperties(_window), Props.WindowWin32HWNDPointer, 0);
        Win32.GetWindowRect(Hwnd, out var rect);
        // The position SDL took, in screen pixels: where every window opened over it goes, and what is captured.
        X = rect.Left;
        Y = rect.Top;
        GetWindowPosition(_window, out var sdlX, out var sdlY);
        SdlX = sdlX;
        SdlY = sdlY;
        Before = Capture();
    }

    /// <summary>The backdrop's position in SDL's window coordinates, for placing a window over it.</summary>
    public int SdlX { get; }
    public int SdlY { get; }

    public void Paint()
    {
        SetRenderDrawColor(_renderer, 235, 235, 235, 255);
        RenderClear(_renderer);
        SetRenderDrawColor(_renderer, 30, 90, 200, 255);
        for (var y = 0; y < Height; y += CheckerSize)
        for (var x = (y / CheckerSize % 2) * CheckerSize; x < Width; x += 2 * CheckerSize)
        {
            var r = new FRect { X = x, Y = y, W = CheckerSize, H = CheckerSize };
            RenderFillRect(_renderer, in r);
        }
        RenderPresent(_renderer);
    }

    /// <summary>
    /// Puts the backdrop back directly under <paramref name="window"/>, the window being judged, if it has come in
    /// front of it. Both are always on top, and among always-on-top windows the one raised last is in front: on the
    /// Surface (2026-10-14) the mouse merely passing over the checkerboard raised it over the window under test, without
    /// a click and without activating it, so a person looking, or a capture, saw checkerboard where the window
    /// should have been. Call it every frame; it moves the backdrop only when it is out of place.
    /// </summary>
    public void KeepBelow(nint window)
    {
        // Walk up from the backdrop: meeting the window means the backdrop is below it, as it should be.
        for (var h = Win32.GetWindow(Hwnd, Win32.GwHwndPrev); h != 0; h = Win32.GetWindow(h, Win32.GwHwndPrev))
            if (h == window)
                return;
        // Inserted after another always-on-top window, the backdrop stays always on top, so nothing else covers it.
        Win32.SetWindowPos(Hwnd, window, 0, 0, 0, 0, Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
    }

    public byte[] Capture() => Win32.CaptureScreen(X, Y, Width, Height);

    /// <summary>Which top-level window a click at (x, y), relative to the backdrop, would reach.</summary>
    public nint WindowAt(int x, int y)
        => Win32.GetAncestor(Win32.WindowFromPoint(new Win32.Point { X = X + x, Y = Y + y }), 2 /* GA_ROOT */);

    public void Dispose()
    {
        DestroyRenderer(_renderer);
        DestroyWindow(_window);
    }
}

/// <summary>
/// The person in front of the panel, for when the capture may not show what the panel does. GDI reads the
/// compositor's picture; the panel shows what the display hardware scans out, and nothing promises the two agree:
/// the same trap as HDR (tools/HdrProbe), where only the panel can judge. On the Surface (Adreno X1-85, 2026-10-14)
/// they did agree. The person watching had first remembered seeing the backdrop through every picture, which would
/// have contradicted the capture's "opaque" for the native driver; asked picture by picture, they saw the native
/// driver's window opaque and Dozen's blend, as the capture had said.
/// </summary>
internal static class Eye
{
    // Long enough for a person to walk up, look and decide: a person sets the pace here, not the probe.
    private const double TimeoutSeconds = 600;

    /// <summary>
    /// Asks <paramref name="question"/> and waits for Y or N in one of the probe's windows (Escape, or 10 minutes,
    /// for no answer), running <paramref name="frame"/> meanwhile so the picture stays up. True for yes.
    /// </summary>
    public static bool? Ask(string question, Action frame)
    {
        Console.WriteLine($"  LOOK: {question} Y or N, in the picture (click it first if the keys do nothing).");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < TimeoutSeconds)
        {
            while (PollEvent(out var e))
            {
                if ((EventType)e.Type != EventType.KeyDown)
                    continue;
                if (e.Key.Key == Keycode.Y) return true;
                if (e.Key.Key == Keycode.N) return false;
                if (e.Key.Key == Keycode.Escape) return null;
            }
            frame();
        }
        return null;
    }

    /// <summary>"eye: alpha", "eye: opaque" or "eye: -", and a warning when it contradicts the capture's verdict.</summary>
    public static string Compare(bool? eyeSawAlpha, bool? captureSawAlpha)
    {
        var eye = eyeSawAlpha switch { true => "alpha", false => "opaque", null => "-" };
        var clash = eyeSawAlpha is { } e && captureSawAlpha is { } c && e != c;
        return clash ? $"eye: {eye}  <-- THE CAPTURE SAYS OTHERWISE" : $"eye: {eye}";
    }
}

/// <summary>Scoring a capture against a prediction.</summary>
internal static class Score
{
    /// <summary>True when GDI handed back nothing: a sleeping panel, a locked or minimized Remote Desktop session.</summary>
    public static bool IsBlack(byte[] bgra) => bgra.All(b => b == 0);

    /// <summary>
    /// Mean absolute error per colour channel over a rectangle of a BGRA capture <paramref name="width"/> pixels wide,
    /// against <paramref name="expect"/>, given the byte's index (so i % 4 is the channel: 0 blue, 1 green, 2 red).
    /// </summary>
    public static double MeanError(byte[] after, int width, int x0, int y0, int x1, int y1, Func<int, double> expect)
    {
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var i = (y * width + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                sum += Math.Abs(after[i + c] - expect(i + c));
                n++;
            }
        }
        return sum / n;
    }

    /// <summary>Two captures side by side, the backdrop alone and with the window over it, as a PNG.</summary>
    public static void WriteSideBySide(string path, byte[] before, byte[] after, int width, int height)
    {
        var both = new byte[width * 2 * height * 4];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(before, y * width * 4, both, y * width * 2 * 4, width * 4);
            Array.Copy(after, y * width * 4, both, (y * width * 2 + width) * 4, width * 4);
        }
        Png.Write(path, both, width * 2, height);
    }
}

internal static class Png
{
    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>A BGRA buffer as an RGB PNG (the capture's alpha means nothing).</summary>
    public static void Write(string path, byte[] bgra, int w, int h)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < h; y++)
            {
                z.WriteByte(0);
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    z.WriteByte(bgra[i + 2]);
                    z.WriteByte(bgra[i + 1]);
                    z.WriteByte(bgra[i]);
                }
            }
        using var f = File.Create(path);
        f.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w);
        BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: RGB
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        var c = 0xFFFFFFFFu;
        foreach (var b in head.AsSpan(4)) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var tail = new byte[4];
        BigEndian(tail, 0, c ^ 0xFFFFFFFFu);
        s.Write(tail);
    }

    private static void BigEndian(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}

internal static unsafe partial class Win32
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RectL { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
    }

    public const int GwlExStyle = -20;
    public const uint WsExLayered = 0x80000, WsExNoRedirectionBitmap = 0x200000, WsExTopmost = 0x8;
    public const uint WsPopup = 0x80000000, WsVisible = 0x10000000;
    public const uint GwHwndPrev = 3;
    public const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(nint hwnd, out RectL rect);
    [LibraryImport("user32.dll")] public static partial nint WindowFromPoint(Point p);
    [LibraryImport("user32.dll")] public static partial nint GetWindow(nint hwnd, uint cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial ushort RegisterClassW(WndClassW* wc);
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint GetModuleHandleW(string? name);
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)] public static partial nint GetProcAddress(nint module, string name);
    [LibraryImport("kernel32.dll")] public static partial uint SetThreadExecutionState(uint flags);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* bmi, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    /// <summary>Keeps the display on (and wakes it) while the tool runs: a powered-down panel is not composed.</summary>
    public static void KeepDisplayOn() => SetThreadExecutionState(0x80000000u /* CONTINUOUS */ | 0x2u /* DISPLAY */ | 0x1u /* SYSTEM */);

    /// <summary>The composed desktop, as DWM hands it to GDI: BGRA, top-down, alpha meaningless.</summary>
    public static byte[] CaptureScreen(int x, int y, int w, int h)
    {
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        BitmapInfoHeader bmi = new() { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(mem, &bmi, 0, out var bits, 0, 0);
        var old = SelectObject(mem, dib);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
        var result = new byte[w * h * 4];
        Marshal.Copy(bits, result, 0, result.Length);
        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(0, screen);
        return result;
    }
}
