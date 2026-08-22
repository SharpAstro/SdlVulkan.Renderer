namespace SdlVulkan.Renderer;

/// <summary>
/// Which region of each swapchain image is out of date, so a frame can repaint that instead of the
/// whole surface.
/// </summary>
/// <remarks>
/// <para><b>The per-image part is the whole difficulty, and it is why this is a type rather than three
/// fields.</b> There are 2-3 swapchain images and the app renders into them in rotation, so the image
/// acquired this frame does not hold the previous frame -- it holds the one from 2-3 frames ago. What
/// must be repainted into it is therefore the UNION of every frame's damage since THAT image was last
/// painted, not this frame's damage. Using the current frame's damage leaves stale pixels that appear
/// only at particular frame counts, which reads as an intermittent rendering glitch rather than as
/// bookkeeping, so it is worth being able to test directly.</para>
/// <para>A bounding box per image rather than a rect list, because a draw takes ONE scissor: multiple
/// scissors need a pipeline with multiple viewports and a shader that selects between them, and the app
/// paints its frame once, so a list could only be honoured by replaying the whole paint per rect. A box
/// over merged damage is the useful approximation -- a status-bar change stays a thin strip, which is
/// the case this exists for.</para>
/// <para>Separated from <see cref="VulkanContext"/> so it can be exercised without a device: the Vulkan
/// side is a render pass that loads instead of clears, which is mechanical, while this is the part with
/// an algorithm in it.</para>
/// </remarks>
internal sealed class SwapchainDamage
{
    private struct Pending
    {
        /// <summary>Contents unknown, so this image needs a full clear and repaint.</summary>
        public bool Full;

        /// <summary>A region has accumulated. Meaningless while <see cref="Full"/>.</summary>
        public bool Any;

        public float X0, Y0, X1, Y1;
    }

    private Pending[] _images = [];

    /// <summary>How many images are being tracked.</summary>
    public int ImageCount => _images.Length;

    /// <summary>
    /// Starts over with every image unknown, so each one clears and repaints in full on its next turn.
    /// The correct state after a swapchain is created or recreated: the images are new, or the right
    /// handles at the wrong size, and neither holds anything worth preserving.
    /// </summary>
    public void Reset(int imageCount)
    {
        if (_images.Length != imageCount)
        {
            _images = new Pending[imageCount < 0 ? 0 : imageCount];
        }

        for (var i = 0; i < _images.Length; i++)
        {
            _images[i] = new Pending { Full = true };
        }
    }

    /// <summary>
    /// Records a rect this frame changed, in surface pixels, against EVERY image -- each will need it
    /// when its turn comes.
    /// </summary>
    public void Add(float x, float y, float width, float height)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        for (var i = 0; i < _images.Length; i++)
        {
            ref var p = ref _images[i];
            if (p.Full)
            {
                continue;   // already the worst case; a rect cannot make it worse
            }

            if (!p.Any)
            {
                p = new Pending { Any = true, X0 = x, Y0 = y, X1 = x + width, Y1 = y + height };
                continue;
            }

            if (x < p.X0) { p.X0 = x; }
            if (y < p.Y0) { p.Y0 = y; }
            if (x + width > p.X1) { p.X1 = x + width; }
            if (y + height > p.Y1) { p.Y1 = y + height; }
        }
    }

    /// <summary>
    /// Declares this frame's change unenumerable, so every image must be fully repainted. The safe
    /// answer, and the right one for a resize, a theme switch, or any caller that cannot say what moved.
    /// </summary>
    public void MarkFull()
    {
        for (var i = 0; i < _images.Length; i++)
        {
            _images[i].Full = true;
        }
    }

    /// <summary>
    /// Takes the region that must be repainted into <paramref name="imageIndex"/> and marks it current.
    /// False means repaint everything -- either the contents are unknown, or nothing has been recorded
    /// and there is no region to give.
    /// </summary>
    /// <remarks>
    /// It CLEARS as it reads, because the image is about to be painted: leaving the region behind would
    /// keep repainting it on every later turn of this image, and the accumulation would never shrink.
    /// </remarks>
    public bool TryTake(int imageIndex, out float x, out float y, out float width, out float height)
    {
        x = y = width = height = 0f;
        if ((uint)imageIndex >= (uint)_images.Length)
        {
            return false;
        }

        ref var p = ref _images[imageIndex];
        var partial = !p.Full && p.Any;
        if (partial)
        {
            x = p.X0;
            y = p.Y0;
            width = p.X1 - p.X0;
            height = p.Y1 - p.Y0;
        }

        // Painted either way, so its contents are known from here on.
        p = default;
        return partial;
    }
}
