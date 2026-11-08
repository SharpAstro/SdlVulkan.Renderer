using System.Diagnostics;
using System.Globalization;
using DIR.Lib;
using SdlVulkan.Renderer;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SdrFrameBench;

/// <summary>
/// One fixed SDR UI scene on a headless offscreen context, timed: see SdrFrameBench.csproj for what it prints and how to
/// run it against another source tree. Uses only API that exists before the HDR work, so it builds against either side
/// of a before/after.
/// </summary>
internal static class Program
{
    private const int WarmFrames = 30;

    private static int Main(string[] args)
    {
        var frames = 300;
        var msaa = 4;
        var width = 1280;
        var height = 800;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--frames": frames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--msaa": msaa = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--width": width = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--height": height = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    default: return Usage();
                }
            }
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
        {
            return Usage();
        }
        if (frames < 1 || msaa is not (1 or 2 or 4 or 8) || width < 64 || height < 64) return Usage();

        VulkanContext ctx;
        try
        {
            ctx = CreateOffscreen((uint)width, (uint)height, (VkSampleCountFlags)msaa);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"no Vulkan device: {e.Message}");
            return 2;
        }

        using (ctx)
        {
            try
            {
                Run(ctx, Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf"), width, height, frames, msaa);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }
    }

    private static unsafe VulkanContext CreateOffscreen(uint width, uint height, VkSampleCountFlags msaa)
    {
        vkInitialize().CheckResult();
        // 1.1 when the loader has it, as the window path asks (SdlVulkanWindow.InstanceApiVersion, which is internal).
        uint raw = 0;
        var version = VkVersion.Version_1_0;
        try
        {
            if (vkEnumerateInstanceVersion(&raw) == VkResult.Success && new VkVersion(raw) >= VkVersion.Version_1_1)
                version = VkVersion.Version_1_1;
        }
        catch (EntryPointNotFoundException)
        {
        }
        VkApplicationInfo appInfo = new() { apiVersion = version };
        VkInstanceCreateInfo ici = new() { pApplicationInfo = &appInfo };
        vkCreateInstance(&ici, null, out var instance).CheckResult();
        // The context owns the instance: disposing it tears down the device and the instance.
        return VulkanContext.CreateOffscreen(instance, width, height, msaaSamples: msaa);
    }

    private static unsafe void Run(VulkanContext ctx, string font, int width, int height, int frames, int msaa)
    {
        ctx.InstanceApi.vkGetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var device = VkStringInterop.ConvertToManaged(props.deviceName) ?? "<unknown>";

        using var renderer = new VkRenderer(ctx, (uint)width, (uint)height);
        using var scene = new Scene(ctx, renderer, font);

        // Warm: pipelines, descriptor sets, the vertex ring, and the glyphs of every label (the atlas takes a glyph a
        // frame late), up to a bounded number of frames.
        for (var i = 0; i < WarmFrames || (renderer.FontAtlasDirty && i < 400); i++)
        {
            Frame(ctx, renderer, scene);
            if (i >= WarmFrames) Thread.Sleep(1);
        }

        var cpu = new List<double>(frames);
        var gpu = new List<double>(frames);
        var wall = new List<double>(frames);
        for (var i = 0; i < frames; i++)
        {
            var (c, g, w) = Frame(ctx, renderer, scene);
            cpu.Add(c);
            gpu.Add(g);
            wall.Add(w);
        }

        Console.WriteLine($"device: {device}  (GPU timestamps: {(ctx.GpuTimingSupported ? "yes" : "no")}, MSAA x{msaa})");
        Console.WriteLine($"scene: {width}x{height}, {scene.Describe()}, {frames} timed frames after {WarmFrames}+ warm");
        Console.WriteLine("                          mean ms     p95 ms");
        Console.WriteLine($"  CPU  (record+submit)  {Num(Mean(cpu)),10}  {Num(P95(cpu)),9}");
        Console.WriteLine($"  GPU  (timestamps)     {Num(Mean(gpu)),10}  {Num(P95(gpu)),9}");
        Console.WriteLine($"  wall (with the wait)  {Num(Mean(wall)),10}  {Num(P95(wall)),9}");
        // One line a script can read back.
        Console.WriteLine($"RESULT cpu_mean={Num(Mean(cpu))} cpu_p95={Num(P95(cpu))} gpu_mean={Num(Mean(gpu))} gpu_p95={Num(P95(gpu))} wall_mean={Num(Mean(wall))} wall_p95={Num(P95(wall))}");
    }

    // One frame: CPU time from before BeginOffscreenFrame to after EndOffscreenFrame returned (the record and the submit,
    // not the GPU's finishing), the GPU time of that frame (the wait collects its timestamps, so LastGpuFrameMs is this
    // frame's), and wall time over all of it.
    private static (double Cpu, double Gpu, double Wall) Frame(VulkanContext ctx, VkRenderer renderer, Scene scene)
    {
        var start = Stopwatch.GetTimestamp();
        if (!renderer.BeginOffscreenFrame(Scene.Paper)) throw new InvalidOperationException("BeginOffscreenFrame refused");
        scene.Draw();
        renderer.EndOffscreenFrame();
        var cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ctx.WaitOffscreenFrameComplete();
        var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return (cpu, ctx.LastGpuFrameMs, wall);
    }

    private static double Mean(List<double> v) => v.Count == 0 || v.Any(double.IsNaN) ? double.NaN : v.Average();

    // Nearest rank.
    private static double P95(List<double> v)
    {
        if (v.Count == 0 || v.Any(double.IsNaN)) return double.NaN;
        var sorted = v.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(0.95 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static string Num(double v) => double.IsNaN(v) ? "n/a" : v.ToString("F3", CultureInfo.InvariantCulture);

    private static int Usage()
    {
        Console.WriteLine("""
            SdrFrameBench [--frames N] [--msaa 1|2|4|8] [--width PX] [--height PX]
              --frames  timed frames (default 300)
              --msaa    samples of the offscreen pass (default 4)
              --width, --height  scene size in pixels (default 1280 x 800)
            Exit code 0 on success, 2 when there is no Vulkan device, 1 on an error.
            """);
        return 1;
    }
}

/// <summary>A fixed UI: a toolbar, a side list, a grid of cards (rounded rectangles with a label and an image) and a
/// scatter of small marks, from a fixed seed so every run and every build of the library draws the same thing.</summary>
internal sealed class Scene : IDisposable
{
    public static readonly RGBAColor32 Paper = new(245, 245, 245, 255);

    private readonly VkRenderer _renderer;
    private readonly string _font;
    private readonly VkTexture[] _images;

    private readonly (RectInt Rect, RGBAColor32 Color)[] _blocks;
    private readonly (RectInt Rect, RGBAColor32 Color, float Radius)[] _cards;
    private readonly (RectInt Rect, RGBAColor32 Color)[] _dots;
    private readonly (RectInt Rect, string Text)[] _labels;
    private readonly (int X, int Y, int W, int H, int Image)[] _pictures;
    private readonly (RectInt Rect, RGBAColor32 Color)[] _strokes;

    private static RectInt R(int x, int y, int w, int h) => new(new PointInt(x + w, y + h), new PointInt(x, y));

    public Scene(VulkanContext ctx, VkRenderer renderer, string font)
    {
        _renderer = renderer;
        _font = font;
        var width = (int)renderer.Width;
        var height = (int)renderer.Height;
        var rng = new Random(165);
        RGBAColor32 Colour(int lo, int hi) => new((byte)rng.Next(lo, hi), (byte)rng.Next(lo, hi), (byte)rng.Next(lo, hi), 255);

        // Four 64x64 BGRA images, a different gradient each.
        _images = new VkTexture[4];
        for (var n = 0; n < _images.Length; n++)
        {
            var px = new byte[64 * 64 * 4];
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                {
                    var o = (y * 64 + x) * 4;
                    px[o] = (byte)(x * 4 + n * 20);
                    px[o + 1] = (byte)(y * 4);
                    px[o + 2] = (byte)(255 - x * 2 - y * 2 + n * 30);
                    px[o + 3] = 255;
                }
            _images[n] = VkTexture.CreateFromBgra(ctx, px, 64, 64);
        }

        // Toolbar and side list: plain blocks.
        var blocks = new List<(RectInt, RGBAColor32)>
        {
            (R(0, 0, width, 48), new RGBAColor32(40, 44, 52, 255)),
            (R(0, 48, 220, height - 48), new RGBAColor32(230, 232, 236, 255)),
        };
        for (var i = 0; i < 24; i++)
            blocks.Add((R(8, 60 + i * 30, 204, 26), i % 3 == 0 ? new RGBAColor32(205, 220, 245, 255) : new RGBAColor32(240, 241, 244, 255)));
        _blocks = [.. blocks];

        // A grid of cards, each a rounded rectangle with a label, an image and a stroke.
        var cards = new List<(RectInt, RGBAColor32, float)>();
        var labels = new List<(RectInt, string)>();
        var pictures = new List<(int, int, int, int, int)>();
        var strokes = new List<(RectInt, RGBAColor32)>();
        var cols = (width - 240) / 170;
        var rows = (height - 68) / 130;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var x = 232 + c * 170;
                var y = 60 + r * 130;
                cards.Add((R(x, y, 160, 120), Colour(235, 256), 8f));
                pictures.Add((x + 8, y + 8, 64, 64, (r + c) % _images.Length));
                labels.Add((R(x + 80, y + 8, 76, 20), $"Card {r * cols + c}"));
                labels.Add((R(x + 8, y + 80, 144, 18), "A longer subtitle line"));
                strokes.Add((R(x, y, 160, 120), new RGBAColor32(180, 184, 192, 255)));
            }
        // The toolbar's title is the last label (drawn white, larger).
        labels.Add((R(16, 12, 240, 24), "SDR frame bench"));
        _cards = [.. cards];
        _labels = [.. labels];
        _pictures = [.. pictures];
        _strokes = [.. strokes];

        // A scatter of small ellipses along the toolbar.
        _dots = new (RectInt, RGBAColor32)[200];
        for (var i = 0; i < _dots.Length; i++)
            _dots[i] = (R(rng.Next(240, width - 12), rng.Next(8, 40), rng.Next(4, 10), rng.Next(4, 10)), Colour(60, 200));
    }

    public string Describe()
        => $"{_blocks.Length} blocks, {_cards.Length} rounded cards, {_strokes.Length} strokes, {_dots.Length} ellipses, {_labels.Length} labels, {_pictures.Length} images";

    public void Draw()
    {
        var r = _renderer;
        r.FillRectangles(_blocks);
        foreach (var (rect, colour, radius) in _cards)
            r.FillRoundedRectangle(rect, colour, radius);
        foreach (var (x, y, w, h, image) in _pictures)
            r.DrawTexture(_images[image].DescriptorSet, x, y, w, h);
        foreach (var (rect, colour) in _strokes)
            r.DrawRectangle(rect, colour, 1);
        foreach (var (rect, colour) in _dots)
            r.FillEllipse(rect, colour);
        var ink = new RGBAColor32(30, 30, 30, 255);
        var white = new RGBAColor32(255, 255, 255, 255);
        for (var i = 0; i < _labels.Length; i++)
        {
            var (rect, text) = _labels[i];
            var onToolbar = i == _labels.Length - 1;
            r.DrawText(text, _font, onToolbar ? 16f : 12f, onToolbar ? white : ink, rect, TextAlign.Near, TextAlign.Center);
        }
    }

    public void Dispose()
    {
        foreach (var t in _images) t.Dispose();
    }
}
