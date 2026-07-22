using System;
using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// End-to-end render verification for <see cref="VkRenderer.DeviceTransform"/>: the transform must be
/// folded into the projection so the WHOLE frame moves, not just individual draws. The test fills the
/// top-left quadrant of a 64×64 offscreen target via the ordinary <see cref="VkRenderer.FillRectangle"/>
/// path (which draws through the cached projection push-constant), reads the framebuffer back, and
/// asserts that a <see cref="Rotation90.Half"/> centred transform moves that fill to the BOTTOM-RIGHT
/// quadrant — the 180° flip. The lit-quadrant SWAP between the two frames is the core assertion, so it
/// holds regardless of the readback's row order.
///
/// Skips when Vulkan isn't loadable on the host (no ICD / libvulkan), like the sibling GPU tests.
/// </summary>
public sealed unsafe class DeviceTransformRenderTests
{
    private const uint Size = 64;
    private const int Half = (int)Size / 2;

    [Fact]
    public void Half_MovesTopLeftFillToBottomRight()
    {
        if (!TryCreateOffscreenContext(out var instance, out var ctx))
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }

        try
        {
            using var renderer = new VkRenderer(ctx!, Size, Size);

            // Identity transform (default): content top-left quadrant → device top-left quadrant.
            renderer.DeviceTransform.IsIdentity.ShouldBeTrue("renderer starts at the identity transform");
            var identity = RenderTopLeftQuadrantFill(renderer, ctx!);

            // 180° about the surface centre: the same top-left quadrant now lands bottom-right.
            renderer.DeviceTransform = DeviceTransform.CenteredRotation(Rotation90.Half, Size, Size);
            var flipped = RenderTopLeftQuadrantFill(renderer, ctx!);

            // Sample the middle of the top-left and bottom-right quadrants.
            var tlId = RedAt(identity, Half / 2, Half / 2);
            var brId = RedAt(identity, Half + Half / 2, Half + Half / 2);
            var tlFlip = RedAt(flipped, Half / 2, Half / 2);
            var brFlip = RedAt(flipped, Half + Half / 2, Half + Half / 2);

            // Identity: top-left lit, bottom-right dark.
            tlId.ShouldBeGreaterThan((byte)200, "identity: top-left quadrant should be filled");
            brId.ShouldBeLessThan((byte)60, "identity: bottom-right quadrant should be clear");

            // Half: the fill has moved to the bottom-right; top-left is now clear.
            tlFlip.ShouldBeLessThan((byte)60, "half: top-left quadrant should be clear after the 180° flip");
            brFlip.ShouldBeGreaterThan((byte)200, "half: bottom-right quadrant should be filled after the 180° flip");
        }
        finally
        {
            ctx?.Dispose();
        }
    }

    /// <summary>
    /// Clears to opaque black, fills the content-space top-left quadrant white via the renderer's own
    /// <see cref="VkRenderer.FillRectangle"/> (so the draw goes through the composed projection), and
    /// returns the RGBA readback.
    /// </summary>
    private static byte[] RenderTopLeftQuadrantFill(VkRenderer renderer, VulkanContext ctx)
    {
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        // RectInt(lowerRight, upperLeft) — matches the framework's own FillRect helper argument order.
        renderer.FillRectangle(
            new RectInt(new PointInt(Half, Half), new PointInt(0, 0)),
            new RGBAColor32(255, 255, 255, 255));
        renderer.EndOffscreenFrame();
        ctx.WaitOffscreenFrameComplete();

        var rgba = ctx.ReadbackOffscreenRgba();
        rgba.Length.ShouldBe((int)(Size * Size * 4));
        return rgba;
    }

    private static byte RedAt(byte[] rgba, int x, int y) => rgba[(y * (int)Size + x) * 4];

    /// <summary>
    /// Best-effort offscreen Vulkan setup; returns false when the host has no Vulkan ICD.
    /// </summary>
    private static bool TryCreateOffscreenContext(out VkInstance instance, out VulkanContext? ctx)
    {
        instance = default;
        ctx = null;
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out instance).CheckResult();
            ctx = VulkanContext.CreateOffscreen(instance, Size, Size);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
