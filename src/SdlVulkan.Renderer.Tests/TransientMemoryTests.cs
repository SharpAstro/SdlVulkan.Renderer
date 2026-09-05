using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// Transient attachments — the depth every pass carries, the multisample colour under MSAA — go into
/// lazily allocated memory where the device has any, so a tiler never backs them at all.
/// </summary>
/// <remarks>
/// <para>What this guards is memory, not pixels: a large sheet exported at 300 dpi under 4x MSAA is
/// 16 bytes a pixel per transient image, and the depth attachment doubled that; on a shared-memory
/// Adreno the second gigabyte was the one <c>vkAllocateMemory</c> refused. Placed in a lazily
/// allocated type the same images cost nothing there, which no render test can see — the picture is
/// identical either way — so the choice is asserted directly.</para>
/// <para>Skips on a device with no lazily allocated type (lavapipe, desktop GPUs), where device-local
/// is the only answer and there is nothing to choose. It therefore runs on the developer's tiler and
/// not in CI, and that is the honest shape: a test that asserted the fallback would pass everywhere and
/// guard nothing.</para>
/// </remarks>
[Collection("OffscreenGpu")]
public sealed class TransientMemoryTests(OffscreenGpuFixture gpu)
{
    [Fact]
    public void TransientAttachmentsTakeLazilyAllocatedMemoryWhereTheDeviceOffersIt()
    {
        if (gpu.Context is not { } ctx)
        {
            Assert.Skip("Vulkan runtime not available on this host");
            return;
        }
        if (!ctx.GraphicsDevice.OffersLazilyAllocatedMemory)
        {
            Assert.Skip("this device has no lazily allocated memory type; transient attachments are device-local by necessity");
            return;
        }

        // The fixture's offscreen target carries a depth attachment, so the choice has already been
        // made by the time any test runs; a resize makes it again on this test's watch.
        ctx.ResizeOffscreen(64, 64);
        ctx.GraphicsDevice.TransientMemoryIsLazy.ShouldBeTrue(
            "the device offers lazily allocated memory and a transient attachment did not take it");
    }
}
