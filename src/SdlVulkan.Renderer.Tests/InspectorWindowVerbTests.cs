#if DEBUG
using System;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The screenshot verb against a fake window: a minimised window is restored, waited on for its first
/// presented frame, captured, and optionally minimised again; a window that never presents fails fast.
/// </summary>
public class InspectorWindowVerbTests
{
    private sealed class FakeWindow : DebugInspector.IInspectorWindow
    {
        public bool IsMinimized { get; set; }
        public bool IsMaximized { get; set; }
        public (int Width, int Height) SizePixels { get; set; } = (800, 600);
        public (int X, int Y) Position { get; set; } = (50, 60);
        public (int Width, int Height) Usable = (1920, 1040);
        public (int Width, int Height) FloatingSize = (800, 600);
        public (int Width, int Height) CaptureSize;
        public int Maximizes;
        public bool IsGpuWedged => false;
        public bool GpuFenceStuck => false;
        public long FramesPresented { get; set; }
        public int Restores, Minimizes, Captures;
        public bool PresentOnRestore = true;
        private bool _captureReady;

        public void Restore()
        {
            Restores++;
            if (IsMinimized) IsMinimized = false;
            else if (IsMaximized)
            {
                IsMaximized = false;
                SizePixels = FloatingSize;
            }
        }

        public void Maximize()
        {
            Maximizes++;
            if (!IsMaximized) FloatingSize = SizePixels;
            IsMaximized = true;
            SizePixels = Usable;
        }

        public void SetSizePixels(int width, int height)
        {
            if (IsMaximized) return; // a maximised window ignores a size, like the real one
            SizePixels = (width, height);
        }

        public void SetPosition(int x, int y) => Position = (x, y);

        public bool TryGetUsableSizePixels(out int width, out int height)
        {
            (width, height) = Usable;
            return true;
        }

        public void Minimize()
        {
            Minimizes++;
            IsMinimized = true;
        }

        public void RequestRedraw() { }
        public void RequestCapture() { Captures++; }

        /// <summary>The loop's side: one iteration, which presents a frame if the window is showing.</summary>
        public void RunFrame()
        {
            if (IsMinimized || (Restores > 0 && !PresentOnRestore)) return;
            FramesPresented++;
            if (Captures > 0) { _captureReady = true; CaptureSize = SizePixels; }
        }

        public bool TryTakeCapture(out byte[] rgba, out uint width, out uint height)
        {
            width = (uint)CaptureSize.Width;
            height = (uint)CaptureSize.Height;
            if (width == 0) width = height = 2;
            rgba = new byte[4 * width * height];
            var ready = _captureReady;
            _captureReady = false;
            return ready;
        }
    }

    private static string? Drive(DebugInspector.ScreenshotOperation op, FakeWindow w, int maxSteps = 20, int sleepMs = 0)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            w.RunFrame();
            var reply = op.Advance();
            if (reply is not null) return reply;
            if (sleepMs > 0) System.Threading.Thread.Sleep(sleepMs);
        }
        return null;
    }

    [Fact]
    public void ScreenshotOfAMinimisedWindowRestoresItAndReportsIt()
    {
        var w = new FakeWindow { IsMinimized = true };
        var op = new DebugInspector.ScreenshotOperation(w, reminimize: false, TimeSpan.FromSeconds(5));

        var reply = Drive(op, w);

        reply.ShouldNotBeNull();
        reply.ShouldContain("\"unminimised\":true");
        reply.ShouldContain("\"base64\"");
        w.Restores.ShouldBe(1);
        w.Minimizes.ShouldBe(0);
        w.IsMinimized.ShouldBeFalse();
    }

    [Fact]
    public void ReminimizePutsTheWindowBackAfterTheCapture()
    {
        var w = new FakeWindow { IsMinimized = true };
        var op = new DebugInspector.ScreenshotOperation(w, reminimize: true, TimeSpan.FromSeconds(5));

        var reply = Drive(op, w);

        reply.ShouldNotBeNull();
        reply.ShouldContain("\"unminimised\":true");
        w.Restores.ShouldBe(1);
        w.Minimizes.ShouldBe(1);
        w.IsMinimized.ShouldBeTrue();
    }

    [Fact]
    public void AWindowThatNeverPresentsFailsWithinTheBudget()
    {
        var w = new FakeWindow { IsMinimized = true, PresentOnRestore = false };
        var op = new DebugInspector.ScreenshotOperation(w, reminimize: false, TimeSpan.FromMilliseconds(60));

        var started = Environment.TickCount64;
        var reply = Drive(op, w, maxSteps: 500, sleepMs: 10);

        reply.ShouldNotBeNull();
        reply.ShouldContain("the window presented no frame: is the display off?");
        reply.ShouldNotContain("\"base64\"");
        (Environment.TickCount64 - started).ShouldBeLessThan(2000);
        w.Captures.ShouldBe(0);
    }

    [Fact]
    public void ANonMinimisedWindowCapturesAsBefore()
    {
        var w = new FakeWindow();
        var op = new DebugInspector.ScreenshotOperation(w, reminimize: true, TimeSpan.FromSeconds(5));

        var reply = Drive(op, w);

        reply.ShouldNotBeNull();
        reply.ShouldNotContain("unminimised");
        w.Restores.ShouldBe(0);
        w.Minimizes.ShouldBe(0);
    }

    private static string? DriveResize(DebugInspector.ResizeOperation op, FakeWindow w, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            w.RunFrame();
            var reply = op.Advance();
            if (reply is not null) return reply;
        }
        return null;
    }

    [Fact]
    public void ResizeSetsTheClientSizeAndUnmaximisesFirst()
    {
        var w = new FakeWindow();
        w.Maximize();
        w.Restores.ShouldBe(0);

        var reply = DriveResize(new DebugInspector.ResizeOperation(w, 700, 900, TimeSpan.FromSeconds(5)), w);

        reply.ShouldNotBeNull();
        reply.ShouldContain("\"width\":700");
        reply.ShouldContain("\"height\":900");
        reply.ShouldContain("\"clamped\":false");
        w.Restores.ShouldBe(1);
        w.IsMaximized.ShouldBeFalse();
        w.SizePixels.ShouldBe((700, 900));
    }

    [Fact]
    public void AnOversizeResizeIsClampedAndSaysSo()
    {
        var w = new FakeWindow();

        var reply = DriveResize(new DebugInspector.ResizeOperation(w, 5000, 900, TimeSpan.FromSeconds(5)), w);

        reply.ShouldNotBeNull();
        reply.ShouldContain("\"width\":1920");
        reply.ShouldContain("\"height\":900");
        reply.ShouldContain("\"clamped\":true");
        w.SizePixels.ShouldBe((1920, 900));
    }

    [Fact]
    public void ScreenshotAtASizeCapturesThereAndLeavesAMaximisedWindowMaximised()
    {
        var w = new FakeWindow();
        w.Maximize();
        w.Maximizes = 0;
        var op = new DebugInspector.ScreenshotOperation(w, false, TimeSpan.FromSeconds(5), 700, 900);

        var reply = Drive(op, w);

        reply.ShouldNotBeNull();
        reply.ShouldContain("\"base64\"");
        w.CaptureSize.ShouldBe((700, 900));
        w.IsMaximized.ShouldBeTrue();
        w.Maximizes.ShouldBe(1);
    }

    [Fact]
    public void ScreenshotAtASizeGivesAFloatingWindowItsOldSizeBack()
    {
        var w = new FakeWindow { SizePixels = (1000, 700), Position = (120, 80) };
        var op = new DebugInspector.ScreenshotOperation(w, false, TimeSpan.FromSeconds(5), 700, 900);

        var reply = Drive(op, w);

        reply.ShouldNotBeNull();
        w.CaptureSize.ShouldBe((700, 900));
        w.IsMaximized.ShouldBeFalse();
        w.SizePixels.ShouldBe((1000, 700));
        w.Position.ShouldBe((120, 80));
    }

    [Fact]
    public void ScreenshotAtASizeOnAMinimisedWindowComposesWithReminimize()
    {
        var w = new FakeWindow { IsMinimized = true };
        var op = new DebugInspector.ScreenshotOperation(w, true, TimeSpan.FromSeconds(5), 700, 900);

        var reply = Drive(op, w);

        reply.ShouldNotBeNull();
        reply.ShouldContain("\"unminimised\":true");
        w.CaptureSize.ShouldBe((700, 900));
        w.SizePixels.ShouldBe((800, 600));
        w.IsMinimized.ShouldBeTrue();
    }
}
#endif
