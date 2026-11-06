using SdlVulkan.Renderer;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace SlugProbe;

/// <summary>
/// Slug against MTSDF on one page, offscreen: see SlugProbe.csproj for what it prints. This file only parses options,
/// makes the headless context and prints; the page and the measuring are in SlugProbePage.cs.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var frames = 300;
        var size = 15f;
        var msaa = 4;
        var burst = 1200;
        string? cjkFont = null;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--frames": frames = int.Parse(args[++i]); break;
                    case "--size": size = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--msaa": msaa = int.Parse(args[++i]); break;
                    case "--burst": burst = int.Parse(args[++i]); break;
                    case "--cjk-font": cjkFont = args[++i]; break;
                    default: return Usage();
                }
            }
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
        {
            return Usage();
        }
        if (frames < 1 || size <= 0 || msaa is not (1 or 2 or 4 or 8) || burst < 1) return Usage();
        if (cjkFont is not null && !File.Exists(cjkFont))
        {
            Console.Error.WriteLine($"--cjk-font: no such file: {cjkFont}");
            return 1;
        }

        VulkanContext ctx;
        try
        {
            ctx = CreateOffscreen((VkSampleCountFlags)msaa);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"no Vulkan device: {e.Message}");
            return 2;
        }

        using (ctx)
        {
            var fonts = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            try
            {
                var report = SlugProbePage.Run(ctx, Path.Combine(fonts, "DejaVuSans.ttf"),
                    cjkFont ?? Path.Combine(fonts, "NotoSansJP-Subset.otf"), frames, size, burst, msaa);
                Console.Write(SlugProbePage.Format(report));
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }
    }

    private static unsafe VulkanContext CreateOffscreen(VkSampleCountFlags msaa)
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
        return VulkanContext.CreateOffscreen(instance, SlugProbePage.PageWidth, SlugProbePage.PageHeight, msaaSamples: msaa);
    }

    private static int Usage()
    {
        Console.WriteLine("""
            SlugProbe [--frames N] [--size PX] [--msaa 1|2|4|8] [--burst GLYPHS] [--cjk-font PATH]
              --frames  timed frames per technique (default 300)
              --size    body text size in pixels per em (default 15, 11 pt at 96 dpi)
              --msaa    samples of the offscreen pass (default 4; Slug's coverage does not depend on it, MTSDF's cost does)
              --burst   new glyphs asked for in the store-growth burst (default 1200)
              --cjk-font  an OpenType face for the CJK block (default: the three-glyph subset the tests committed, whose block
                        repeats those three; a full CJK face fills it with different glyphs, and the report says how many)
            Exit code 0 on success, 2 when there is no Vulkan device, 1 on an error.
            """);
        return 1;
    }
}
