using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Per-swapchain-image damage accumulation: which region must be repainted into the image being
/// acquired, given that it holds a frame from several frames ago rather than the previous one.
/// </summary>
/// <remarks>
/// <para>Tested without a device because the Vulkan half is mechanical -- a render pass that loads
/// instead of clears -- while this is the half with an algorithm, and its failure mode is the nastiest
/// kind. Using the CURRENT frame's damage instead of the accumulated damage leaves stale pixels that
/// appear only at particular frame counts and only for the images that missed an update, which presents
/// as an intermittent rendering glitch with no obvious relation to bookkeeping.</para>
/// <para>The other direction is just as invisible: accumulating and never clearing looks perfectly
/// correct on screen while quietly giving back the entire saving, so nothing would ever fail.</para>
/// </remarks>
public sealed class SwapchainDamageTests
{
    [Fact]
    public void AFreshSwapchainRepaintsEveryImageInFull()
    {
        // New images hold nothing; resized ones hold the right handles at the wrong size. Neither can
        // be preserved, so the first frame for each must clear.
        var damage = new SwapchainDamage();
        damage.Reset(3);

        for (var i = 0; i < 3; i++)
        {
            damage.TryTake(i, out _, out _, out _, out _)
                .ShouldBeFalse($"image {i} has never been painted");
        }
    }

    [Fact]
    public void OnceAnImageIsPaintedAndDamagedItTakesJustThatRegion()
    {
        var damage = new SwapchainDamage();
        damage.Reset(2);
        damage.TryTake(0, out _, out _, out _, out _);   // first paint: full, and now known

        damage.Add(10f, 20f, 30f, 40f);

        damage.TryTake(0, out var x, out var y, out var w, out var h).ShouldBeTrue();
        (x, y, w, h).ShouldBe((10f, 20f, 30f, 40f));
    }

    /// <summary>
    /// THE test. An image skipped for two frames must be repainted with everything that happened while
    /// it was away, not merely with the newest frame's damage.
    /// </summary>
    [Fact]
    public void AnImageSkippedForSeveralFramesTakesTheUnionOfWhatItMissed()
    {
        var damage = new SwapchainDamage();
        damage.Reset(2);
        damage.TryTake(0, out _, out _, out _, out _);
        damage.TryTake(1, out _, out _, out _, out _);   // both known now

        // Frame A damages the top-left and is painted into image 0.
        damage.Add(0f, 0f, 10f, 10f);
        damage.TryTake(0, out _, out _, out _, out _).ShouldBeTrue();

        // Frame B damages the bottom-right and is painted into image 1.
        damage.Add(90f, 90f, 10f, 10f);
        damage.TryTake(1, out var bx, out var by, out var bw, out var bh).ShouldBeTrue();
        (bx, by).ShouldBe((0f, 0f), "image 1 missed frame A, so it needs that region too");
        (bw, bh).ShouldBe((100f, 100f), "the union of both frames, not just frame B's corner");

        // Frame C damages the middle and comes back round to image 0, which missed B.
        damage.Add(40f, 40f, 10f, 10f);
        damage.TryTake(0, out var cx, out var cy, out var cw, out var ch).ShouldBeTrue();
        (cx, cy).ShouldBe((40f, 40f));
        (cw, ch).ShouldBe((60f, 60f), "image 0 missed frame B, so its region spans B and C");
    }

    [Fact]
    public void TakingClearsSoARegionIsNotRepaintedForever()
    {
        // Without the clear the accumulation only ever grows, every frame converges on the whole
        // surface, and the saving silently disappears while the screen still looks right.
        var damage = new SwapchainDamage();
        damage.Reset(1);
        damage.TryTake(0, out _, out _, out _, out _);

        damage.Add(5f, 5f, 10f, 10f);
        damage.TryTake(0, out _, out _, out _, out _).ShouldBeTrue();

        damage.TryTake(0, out _, out _, out _, out _)
            .ShouldBeFalse("nothing has been damaged since, so there is no region to repaint");
    }

    [Fact]
    public void MarkFullBeatsAnyAccumulatedRegion()
    {
        var damage = new SwapchainDamage();
        damage.Reset(2);
        damage.TryTake(0, out _, out _, out _, out _);
        damage.TryTake(1, out _, out _, out _, out _);

        damage.Add(1f, 1f, 2f, 2f);
        damage.MarkFull();

        damage.TryTake(0, out _, out _, out _, out _).ShouldBeFalse();
        damage.TryTake(1, out _, out _, out _, out _).ShouldBeFalse("a resize invalidates every image");
    }

    [Fact]
    public void ARectAddedWhileFullDoesNotDowngradeItToPartial()
    {
        // Order must not matter: a full repaint that then receives a small rect is still a full
        // repaint, or a resize followed by one moved label would preserve a stale surface.
        var damage = new SwapchainDamage();
        damage.Reset(1);
        damage.MarkFull();
        damage.Add(1f, 1f, 2f, 2f);

        damage.TryTake(0, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void EmptyRectsAreIgnored()
    {
        var damage = new SwapchainDamage();
        damage.Reset(1);
        damage.TryTake(0, out _, out _, out _, out _);

        damage.Add(10f, 10f, 0f, 50f);
        damage.Add(10f, 10f, 50f, 0f);
        damage.Add(10f, 10f, -5f, -5f);

        damage.TryTake(0, out _, out _, out _, out _)
            .ShouldBeFalse("a zero-area rect damages nothing, and must not start a region at its origin");
    }

    [Fact]
    public void AnOutOfRangeImageAsksForAFullRepaint()
    {
        // "Repaint everything" is the only safe answer about an image that does not exist.
        var damage = new SwapchainDamage();
        damage.Reset(2);

        damage.TryTake(5, out _, out _, out _, out _).ShouldBeFalse();
        damage.TryTake(-1, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void ResetToADifferentCountReallocatesAndInvalidates()
    {
        // A swapchain recreated with a different image count must not leave stale per-image state
        // behind, and must not preserve anything.
        var damage = new SwapchainDamage();
        damage.Reset(2);
        damage.TryTake(0, out _, out _, out _, out _);
        damage.Add(1f, 1f, 5f, 5f);

        damage.Reset(3);

        damage.ImageCount.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            damage.TryTake(i, out _, out _, out _, out _).ShouldBeFalse();
        }
    }
}
