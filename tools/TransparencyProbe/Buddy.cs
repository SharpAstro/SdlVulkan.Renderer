using System.Diagnostics;
using DIR.Lib;
using SdlVulkan.Renderer;
using static SDL3.SDL;

namespace TransparencyProbe;

/// <summary>
/// "buddy": the library's transparent-window API, used the way a consumer uses it: a sprite with an opaque body and
/// a translucent halo, in a transparent window cut to the halo's silhouette with <c>SetShape</c>, over the
/// <see cref="Backdrop"/>. Scores the corner (outside the shape), the halo and the body, and where clicks go, and
/// times the frames.
/// </summary>
/// <remarks>
/// <c>app</c> goes through <c>SdlVulkanApp</c>: an ordinary hidden window first (which makes the shared device), then
/// <c>CreateTransparentWindow</c> and a context on <c>DeviceFor</c>. <c>standalone</c> goes through
/// <c>SdlVulkanWindow.CreateTransparent</c> and <c>VulkanContext.CreateTransparent</c>. One per run: both end in
/// <c>SDL_Quit</c>. To see the fallback without Dozen, hide it from the Vulkan loader for the run:
/// <c>$env:VK_LOADER_DRIVERS_DISABLE='*dzn*'</c>. <c>native</c> skips the second device with Dozen still installed:
/// <c>app</c> builds the context on <c>app.Device</c>, <c>standalone</c> passes <c>nativeDeviceOnly</c>, and on
/// Windows the window is then a layered one, which needs no shape, so none is set (its alpha decides where clicks
/// go). <c>opaque</c> gives the window an ordinary context (<c>CreateForSharedDevice</c>, <c>VulkanContext.Create</c>):
/// an opaque swapchain, the hard-edged companion the shape alone makes.
/// </remarks>
internal static class Buddy
{
    internal const int W = 240, H = 240;
    private const int OrangeR = 240, OrangeG = 176, OrangeB = 48;
    private const byte HaloAlpha = 100;

    public static int Run(string mode, string variant, string outDir, bool look)
    {
        if (mode is not ("app" or "standalone") || variant is not ("" or "native" or "opaque"))
        {
            Console.Error.WriteLine("buddy: app or standalone, then optionally native or opaque");
            return 1;
        }
        if (!Init(InitFlags.Video | InitFlags.Events))
            throw new InvalidOperationException(GetError());

        var backdrop = new Backdrop(W, H);
        if (Score.IsBlack(backdrop.Before))
        {
            Console.WriteLine("The screen captured black before anything was opened: is the display on, and the session visible?");
            backdrop.Dispose();
            Quit();
            return 3;
        }

        SdlVulkanApp? app = null;
        SdlVulkanWindow? plain = null;
        VulkanContext? plainCtx = null;
        SdlVulkanWindow buddy;
        VulkanContext ctx;
        if (mode == "app")
        {
            app = SdlVulkanApp.Create();
            plain = app.CreateWindow("ordinary", 320, 200, maximized: false, hidden: true);
            plainCtx = VulkanContext.CreateForSharedDevice(app.DeviceFor(plain), plain.Surface, 320, 200);
            buddy = app.CreateTransparentWindow("buddy", W, H);
            var device = variant == "" ? app.DeviceFor(buddy) : app.Device;
            Console.WriteLine($"DeviceFor(ordinary window) is the shared device: {app.DeviceFor(plain) == app.Device}");
            if (variant != "")
                Console.WriteLine($"{variant}: the transparent window's context is on the shared device, not DeviceFor's");
            else
                Console.WriteLine($"DeviceFor(transparent window) is the shared device: {device == app.Device}; asked again, the same device: {app.DeviceFor(buddy) == device}");
            ctx = variant == "opaque"
                ? VulkanContext.CreateForSharedDevice(device, buddy.Surface, W, H)
                : VulkanContext.CreateTransparentForSharedDevice(device, buddy, W, H);
        }
        else
        {
            buddy = SdlVulkanWindow.CreateTransparent("buddy", W, H);
            ctx = variant == "opaque"
                ? VulkanContext.Create(buddy.Instance, buddy.Surface, W, H)
                : VulkanContext.CreateTransparent(buddy, W, H, nativeDeviceOnly: variant == "native");
        }
        buddy.SetPosition(backdrop.SdlX, backdrop.SdlY);
        buddy.Raise();
        Console.WriteLine($"IsTransparent: {buddy.IsTransparent}; AlphaPresentation: {ctx.AlphaPresentation}");

        // The shape: the halo's ellipse, a pixel wider so its antialiased edge is inside.
        var shape = new RgbaImage(W, H);
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            double dx = (x + 0.5 - 120) / 101.0, dy = (y + 0.5 - 120) / 101.0;
            if (dx * dx + dy * dy <= 1)
                shape.Pixels[(y * W + x) * 4 + 3] = 255;
        }
        // A layered window's own alpha decides where clicks go, so it is left without a shape: the corner's clicks
        // passing through below are then the alpha's doing.
        Console.WriteLine(ctx.AlphaPresentation == AlphaPresentation.LayeredWindow
            ? "SetShape: not set (a layered window passes clicks through wherever alpha is 0)"
            : $"SetShape: {buddy.SetShape(shape)}");

        var renderer = new VkRenderer(ctx, W, H);
        var buddyHwnd = buddy.GetNativeWindowHandle();
        long frameTicks = 0;
        var frames = 0;
        void Frame()
        {
            PumpEvents();
            backdrop.Paint();
            backdrop.KeepBelow(buddyHwnd);
            var start = Stopwatch.GetTimestamp();
            if (!renderer.BeginFrame(new RGBAColor32(0, 0, 0, 0)))
                return;
            Draw(renderer);
            renderer.EndFrame();
            frameTicks += Stopwatch.GetTimestamp() - start;
            frames++;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 1.5)
            Frame();
        // BeginFrame to EndFrame: the draw, plus for a layered window the wait, the readback and the hand-over; for a
        // swapchain, the acquire and the present, which under Fifo wait for the display.
        Console.WriteLine($"{frames} frames in {clock.Elapsed.TotalSeconds:F2} s; BeginFrame to EndFrame "
            + $"{(frames == 0 ? 0 : frameTicks * 1000.0 / Stopwatch.Frequency / frames):F2} ms a frame");
        var (exit, captureSawAlpha) = Judge(backdrop, backdrop.Capture(), ctx.PresentsAlpha, buddyHwnd,
            variant == "" ? mode : $"{mode}-{variant}", outDir);
        if (look && exit != 3)
        {
            var saw = Eye.Ask("Through the pale ring around the face, do you see the checkerboard?", Frame);
            Console.WriteLine(Eye.Compare(saw, captureSawAlpha));
        }

        // The backdrop goes first: the library's teardown below ends in SDL_Quit.
        backdrop.Dispose();
        renderer.Dispose();
        ctx.Dispose();
        buddy.Dispose();
        plainCtx?.Dispose();
        plain?.Dispose();
        app?.Dispose();
        return exit;
    }

    /// <summary>The sprite, between a frame's begin and end: a translucent halo, an opaque body, two eyes.</summary>
    internal static void Draw(VkRenderer renderer)
    {
        renderer.FillEllipse(Box(20, 20, 220, 220), new RGBAColor32(OrangeR, OrangeG, OrangeB, HaloAlpha));
        renderer.FillEllipse(Box(50, 50, 190, 190), new RGBAColor32(OrangeR, OrangeG, OrangeB, 255));
        renderer.FillEllipse(Box(88, 92, 104, 112), new RGBAColor32(42, 33, 16, 255));
        renderer.FillEllipse(Box(136, 92, 152, 112), new RGBAColor32(42, 33, 16, 255));
    }

    private static RectInt Box(int x0, int y0, int x1, int y1) => new(new PointInt(x1, y1), new PointInt(x0, y0));

    // The exit code, and what the capture says of the halo: true blended, false opaque, null neither.
    internal static (int Exit, bool? CaptureSawAlpha) Judge(Backdrop backdrop, byte[] after, bool presentsAlpha,
        nint hwnd, string mode, string outDir)
    {
        if (Score.IsBlack(after))
        {
            Console.WriteLine("screen captured black: display off, or the Remote Desktop client minimized?");
            return (3, null);
        }
        var before = backdrop.Before;
        const double a = HaloAlpha / 255.0;
        double Orange(int i) => (i % 4) switch { 0 => OrangeB, 1 => OrangeG, _ => OrangeR };
        var corner = Score.MeanError(after, W, 2, 2, 14, 14, i => before[i]);
        var haloBlended = Score.MeanError(after, W, 26, 112, 44, 128, i => before[i] * (1 - a) + Orange(i) * a);
        var haloOpaque = Score.MeanError(after, W, 26, 112, 44, 128, i => Orange(i) * a);
        var body = Score.MeanError(after, W, 100, 150, 140, 170, Orange);
        var halo = haloBlended < 6 ? "blends with the backdrop" : haloOpaque < 6 ? "opaque (orange times alpha over black)" : "neither (look at the picture)";
        Console.WriteLine($"mean error per channel: corner vs backdrop {corner:F1}; halo vs blended {haloBlended:F1}, vs opaque {haloOpaque:F1}; body vs orange {body:F1}");
        Console.WriteLine($"halo: {halo}{(presentsAlpha || haloBlended >= 6 ? "" : " (although PresentsAlpha is false: the driver blends an OPAQUE swapchain)")}");

        string Click(int x, int y)
        {
            var at = backdrop.WindowAt(x, y);
            return at == backdrop.Hwnd ? "through" : at == hwnd ? "caught" : "elsewhere";
        }
        Console.WriteLine($"clicks: corner {Click(5, 5)}, halo {Click(35, 120)}, body {Click(120, 160)}");
        var path = Path.Combine(outDir, $"buddy-{mode}-{(presentsAlpha ? "alpha" : "opaque")}.png");
        Score.WriteSideBySide(path, before, after, W, H);
        Console.WriteLine($"picture: {path} (backdrop alone | with the window)");

        var ok = corner < 3 && body < 3 && (haloBlended < 6 || (!presentsAlpha && haloOpaque < 6))
            && Click(5, 5) == "through" && Click(35, 120) == "caught";
        Console.WriteLine(ok ? "VERDICT: as expected" : "VERDICT: NOT as expected");
        return (ok ? 0 : 2, haloBlended < 6 ? true : haloOpaque < 6 ? false : null);
    }
}
