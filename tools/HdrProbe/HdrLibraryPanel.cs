using DIR.Lib;
using SdlVulkan.Renderer;
using static SDL3.SDL;

namespace HdrProbe;

/// <summary>
/// The second phase of <c>--show</c>: the same kind of picture as <see cref="HdrShow"/>, drawn THROUGH THE LIBRARY, so a
/// person can judge on the panel whether <see cref="VkRenderer.DrawHdrImage"/> on a <see cref="VulkanContext"/> created
/// with <c>hdr: true</c> reaches the glass as <see cref="HdrShow"/>'s raw clear rectangles do.
/// </summary>
/// <remarks>
/// <para><b>Why a second phase and not a second window beside the first.</b> <see cref="HdrShow"/> is raw Vulkan on
/// purpose: it proves the PRESENT path with no library in between, and the probe's own instance and device are what it
/// runs on. The library owns its SDL lifetime, its instance and its device (<c>SdlVulkanWindow.Create</c> initialises
/// SDL and disposing it quits SDL), so two live at once would fight over both. Run one after the other, the raw window
/// first (closed with Escape), then this one, the pair is a clean A/B: the first says "the compositor takes scRGB",
/// the second says "and the library's composite puts its images there". If the first looks right and this does not, the
/// fault is the library's, not the driver's.</para>
/// <para><b>What is drawn.</b> The window's own scRGB swapchain (the library's, through <see cref="SdlEventLoop"/>, which
/// reads the display's facts on SDL events and keeps SDR white and headroom current):</para>
/// <list type="bullet">
/// <item>Top row: an SDR white swatch (an ordinary <c>FillRectangle</c> of 255, 255, 255, drawn in the SDR layer, so it
/// is exactly as bright as any SDR window's white), then a horizontal RAMP, one float texture, linear light in BT.709,
/// from 0 to the panel's peak, so the ramp's far end is the headroom. A tick under the ramp marks where SDR white falls
/// on it.</item>
/// <item>Bottom row: for each of red, green and blue, three patches. First the BT.709 primary as an SDR
/// <c>FillRectangle</c> (255, 0, 0 and so on: the most saturated an SDR window can show, at SDR white's brightness).
/// Second the BT.2020 primary at SDR white, a 1x1 float texture drawn with <c>HdrPrimaries.Bt2020</c>: more saturated
/// than the first, because it lies outside BT.709's triangle (the converted value has a negative component, which scRGB
/// can carry and a panel with a wide gamut can show). Third the BT.2020 primary at the panel's peak: the same hue,
/// brighter than anything SDR.</item>
/// </list>
/// <para>Everything is drawn every frame (the composite is not preserved between frames, see
/// <see cref="VkRenderer.DrawHdrImage"/>), the window repaints only when something asks (the event loop is
/// idle-suppressing). Where the surface offers no scRGB (the report says <see cref="VulkanContext.HdrActive"/> false)
/// the same calls tone-map to SDR: the ramp then rolls off to white, which is the answer the library gives an SDR
/// display, and a useful one to look at next to the HDR run.</para>
/// <para>Judge it on the panel, not in a screenshot: Windows captures to SDR (see <see cref="HdrShow"/>).</para>
/// </remarks>
internal static class HdrLibraryPanel
{
    private const int WindowWidth = 1280;
    private const int WindowHeight = 760;

    // The patch of the ramp texture: wide enough that the linear filter gives a smooth gradient on any panel.
    private const int RampTexels = 512;

    public static void Run(Rect bounds)
    {
        using var window = SdlVulkanWindow.Create("HdrProbe: library DrawHdrImage (Esc closes)", WindowWidth, WindowHeight);
        SetWindowPosition(window.Handle, bounds.X + (bounds.W - WindowWidth) / 2, bounds.Y + (bounds.H - WindowHeight) / 2);

        // SDL re-derives the window's display when the move lands (see Program.Run); wait for it before the facts are read.
        var target = GetDisplayForPoint(new Point { X = bounds.X + bounds.W / 2, Y = bounds.Y + bounds.H / 2 });
        for (var attempt = 0; attempt < 50 && GetDisplayForWindow(window.Handle) != target; attempt++)
        {
            PumpEvents();
            Thread.Sleep(10);
        }

        window.GetSizeInPixels(out var w, out var h);
        // hdr: true is the whole opt-in. MSAA 1: the picture is rectangles and flat images, nothing to antialias.
        using var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)w, (uint)h, hdr: true);
        using var renderer = new VkRenderer(ctx, (uint)w, (uint)h);
        var loop = new SdlEventLoop(window, renderer);

        // The loop read the display's facts when the window was added and handed them to the context.
        var sdrWhite = ctx.SdrWhiteScRgb;
        var peak = ctx.HdrPeakScRgb;
        // The texture's unit is 203 cd/m2 (HdrColor.ReferenceWhiteNits); scRGB's is 80. These are the SDR white and the
        // peak in the texture's unit, so a texel of that value comes out as exactly that scRGB level on the glass.
        var unit = HdrColor.ScRgbUnitNits / HdrColor.ReferenceWhiteNits;
        var sdrWhiteTexture = sdrWhite * unit;
        var peakTexture = peak * unit;
        var peakNits = peak * HdrColor.ScRgbUnitNits;

        Describe(ctx, sdrWhite, peak, sdrWhiteTexture, peakTexture);

        var ramp = new float[RampTexels * 4];
        for (var i = 0; i < RampTexels; i++)
        {
            // Texel centres, linear in light, 0 to the peak across the texture's width.
            var v = peakTexture * (i + 0.5f) / RampTexels;
            ramp[i * 4] = ramp[i * 4 + 1] = ramp[i * 4 + 2] = v;
            ramp[i * 4 + 3] = 1f;
        }
        using var rampTexture = VkTexture.CreateFromRgbaFloat(ctx, ramp, RampTexels, 1);

        // Six 1x1 textures, so the linear filter has nothing to blend between neighbours: each patch is one value.
        var primaries = new (float R, float G, float B)[] { (1, 0, 0), (0, 1, 0), (0, 0, 1) };
        var atWhite = new VkTexture[3];
        var atPeak = new VkTexture[3];
        for (var i = 0; i < 3; i++)
        {
            var (r, g, b) = primaries[i];
            atWhite[i] = VkTexture.CreateFromRgbaFloat(ctx, [r * sdrWhiteTexture, g * sdrWhiteTexture, b * sdrWhiteTexture, 1f], 1, 1);
            atPeak[i] = VkTexture.CreateFromRgbaFloat(ctx, [r * peakTexture, g * peakTexture, b * peakTexture, 1f], 1, 1);
        }

        var background = new RGBAColor32(46, 46, 46, 255); // sRGB 46 is about 18 percent linear: the photographer's grey
        var sdrWhiteColor = new RGBAColor32(255, 255, 255, 255);
        loop.BackgroundColor = background;
        loop.OnKeyDown = e =>
        {
            if (e.Key is InputKey.Escape or InputKey.Q)
                loop.Stop();
            return true;
        };
        loop.OnRender = () =>
        {
            var width = (int)renderer.Width;
            var height = (int)renderer.Height;
            var margin = width / 40;
            var gap = margin / 2;
            var rowHeight = (height - 3 * margin) / 2;

            // Top row: the SDR white swatch, then the ramp. DrawHdrImage on an HDR frame punches its rectangle out of the
            // SDR layer, so everything filled AFTER it lies over it (the tick below) and what came before does not show.
            var swatchWidth = (width - 2 * margin - gap) / 8;
            var rampLeft = margin + swatchWidth + gap;
            var rampWidth = width - margin - rampLeft;
            Fill(margin, margin, swatchWidth, rowHeight, sdrWhiteColor);
            renderer.DrawHdrImage(rampTexture, rampLeft, margin, rampWidth, rowHeight,
                HdrPrimaries.Bt709, sourcePeakNits: peakNits);
            // Where SDR white falls on the ramp: a dark tick in the SDR layer, over the HDR image.
            var tickX = rampLeft + (int)(rampWidth * (sdrWhite / peak));
            Fill(tickX - 1, margin, 3, rowHeight, new RGBAColor32(200, 0, 120, 255));

            // Bottom row: red, green, blue as (SDR primary, BT.2020 at SDR white, BT.2020 at peak).
            var rowTop = margin + rowHeight + margin;
            var group = (width - 2 * margin - 2 * gap * 3) / 3;
            var patch = (group - 2 * gap) / 3;
            RGBAColor32[] sdrPrimaries = [new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255)];
            for (var i = 0; i < 3; i++)
            {
                var left = margin + i * (group + 3 * gap);
                Fill(left, rowTop, patch, rowHeight, sdrPrimaries[i]);
                renderer.DrawHdrImage(atWhite[i], left + patch + gap, rowTop, patch, rowHeight,
                    HdrPrimaries.Bt2020, sourcePeakNits: peakNits);
                renderer.DrawHdrImage(atPeak[i], left + 2 * (patch + gap), rowTop, patch, rowHeight,
                    HdrPrimaries.Bt2020, sourcePeakNits: peakNits);
            }
        };

        void Fill(int x, int y, int fw, int fh, RGBAColor32 color)
            => renderer.FillRectangle(new RectInt(new PointInt(x + fw, y + fh), new PointInt(x, y)), color);

        try
        {
            loop.Run();
        }
        finally
        {
            foreach (var t in atWhite) t.Dispose();
            foreach (var t in atPeak) t.Dispose();
        }
    }

    private static void Describe(VulkanContext ctx, float sdrWhite, float peak,
        float sdrWhiteTexture, float peakTexture)
    {
        Console.WriteLine();
        Console.WriteLine($"== Library panel: DrawHdrImage on a VulkanContext created with hdr: true");
        Console.WriteLine($"   Swapchain presented as: {ctx.PresentFormat}.  HdrActive: {ctx.HdrActive}  (HdrRequested: {ctx.HdrRequested}).");
        Console.WriteLine($"   Display facts the library read: HDR {ctx.HdrDisplay.HdrActive}, SDR white {sdrWhite:F2} scRGB ({sdrWhite * 80:F0} nits), headroom {ctx.HdrHeadroom:F2}, peak {peak:F2} ({peak * 80:F0} nits).");
        if (!ctx.HdrActive)
        {
            Console.WriteLine("   The context is NOT presenting scRGB (the surface offers none, or the display is SDR): the same calls are tone-mapped to SDR.");
            Console.WriteLine("   Expect the ramp to roll off to white well before its right end, and every patch to sit at or below SDR white. That is the library's SDR answer, not a fault.");
        }
        else
        {
            Console.WriteLine($"   A texel of 1.0 is {HdrColor.ReferenceWhiteNits:F0} nits (BT.2408 reference white); SDR white is {sdrWhiteTexture:F2} in that unit, the panel's peak {peakTexture:F2}.");
            Console.WriteLine("   Top row: an SDR white swatch (an ordinary fill, as bright as any SDR window's white), then a ramp from black to the panel's peak,");
            Console.WriteLine("            BT.709 linear light. Left of the magenta tick the ramp is no brighter than the swatch; right of it, it is brighter than the swatch,");
            Console.WriteLine("            smoothly, up to the peak at the right edge. A ramp that stops getting brighter at the tick is clipping at SDR white: the composite did not take.");
            Console.WriteLine("   Bottom row: red, green, blue, each as three patches left to right:");
            Console.WriteLine("            1. the SDR primary (255 in one channel): the most saturated an SDR window can show;");
            Console.WriteLine("            2. the BT.2020 primary at SDR white: the same brightness, MORE saturated (outside BT.709; visible on a wide-gamut panel, clipped to 1 on an sRGB one);");
            Console.WriteLine("            3. the BT.2020 primary at the panel's peak: the same hue, brighter than the SDR swatches beside it.");
            Console.WriteLine("   The top-left swatch and the first patch of each group are drawn in the SDR layer; everything else is a float texture through DrawHdrImage.");
        }
        Console.WriteLine("   Judge it on the panel, not in a screenshot: Windows captures to SDR. Esc closes.");
    }
}
