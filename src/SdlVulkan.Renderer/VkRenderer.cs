using System.Runtime.InteropServices;
using DIR.Lib;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

public sealed unsafe class VkRenderer : Renderer<VulkanContext>
{
    private VkPipelineSet? _pipelines;
    private VkFontAtlas? _fontAtlas;
    private VkSdfFontAtlas? _sdfFontAtlas;
    private uint _width;
    private uint _height;
    private VkCommandBuffer _currentCmd;

    // Saved swapchain projection dims while a thumbnail capture redirects _width/_height (below).
    private uint _savedWidth;
    private uint _savedHeight;
    private bool _inThumbnailCapture;

    // Push constant data: mat4 (16 floats) + vec4 color (4 floats) + float innerRadius (1 float) = 84 bytes
    private readonly float[] _pushConstants = new float[21];

    // Glyph batching state — accumulates contiguous glyph quads for a single draw call.
    // A batch is either bitmap (TexturedPipeline + RGBA atlas) or SDF (SdfPipeline +
    // R8 SDF atlas). BeginGlyphBatch opens a bitmap batch; BeginSdfGlyphBatch opens
    // an SDF batch. EndGlyphBatch dispatches to the correct pipeline based on _glyphBatchIsSdf.
    private uint _glyphBatchStartOffset = uint.MaxValue;
    private int _glyphBatchVertexCount;
    private bool _glyphBatchActive;
    private bool _glyphBatchIsSdf;
    // SDF batches share a single fontSize (drives edge-softness push constant).
    private float _glyphBatchFontSize;
    // SDF glyphs are accumulated per atlas page (the atlas may span several page textures, each
    // with its own descriptor set). At EndGlyphBatch each non-empty page is one bind+draw. Lists
    // are reused across frames (Clear, not realloc). Index = atlas page index.
    private readonly List<List<float>> _sdfPageVertices = new();

    // Reused per-line shaping buffer (cleared + refilled by ITextShaper.Shape each call). DrawText
    // runs every frame for every UI label, so this stays allocation-free after warmup — a foreach
    // over List<ShapedGlyph> uses the struct enumerator (no alloc). Not reentrant: DrawText fully
    // consumes it per line before reshaping, and never calls MeasureText mid-iteration.
    private readonly List<ShapedGlyph> _shapedLine = new();

    // sdfInitialAtlasDim: square size of each SDF atlas PAGE (0 = the atlas default, 2048²). The
    // atlas never reallocates — when a page fills it appends a new page — so this is the page
    // granularity, not a glyph cap. A glyph-heavy consumer can raise it (must be a power of two)
    // to pack more glyphs per page = fewer pages / fewer per-page draw calls.
    // rasterizer: an optional PROCESS-OWNED glyph rasterizer shared across windows. A multi-window host
    // passes one so every window's atlas — and the PDF parser — rasterize through the same instance,
    // which lets a document tab tear out into another window without losing its embedded-font
    // registrations when the origin window closes. When null, this renderer's atlas creates and owns its
    // own rasterizer (the single-window / standalone case), unchanged.
    public VkRenderer(VulkanContext ctx, uint width, uint height, SdfGlyphDiskCache? sdfDiskCache = null,
        int sdfInitialAtlasDim = 0, ManagedFontRasterizer? rasterizer = null) : base(ctx)
    {
        _width = width;
        _height = height;
        _pipelines = VkPipelineSet.Create(ctx);
        _fontAtlas = new VkFontAtlas(ctx, rasterizer);
        _sdfFontAtlas = sdfInitialAtlasDim > 0
            ? new VkSdfFontAtlas(ctx, _fontAtlas.Rasterizer, sdfDiskCache, sdfInitialAtlasDim, sdfInitialAtlasDim)
            : new VkSdfFontAtlas(ctx, _fontAtlas.Rasterizer, sdfDiskCache);
        UpdateProjection();
    }

    public override uint Width => _width;
    public override uint Height => _height;

    public VkPipelineSet? Pipelines => _pipelines;
    internal VkFontAtlas? FontAtlas => _fontAtlas;
    public ManagedFontRasterizer? GlyphRasterizer => _fontAtlas?.Rasterizer;
    public bool FontAtlasDirty => _fontAtlas?.IsDirty == true || _sdfFontAtlas?.IsDirty == true;

    public VulkanContext Context => Surface;

    /// <summary>
    /// The active command buffer for the current frame. Only valid between BeginFrame and EndFrame.
    /// Allows side-car pipelines to record custom draw commands within the same render pass.
    /// </summary>
    public VkCommandBuffer CurrentCommandBuffer
    {
        get
        {
            // A side-car may bind its own pipeline into this command buffer — the renderer's
            // redundant-bind cache can no longer assume its pipeline is still bound.
            _lastBoundPipeline = VkPipeline.Null;
            return _currentCmd;
        }
    }

    // Last pipeline bound into the current command buffer. vkCmdBindPipeline is a real
    // state-change token on the GPU command processor (a tile flush on mobile GPUs), and
    // page-content rendering binds the same Flat/Stroke pipeline hundreds of times in a row.
    // Push constants are always (re-)pushed by every draw — only the bind is deduplicated.
    private VkPipeline _lastBoundPipeline;

    private void BindPipeline(VkPipeline pipeline)
    {
        if (pipeline == _lastBoundPipeline) return;
        Surface.DeviceApi.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, pipeline);
        _lastBoundPipeline = pipeline;
    }

    /// <summary>
    /// Measures the size of the given text in pixels at the specified font size.
    /// Returns (width, height) where height is ascent + descent.
    /// </summary>
    public override (float Width, float Height) MeasureText(ReadOnlySpan<char> text, string fontFamily, float fontSize)
    {
        if (_sdfFontAtlas is null || text.IsEmpty)
            return (0f, 0f);

        var glyphScale = VkSdfFontAtlas.GetGlyphScale(fontSize);
        var bitmapScale = VkFontAtlas.GetGlyphScale(fontSize);

        // Same shaper as DrawText, so measured width matches drawn advance exactly (incl. any
        // opt-in kerning). Under AdvanceShaper this is the old per-rune advance sum verbatim —
        // '\n' is treated as a whitespace rune (its advance derives from the 'n' reference glyph),
        // matching the pre-seam loop which also never split lines here.
        TextShaper.Shape(text, fontFamily, fontSize, _sdfFontAtlas.Rasterizer, _shapedLine);

        var width = 0f;
        var maxAscent = 0f;
        var maxDescent = 0f;
        foreach (var sg in _shapedLine)
        {
            var ch = sg.Source;
            float advance, bearingY, height;
            var isEmoji = ch.Value >= 0x1F000
                || (ch.Value >= 0x2600 && ch.Value <= 0x27BF)
                || (ch.Value >= 0xFE00 && ch.Value <= 0xFE0F)
                || ch.Value == 0x200D;
            if (isEmoji && _fontAtlas is not null)
            {
                var bg = _fontAtlas.GetGlyph(fontFamily, fontSize, ch);
                advance = bg.AdvanceX * bitmapScale;
                bearingY = bg.BearingY * bitmapScale;
                height = bg.Height * bitmapScale;
            }
            else
            {
                var glyph = _sdfFontAtlas.GetGlyph(fontFamily, fontSize, ch);
                advance = glyph.AdvanceX * glyphScale;
                bearingY = glyph.BearingY * glyphScale;
                height = glyph.Height * glyphScale;
            }
            width += advance + sg.XAdvanceAdjust;
            if (bearingY > maxAscent) maxAscent = bearingY;
            var descent = height - bearingY;
            if (descent > maxDescent) maxDescent = descent;
        }
        return (width, maxAscent + maxDescent);
    }

    /// <summary>
    /// Called after font atlas BeginFrame but before Flush.
    /// Use to pre-warm glyphs so they are available in the same frame they're first needed.
    /// </summary>
    public Action? OnPreFlush { get; set; }

    /// <summary>
    /// Called after font atlas flush but before render pass begin.
    /// Use for recording texture upload commands (transfers must happen outside render pass).
    /// </summary>
    public Action<VkCommandBuffer>? OnPreRenderPass { get; set; }

    /// <summary>
    /// Begins a new frame. Must be called before any draw calls.
    /// Returns false if the swapchain needs recreation (caller should resize and retry).
    /// </summary>
    public bool BeginFrame(DIR.Lib.RGBAColor32 clearColor)
    {
        _currentCmd = Surface.BeginFrame(out var resized);
        if (resized || _currentCmd == VkCommandBuffer.Null)
            return false;

        // Handle deferred eviction, then flush font atlas changes before render pass
        _fontAtlas?.BeginFrame();
        _sdfFontAtlas?.BeginFrame();
        OnPreFlush?.Invoke();
        _fontAtlas?.Flush(_currentCmd);
        _sdfFontAtlas?.Flush(_currentCmd);

        // Record pending texture uploads before the render pass (transfers can't happen inside)
        OnPreRenderPass?.Invoke(_currentCmd);

        Surface.BeginRenderPass(_currentCmd, clearColor.Red / 255f, clearColor.Green / 255f, clearColor.Blue / 255f, clearColor.Alpha / 255f);
        _lastBoundPipeline = VkPipeline.Null; // fresh command buffer — nothing is bound
        return true;
    }

    /// <summary>
    /// Ends the current frame and presents.
    /// </summary>
    public void EndFrame()
    {
        Surface.EndFrame(_currentCmd);
    }

    // ---- Live-device thumbnail capture (see VulkanContext.ThumbnailCapture.cs) ----

    /// <summary>
    /// Allocate the live-device thumbnail capture target once, up front (never mid steady-state).
    /// Size it to the largest thumbnail you will request — per-page captures use a (w,h) sub-rect.
    /// </summary>
    public bool EnsureThumbnailTarget(uint maxW, uint maxH) => Surface.EnsureThumbnailTarget(maxW, maxH);

    /// <summary>True while a capture is recorded-but-unconsumed or a finished snapshot awaits fetch.</summary>
    public bool ThumbnailCaptureBusy => Surface.ThumbnailCaptureBusy;

    /// <summary>
    /// Opens the thumbnail capture render pass on the current frame's command buffer and redirects the
    /// projection to a (w,h) target, so subsequent DrawPersistent* / glyph / textured draws land in the
    /// offscreen thumbnail target at thumbnail scale (page origin 0,0). MUST be called from the
    /// OnPreRenderPass hook (before the main render pass) and bracketed by <see cref="EndThumbnailCapture"/>.
    /// Returns false (and changes nothing) if the target isn't ready or a capture is already in flight.
    /// </summary>
    public bool BeginThumbnailCapture(uint w, uint h)
    {
        if (_currentCmd == VkCommandBuffer.Null || _inThumbnailCapture) return false;
        if (!Surface.BeginThumbnailCapturePass(_currentCmd, w, h)) return false;

        _savedWidth = _width;
        _savedHeight = _height;
        _width = w;
        _height = h;
        // Refresh the cached screen-space projection matrix: glyph and textured-quad (image) draws
        // read _pushConstants, not _width/_height inline (only DrawPersistent* recompute inline), so
        // without this text and images would be projected at the swapchain scale into the thumbnail.
        UpdateProjection();
        // The command buffer is fresh this frame (reset in BeginFrame) but _lastBoundPipeline still
        // holds the previous frame's pipeline and isn't cleared until after the main BeginRenderPass.
        // Force a real bind on the first capture draw so we don't skip binding into an empty cmd.
        _lastBoundPipeline = VkPipeline.Null;
        _inThumbnailCapture = true;
        return true;
    }

    /// <summary>
    /// Closes the capture pass opened by <see cref="BeginThumbnailCapture"/>, records the readback
    /// copy, and restores the swapchain projection. The captured pixels become available
    /// (non-blocking, a couple of frames later) via <see cref="TryGetThumbnailCapture"/>.
    /// </summary>
    public void EndThumbnailCapture()
    {
        if (!_inThumbnailCapture) return;
        Surface.EndThumbnailCapturePassAndCopy(_currentCmd);
        _width = _savedWidth;
        _height = _savedHeight;
        UpdateProjection(); // restore the swapchain-scale projection for the main render pass
        _lastBoundPipeline = VkPipeline.Null; // main render pass will rebind from scratch
        _inThumbnailCapture = false;
    }

    /// <summary>
    /// Fetches the most recent finished capture (RGBA, top-to-bottom rows). Call once per frame (e.g.
    /// from OnPreFlush). Returns false if none is ready.
    /// </summary>
    public bool TryGetThumbnailCapture(out byte[] rgba, out int width, out int height)
        => Surface.TryGetThumbnailReadback(out rgba, out width, out height);

    /// <summary>
    /// Offscreen equivalent of <see cref="BeginFrame"/>. Requires the backing context was
    /// created via <see cref="VulkanContext.CreateOffscreen"/>. Orchestrates the same font
    /// atlas + pre-render-pass callbacks as the swapchain path so consumers (VectorPageRenderer
    /// etc.) don't need to know which mode they're in.
    /// </summary>
    public bool BeginOffscreenFrame(DIR.Lib.RGBAColor32 clearColor)
    {
        _currentCmd = Surface.BeginOffscreenFrame();
        if (_currentCmd == VkCommandBuffer.Null) return false;

        _fontAtlas?.BeginFrame();
        _sdfFontAtlas?.BeginFrame();
        OnPreFlush?.Invoke();
        _fontAtlas?.Flush(_currentCmd);
        _sdfFontAtlas?.Flush(_currentCmd);

        OnPreRenderPass?.Invoke(_currentCmd);

        Surface.BeginOffscreenRenderPass(_currentCmd,
            clearColor.Red / 255f, clearColor.Green / 255f, clearColor.Blue / 255f, clearColor.Alpha / 255f);
        _lastBoundPipeline = VkPipeline.Null; // fresh command buffer — nothing is bound
        return true;
    }

    /// <summary>Ends the offscreen frame; pair with <see cref="BeginOffscreenFrame"/>. No present.</summary>
    public void EndOffscreenFrame()
    {
        Surface.EndOffscreenFrame(_currentCmd);
    }

    public override void Resize(uint width, uint height)
    {
        _width = width;
        _height = height;
        Surface.RecreateSwapchain(width, height);
        UpdateProjection();
    }

    /// <summary>
    /// Resize the offscreen render target (offscreen contexts only) and update the projection to
    /// match. Unlike <see cref="Resize"/> this rebuilds the single VkImage target, not a swapchain,
    /// and leaves the glyph atlases intact — for multi-page offscreen raster/export where pages
    /// differ in size but should share a warm atlas.
    /// </summary>
    public void ResizeOffscreen(uint width, uint height)
    {
        _width = width;
        _height = height;
        Surface.ResizeOffscreen(width, height);
        UpdateProjection();
    }

    /// <summary>
    /// Drop the current frame's command buffer reference and ask the underlying context to
    /// rebuild its sync objects and swapchain. Use from an outer try/catch when a Vulkan call
    /// (typically vkQueueSubmit/Present) throws mid-frame, so the next frame can start clean
    /// instead of hanging on a stuck fence from the failed submit.
    /// </summary>
    public void RecoverFromGpuError()
    {
        _currentCmd = VkCommandBuffer.Null;
        _lastBoundPipeline = VkPipeline.Null;
        Surface.RecoverFromGpuError(_width, _height);
        UpdateProjection();
    }

    public override void FillRectangle(in RectInt rect, DIR.Lib.RGBAColor32 fillColor)
    {
        if (_pipelines is null) return;

        var api = Surface.DeviceApi;
        var x0 = (float)rect.UpperLeft.X;
        var y0 = (float)rect.UpperLeft.Y;
        var x1 = (float)rect.LowerRight.X;
        var y1 = (float)rect.LowerRight.Y;

        ReadOnlySpan<float> vertices =
        [
            x0, y0, x1, y0, x1, y1,
            x0, y0, x1, y1, x0, y1
        ];

        SetColor(fillColor);
        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.FlatPipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    // Scratch vertex accumulator for FillRectangles — reused across calls to avoid a per-call
    // allocation. Render-thread only, like all draw APIs.
    private readonly List<float> _rectScratch = new();

    public override void FillRectangles(ReadOnlySpan<(RectInt Rect, DIR.Lib.RGBAColor32 Color)> rectangles)
    {
        if (_pipelines is null || rectangles.IsEmpty) return;

        var api = Surface.DeviceApi;

        // Batch consecutive same-color rectangles into one vertex run + one draw. Draw order
        // is preserved (rectangles may overlap), so only ADJACENT runs merge — which already
        // collapses the common UI case (rows, grids, highlight sets) to a handful of draws
        // instead of bind+push+draw per rectangle.
        var start = 0;
        while (start < rectangles.Length)
        {
            var color = rectangles[start].Color;
            var end = start + 1;
            while (end < rectangles.Length && rectangles[end].Color == color)
                end++;

            _rectScratch.Clear();
            for (var i = start; i < end; i++)
            {
                var r = rectangles[i].Rect;
                var x0 = (float)r.UpperLeft.X;
                var y0 = (float)r.UpperLeft.Y;
                var x1 = (float)r.LowerRight.X;
                var y1 = (float)r.LowerRight.Y;
                _rectScratch.AddRange([x0, y0, x1, y0, x1, y1, x0, y0, x1, y1, x0, y1]);
            }

            SetColor(color);
            var offset = Surface.WriteVertices(CollectionsMarshal.AsSpan(_rectScratch));
            if (offset != uint.MaxValue)
            {
                BindPipeline(_pipelines.FlatPipeline);
                fixed (float* pPC = _pushConstants)
                    api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                        VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

                var buffer = Surface.VertexBuffer;
                var vkOffset = (ulong)offset;
                api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
                api.vkCmdDraw(_currentCmd, (uint)((end - start) * 6), 1, 0, 0);
            }

            start = end;
        }
    }

    /// <summary>
    /// Draws raw triangles via the FlatPipeline. Vertices are flat x,y pairs (2 floats per vertex,
    /// 3 vertices per triangle). All triangles share the same color.
    /// </summary>
    public void DrawTriangles(ReadOnlySpan<float> vertices, DIR.Lib.RGBAColor32 color)
    {
        if (_pipelines is null || vertices.Length < 6) return;

        var api = Surface.DeviceApi;
        var vertexCount = (uint)(vertices.Length / 2);

        SetColor(color);
        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.FlatPipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, vertexCount, 1, 0, 0);
    }

    /// <summary>
    /// Draws triangles with a custom origin and scale (e.g. for tiled/paged rendering).
    /// Builds a combined projection so vertices stay in their original coordinate space.
    /// </summary>
    public void DrawTrianglesTransformed(ReadOnlySpan<float> vertices, DIR.Lib.RGBAColor32 color,
        float originX, float originY, float scale)
    {
        if (_pipelines is null || vertices.Length < 6) return;

        var api = Surface.DeviceApi;
        var vertexCount = (uint)(vertices.Length / 2);

        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        Span<float> pc = stackalloc float[21];
        var w = (float)_width;
        var h = (float)_height;
        pc[0]  = 2f * scale / w;
        pc[5]  = 2f * scale / h;
        pc[10] = -1f;
        pc[12] = 2f * originX / w - 1f;
        pc[13] = 2f * originY / h - 1f;
        pc[15] = 1f;
        pc[16] = color.Red / 255f;
        pc[17] = color.Green / 255f;
        pc[18] = color.Blue / 255f;
        pc[19] = color.Alpha / 255f;

        BindPipeline(_pipelines.FlatPipeline);
        fixed (float* pPC = pc)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, vertexCount, 1, 0, 0);
    }

    /// <summary>
    /// Draws triangles from a persistent GPU buffer with a custom origin and scale.
    /// No frame-allocator usage — the buffer is pre-uploaded and reused across frames.
    /// </summary>
    public void DrawPersistentTriangles(Vortice.Vulkan.VkBuffer buffer, uint byteOffset, uint vertexCount,
        DIR.Lib.RGBAColor32 color, float originX, float originY, float scale,
        VkPipeline? pipelineOverride = null)
    {
        if (_pipelines is null || vertexCount < 3) return;

        var api = Surface.DeviceApi;

        Span<float> pc = stackalloc float[21];
        var w = (float)_width;
        var h = (float)_height;
        pc[0]  = 2f * scale / w;
        pc[5]  = 2f * scale / h;
        pc[10] = -1f;
        pc[12] = 2f * originX / w - 1f;
        pc[13] = 2f * originY / h - 1f;
        pc[15] = 1f;
        pc[16] = color.Red / 255f;
        pc[17] = color.Green / 255f;
        pc[18] = color.Blue / 255f;
        pc[19] = color.Alpha / 255f;

        BindPipeline(pipelineOverride ?? _pipelines.FlatPipeline);
        fixed (float* pPC = pc)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var vkOffset = (ulong)byteOffset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, vertexCount, 1, 0, 0);
    }

    /// <summary>
    /// Draws stroke segments from a persistent GPU buffer with a custom origin and scale.
    /// No frame-allocator usage — the buffer is pre-uploaded and reused across frames.
    /// </summary>
    public void DrawPersistentStrokes(Vortice.Vulkan.VkBuffer buffer, uint byteOffset, uint vertexCount,
        DIR.Lib.RGBAColor32 color, float originX, float originY, float scale, float halfWidth)
    {
        if (_pipelines is null || vertexCount < 6) return;

        var api = Surface.DeviceApi;

        Span<float> pc = stackalloc float[21];
        var w = (float)_width;
        var h = (float)_height;
        pc[0]  = 2f * scale / w;
        pc[5]  = 2f * scale / h;
        pc[10] = -1f;
        pc[12] = 2f * originX / w - 1f;
        pc[13] = 2f * originY / h - 1f;
        pc[15] = 1f;
        pc[16] = color.Red / 255f;
        pc[17] = color.Green / 255f;
        pc[18] = color.Blue / 255f;
        pc[19] = color.Alpha / 255f;
        pc[20] = halfWidth;

        BindPipeline(_pipelines.StrokePipeline);
        fixed (float* pPC = pc)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var vkOffset = (ulong)byteOffset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, vertexCount, 1, 0, 0);
    }

    /// <summary>
    /// Draws stroke segments via the StrokePipeline with a custom origin and scale.
    /// Vertices are 6 floats each (P0, P1, side/end params), 6 vertices per line segment.
    /// </summary>
    public void DrawStrokeSegments(ReadOnlySpan<float> segmentVertices, DIR.Lib.RGBAColor32 color,
        float originX, float originY, float scale, float halfWidth)
    {
        if (_pipelines is null || segmentVertices.Length < 36) return;

        var api = Surface.DeviceApi;
        var vertexCount = (uint)(segmentVertices.Length / 6);

        var offset = Surface.WriteVertices(segmentVertices);
        if (offset == uint.MaxValue) return;

        Span<float> pc = stackalloc float[21];
        var w = (float)_width;
        var h = (float)_height;
        pc[0]  = 2f * scale / w;
        pc[5]  = 2f * scale / h;
        pc[10] = -1f;
        pc[12] = 2f * originX / w - 1f;
        pc[13] = 2f * originY / h - 1f;
        pc[15] = 1f;
        pc[16] = color.Red / 255f;
        pc[17] = color.Green / 255f;
        pc[18] = color.Blue / 255f;
        pc[19] = color.Alpha / 255f;
        pc[20] = halfWidth;

        BindPipeline(_pipelines.StrokePipeline);
        fixed (float* pPC = pc)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, vertexCount, 1, 0, 0);
    }

    /// <summary>
    /// Draws a textured quad using the PagePipeline (pass-through, no glyph color detection).
    /// The texture must have its own VkDescriptorSet from VkTexture.
    /// </summary>
    public void DrawTexture(VkDescriptorSet textureSet, float x, float y, float w, float h)
    {
        DrawTextureRegion(textureSet, x, y, w, h, 0f, 0f, 1f, 1f);
    }

    /// <summary>
    /// Draws a texture mapped to an arbitrary quad defined by 4 corners.
    /// Corners are: (x0,y0)=image origin, (x1,y1)=right edge, (x2,y2)=bottom edge, (x3,y3)=far corner.
    /// UV mapping: (0,0) at (x0,y0), (1,0) at (x1,y1), (0,1) at (x2,y2), (1,1) at (x3,y3).
    /// </summary>
    public void DrawTexturedQuad(VkDescriptorSet textureSet,
        float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        if (_pipelines is null) return;

        var api = Surface.DeviceApi;

        ReadOnlySpan<float> vertices =
        [
            x0, y0, 0f, 0f,  // image origin (UV 0,0)
            x1, y1, 1f, 0f,  // right edge   (UV 1,0)
            x3, y3, 1f, 1f,  // far corner    (UV 1,1)
            x0, y0, 0f, 0f,  // image origin  (UV 0,0)
            x3, y3, 1f, 1f,  // far corner    (UV 1,1)
            x2, y2, 0f, 1f   // bottom edge   (UV 0,1)
        ];

        _pushConstants[16] = 1f;
        _pushConstants[17] = 1f;
        _pushConstants[18] = 1f;
        _pushConstants[19] = 1f;

        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.PagePipeline);

        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
            Surface.PipelineLayout, 0, 1, &textureSet, 0, null);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    /// <summary>
    /// Draws a sub-region of a texture using the PagePipeline.
    /// UV coordinates specify which part of the texture to sample.
    /// </summary>
    public void DrawTextureRegion(VkDescriptorSet textureSet, float x, float y, float w, float h,
        float u0, float v0, float u1, float v1)
    {
        if (_pipelines is null) return;

        var api = Surface.DeviceApi;

        var x0 = x;
        var y0 = y;
        var x1 = x + w;
        var y1 = y + h;

        ReadOnlySpan<float> vertices =
        [
            x0, y0, u0, v0,
            x1, y0, u1, v0,
            x1, y1, u1, v1,
            x0, y0, u0, v0,
            x1, y1, u1, v1,
            x0, y1, u0, v1
        ];

        // Set color to white so push constants are valid (PagePipeline ignores it, but layout expects 84 bytes)
        _pushConstants[16] = 1f;
        _pushConstants[17] = 1f;
        _pushConstants[18] = 1f;
        _pushConstants[19] = 1f;

        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.PagePipeline);

        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
            Surface.PipelineLayout, 0, 1, &textureSet, 0, null);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    /// <summary>
    /// Sets the scissor rect for clipping. Call ResetScissor to restore to full viewport.
    /// </summary>
    public void SetScissor(int x, int y, uint w, uint h)
    {
        var api = Surface.DeviceApi;
        VkRect2D scissor = new(x, y, w, h);
        api.vkCmdSetScissor(_currentCmd, 0, scissor);
    }

    /// <summary>
    /// Resets the scissor rect to the full viewport.
    /// </summary>
    public void ResetScissor()
    {
        SetScissor(0, 0, _width, _height);
    }

    // DIR.Lib widget clip hooks → Vulkan scissor. Single-level (per the base contract): PopClip
    // restores the full viewport rather than popping a stack.
    public override void PushClip(in DIR.Lib.RectInt rect)
    {
        var x = Math.Min(rect.UpperLeft.X, rect.LowerRight.X);
        var y = Math.Min(rect.UpperLeft.Y, rect.LowerRight.Y);
        var w = (uint)Math.Abs(rect.LowerRight.X - rect.UpperLeft.X);
        var h = (uint)Math.Abs(rect.LowerRight.Y - rect.UpperLeft.Y);
        SetScissor(x, y, w, h);
    }

    public override void PopClip() => ResetScissor();

    /// <summary>
    /// Pre-warms a glyph in the font atlas so it's available in the current frame's flush.
    /// Call this from OnPreFlush to avoid 1-frame text flicker when font sizes change.
    /// </summary>
    public void PreWarmGlyph(string fontPath, float fontSize, System.Text.Rune character, int charCode = -1, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto)
    {
        _fontAtlas?.GetGlyph(fontPath, fontSize, character, charCode: charCode, hint: hint);
    }

    /// <summary>
    /// Draws a single glyph at the exact ink position.
    /// Supports CID subset fonts via charCode for glyph index lookup.
    /// <para>
    /// <paramref name="xScale"/> stretches the rendered quad along the writing direction
    /// only. The atlas glyph itself remains at the uniform <paramref name="fontSize"/>
    /// rasterization — we just scale the output quad's writing-axis vertex offsets.
    /// </para>
    /// </summary>
    public void DrawSingleGlyph(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, DIR.Lib.RGBAColor32 color, float inkX, float inkY,
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto,
        float xScale = 1f)
    {
        if (_pipelines is null || _fontAtlas is null) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        var api = Surface.DeviceApi;

        // Scale glyph metrics when rasterized at a capped size (e.g., 128px max)
        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        var w = glyph.Width * glyphScale * xScale;
        var h = glyph.Height * glyphScale;

        // Build glyph quad — rotated or axis-aligned
        Span<float> verts = stackalloc float[24];
        if (MathF.Abs(rotation) < 0.01f)
        {
            // Fast path: axis-aligned (no trig needed for ~99% of text)
            var gx1 = inkX + w;
            var gy1 = inkY + h;
            verts[0] = inkX; verts[1] = inkY; verts[2] = glyph.U0; verts[3] = glyph.V0;
            verts[4] = gx1;  verts[5] = inkY; verts[6] = glyph.U1; verts[7] = glyph.V0;
            verts[8] = gx1;  verts[9] = gy1;  verts[10] = glyph.U1; verts[11] = glyph.V1;
            verts[12] = inkX; verts[13] = inkY; verts[14] = glyph.U0; verts[15] = glyph.V0;
            verts[16] = gx1;  verts[17] = gy1;  verts[18] = glyph.U1; verts[19] = glyph.V1;
            verts[20] = inkX; verts[21] = gy1;  verts[22] = glyph.U0; verts[23] = glyph.V1;
        }
        else
        {
            // Rotated quad: right = advance direction, down = perpendicular
            var cosA = MathF.Cos(rotation);
            var sinA = MathF.Sin(rotation);
            var rx = cosA * w;  var ry = sinA * w;   // "right" vector (advance dir)
            var ddx = -sinA * h; var ddy = cosA * h;  // "down" vector (perpendicular)

            var trx = inkX + rx;      var try_ = inkY + ry;
            var blx = inkX + ddx;     var bly = inkY + ddy;
            var brx = inkX + rx + ddx; var bry = inkY + ry + ddy;

            verts[0] = inkX; verts[1] = inkY; verts[2] = glyph.U0; verts[3] = glyph.V0;
            verts[4] = trx;  verts[5] = try_; verts[6] = glyph.U1; verts[7] = glyph.V0;
            verts[8] = brx;  verts[9] = bry;  verts[10] = glyph.U1; verts[11] = glyph.V1;
            verts[12] = inkX; verts[13] = inkY; verts[14] = glyph.U0; verts[15] = glyph.V0;
            verts[16] = brx;  verts[17] = bry;  verts[18] = glyph.U1; verts[19] = glyph.V1;
            verts[20] = blx;  verts[21] = bly;  verts[22] = glyph.U0; verts[23] = glyph.V1;
        }
        ReadOnlySpan<float> vertices = verts;

        SetColor(color);
        var vertOffset = Surface.WriteVertices(vertices);
        if (vertOffset == uint.MaxValue) return;

        BindPipeline(_pipelines.TexturedPipeline);

        var descriptorSet = Surface.DescriptorSet;
        api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
            Surface.PipelineLayout, 0, 1, &descriptorSet, 0, null);

        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)vertOffset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    /// <summary>
    /// Draw a glyph positioned at the text baseline (not ink-top).
    /// Computes ink-top from baseline using the glyph's FreeType bearing.
    /// Use this when the caller provides baseline coordinates (e.g., PDF.Lib parser).
    /// </summary>
    public void DrawGlyphAtBaseline(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, DIR.Lib.RGBAColor32 color, float baselineX, float baselineY,
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto,
        float xScale = 1f)
    {
        if (_pipelines is null || _fontAtlas is null) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        // Bearing-X lives along the writing direction, so it must scale with xScale —
        // otherwise compressed glyphs drift right of their narrow advance slots.
        var inkX = baselineX + glyph.BearingX * glyphScale * xScale;
        var inkY = baselineY - glyph.BearingY * glyphScale;

        DrawSingleGlyph(fontPath, fontSize, character, charCode, color, inkX, inkY, rotation, hint, xScale);
    }

    /// <summary>
    /// Begins a glyph batch. All subsequent AddBatchedGlyph/AddBatchedGlyphAtBaseline calls
    /// accumulate vertex data contiguously. Call EndGlyphBatch to issue a single draw call.
    /// </summary>
    public void BeginGlyphBatch(DIR.Lib.RGBAColor32 color)
    {
        _glyphBatchActive = true;
        _glyphBatchIsSdf = false;
        _glyphBatchStartOffset = uint.MaxValue;
        _glyphBatchVertexCount = 0;
        SetColor(color);
    }

    /// <summary>
    /// Begins an SDF glyph batch. All subsequent AddBatchedSdfGlyph/AddBatchedSdfGlyphAtBaseline
    /// calls accumulate into a single draw through the SdfPipeline. The whole batch shares one
    /// <paramref name="fontSize"/>, which drives the edge-softness push constant — callers must
    /// end the batch and begin a new one when fontSize changes.
    /// </summary>
    public void BeginSdfGlyphBatch(DIR.Lib.RGBAColor32 color, float fontSize)
    {
        _glyphBatchActive = true;
        _glyphBatchIsSdf = true;
        _glyphBatchFontSize = fontSize;
        _glyphBatchStartOffset = uint.MaxValue;
        _glyphBatchVertexCount = 0;
        foreach (var l in _sdfPageVertices) l.Clear();  // reuse the per-page buffers, don't realloc
        SetColor(color);
    }

    /// <summary>
    /// Adds a glyph to the current batch at the exact ink position (no draw call issued).
    /// <para>
    /// <paramref name="xScale"/> stretches the rendered quad along the writing direction only
    /// (the atlas glyph itself stays at the uniform <paramref name="fontSize"/> rasterization).
    /// 1.0 means no horizontal stretch — the common case. Values &lt; 1 compress glyphs
    /// horizontally; used for PDF text with /Tz or an anisotropic Tm/cm so wide outlines
    /// drawn on narrow advances don't crowd each other.
    /// </para>
    /// </summary>
    public void AddBatchedGlyph(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, float inkX, float inkY,
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto,
        float xScale = 1f)
    {
        if (_pipelines is null || _fontAtlas is null || !_glyphBatchActive) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        AddBatchedGlyph(glyph, fontSize, inkX, inkY, rotation, xScale);
    }

    /// <summary>
    /// Assembly-internal overload that accepts a pre-resolved <see cref="VkFontAtlas.GlyphInfo"/>
    /// directly — skips the atlas dictionary lookup that the <c>fontPath</c> overload performs
    /// internally. Used by <see cref="DrawText"/> which already holds the <c>GlyphInfo</c> from
    /// its metrics pass and would otherwise pay for a second lookup. Passed by <c>in</c> to
    /// avoid copying the ~36-byte record struct on the per-glyph hot path.
    /// </summary>
    internal void AddBatchedGlyph(in VkFontAtlas.GlyphInfo glyph, float fontSize,
        float inkX, float inkY, float rotation = 0f, float xScale = 1f)
    {
        if (_pipelines is null || _fontAtlas is null || !_glyphBatchActive) return;
        if (glyph.Width == 0) return;

        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        // Stretch only the writing direction. The atlas glyph stays at its uniform-fontSize
        // rasterization — we just scale the output quad's "right" basis vector by xScale.
        // Vertical ("down" basis) is unchanged.
        var w = glyph.Width * glyphScale * xScale;
        var h = glyph.Height * glyphScale;

        Span<float> verts = stackalloc float[24];
        if (MathF.Abs(rotation) < 0.01f)
        {
            var gx1 = inkX + w;
            var gy1 = inkY + h;
            verts[0] = inkX; verts[1] = inkY; verts[2] = glyph.U0; verts[3] = glyph.V0;
            verts[4] = gx1;  verts[5] = inkY; verts[6] = glyph.U1; verts[7] = glyph.V0;
            verts[8] = gx1;  verts[9] = gy1;  verts[10] = glyph.U1; verts[11] = glyph.V1;
            verts[12] = inkX; verts[13] = inkY; verts[14] = glyph.U0; verts[15] = glyph.V0;
            verts[16] = gx1;  verts[17] = gy1;  verts[18] = glyph.U1; verts[19] = glyph.V1;
            verts[20] = inkX; verts[21] = gy1;  verts[22] = glyph.U0; verts[23] = glyph.V1;
        }
        else
        {
            var cosA = MathF.Cos(rotation);
            var sinA = MathF.Sin(rotation);
            var rx = cosA * w;  var ry = sinA * w;
            var ddx = -sinA * h; var ddy = cosA * h;
            var trx = inkX + rx;      var try_ = inkY + ry;
            var blx = inkX + ddx;     var bly = inkY + ddy;
            var brx = inkX + rx + ddx; var bry = inkY + ry + ddy;

            verts[0] = inkX; verts[1] = inkY; verts[2] = glyph.U0; verts[3] = glyph.V0;
            verts[4] = trx;  verts[5] = try_; verts[6] = glyph.U1; verts[7] = glyph.V0;
            verts[8] = brx;  verts[9] = bry;  verts[10] = glyph.U1; verts[11] = glyph.V1;
            verts[12] = inkX; verts[13] = inkY; verts[14] = glyph.U0; verts[15] = glyph.V0;
            verts[16] = brx;  verts[17] = bry;  verts[18] = glyph.U1; verts[19] = glyph.V1;
            verts[20] = blx;  verts[21] = bly;  verts[22] = glyph.U0; verts[23] = glyph.V1;
        }
        ReadOnlySpan<float> vertices = verts;

        var vertOffset = Surface.WriteVertices(vertices);
        if (vertOffset == uint.MaxValue) return;

        if (_glyphBatchStartOffset == uint.MaxValue)
            _glyphBatchStartOffset = vertOffset;
        _glyphBatchVertexCount += 6;
    }

    /// <summary>
    /// Adds a glyph to the current batch at the text baseline position (no draw call issued).
    /// Computes ink-top from baseline using FreeType bearings, accounting for rotation —
    /// for rotated text the baseline-to-ink-top offset must follow the rotated text frame,
    /// not the screen axes, or every rotated glyph lands at the wrong screen position.
    /// <para>
    /// <paramref name="xIsInkLeft"/>: when false (default), <paramref name="baselineX"/> is
    /// the glyph origin (pen position / cell-left) and the glyph's left-side bearing is
    /// added to reach ink-left. When true, the caller has already resolved ink-left and the
    /// LSB add is skipped — useful when feeding per-glyph positions from a layout engine
    /// that reports ink boxes (pdfium's GetCharQuad) rather than pen positions.
    /// </para>
    /// </summary>
    public void AddBatchedGlyphAtBaseline(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, float baselineX, float baselineY,
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto,
        bool xIsInkLeft = false, float xScale = 1f)
    {
        if (_pipelines is null || _fontAtlas is null || !_glyphBatchActive) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        // Skip the LSB shift when the caller already has ink-left — otherwise narrow glyphs
        // in monospace fonts (e.g. 'S' or 'i' in Courier) end up shifted right by their LSB,
        // opening a visible gap between them and the next glyph.
        // The LSB lives along the writing direction so it must scale with xScale: when text
        // is horizontally compressed, the bearing-X shifts proportionally — otherwise each
        // glyph drifts right of its baseline by an unstretched LSB.
        var bx = xIsInkLeft ? 0f : glyph.BearingX * glyphScale * xScale;
        var by = glyph.BearingY * glyphScale;
        float inkX, inkY;
        if (MathF.Abs(rotation) < 0.001f)
        {
            // Hot path for horizontal text — same as the old formula.
            inkX = baselineX + bx;
            inkY = baselineY - by;
        }
        else
        {
            // Rotate the (bearingX along baseline, -bearingY perpendicular) offset into
            // screen coords. Baseline direction = (cos R, sin R) in Y-down; the "above
            // baseline" perpendicular is (sin R, -cos R) so the ink-top offset is
            // bx * baseline_dir + (-by) * up_dir.
            var cosR = MathF.Cos(rotation);
            var sinR = MathF.Sin(rotation);
            inkX = baselineX + bx * cosR + by * sinR;
            inkY = baselineY + bx * sinR - by * cosR;
        }

        AddBatchedGlyph(fontPath, fontSize, character, charCode, inkX, inkY, rotation, hint, xScale);
    }

    /// <summary>
    /// Adds an SDF glyph to the current batch at the exact ink top-left position (no draw call
    /// issued). All glyphs in the batch share the fontSize passed to <see cref="BeginSdfGlyphBatch"/>.
    /// <para>Semantics match <see cref="AddBatchedGlyph"/>: <paramref name="inkX"/>/<paramref name="inkY"/>
    /// is where the top-left of the glyph's *ink* bounding box should land. The SDF texture itself
    /// extends <c>spread*scale</c> pixels beyond the ink on every side; this function offsets the
    /// quad so the ink inside lines up with the caller's coordinates.</para>
    /// </summary>
    public void AddBatchedSdfGlyph(string fontPath, System.Text.Rune character, int charCode,
        float inkX, float inkY, float rotation = 0f,
        DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto, float xScale = 1f)
    {
        if (_pipelines is null || _sdfFontAtlas is null || !_glyphBatchActive || !_glyphBatchIsSdf) return;

        // rasterizeOnMiss: false — the draw path must never rasterize on the render thread. A glyph
        // not yet in the atlas is queued for background rasterization and skipped this frame (Width=0);
        // it appears once DrainPendingRasterized inserts it (a redraw is kept alive via IsDirty).
        var glyph = _sdfFontAtlas.GetGlyph(fontPath, _glyphBatchFontSize, character,
            skipUnflushed: true, charCode: charCode, hint: hint, rasterizeOnMiss: false);
        AddBatchedSdfGlyph(glyph, inkX, inkY, rotation, xScale);
    }

    /// <summary>
    /// Assembly-internal overload that accepts a pre-resolved <see cref="VkSdfFontAtlas.GlyphInfo"/>
    /// directly — skips the atlas dictionary lookup that the <c>fontPath</c> overload performs
    /// internally. Uses the current batch's <c>fontSize</c> (set by <see cref="BeginSdfGlyphBatch"/>)
    /// to compute scale and spread padding. Passed by <c>in</c> to avoid copying the ~40-byte
    /// record struct on the per-glyph hot path.
    /// </summary>
    internal void AddBatchedSdfGlyph(in VkSdfFontAtlas.GlyphInfo glyph, float inkX, float inkY,
        float rotation = 0f, float xScale = 1f)
    {
        if (_pipelines is null || _sdfFontAtlas is null || !_glyphBatchActive || !_glyphBatchIsSdf) return;
        if (glyph.Width == 0) return;

        // The atlas may span several page textures; recover this glyph's page + page-local V
        // (U is already page-local). Each page is drawn separately in EndGlyphBatch.
        _sdfFontAtlas.DecodePage(glyph, out var page, out var lv0, out var lv1);

        var glyphScale = VkSdfFontAtlas.GetGlyphScale(_glyphBatchFontSize);
        // Stretch only the writing direction. SDF spread padding follows xScale on the X-axis
        // too so the ink inside the texture continues to land at (inkX, inkY) after scaling
        // — otherwise compressed text would slip leftward by (1 - xScale) * spread.
        var w = glyph.Width * glyphScale * xScale;   // SDF texture width along writing dir (ink + 2*spread*xScale)
        var h = glyph.Height * glyphScale;            // SDF texture height (ink + 2*spread)
        var padX = glyph.Spread * glyphScale * xScale;
        var padY = glyph.Spread * glyphScale;

        Span<float> verts = stackalloc float[24];
        if (MathF.Abs(rotation) < 0.01f)
        {
            // Quad top-left = ink top-left shifted by (-padX, -padY) so the ink
            // inside the SDF texture lands at (inkX, inkY).
            var x0 = inkX - padX;
            var y0 = inkY - padY;
            var x1 = x0 + w;
            var y1 = y0 + h;
            verts[0] = x0; verts[1] = y0; verts[2] = glyph.U0; verts[3] = lv0;
            verts[4] = x1; verts[5] = y0; verts[6] = glyph.U1; verts[7] = lv0;
            verts[8] = x1; verts[9] = y1; verts[10] = glyph.U1; verts[11] = lv1;
            verts[12] = x0; verts[13] = y0; verts[14] = glyph.U0; verts[15] = lv0;
            verts[16] = x1; verts[17] = y1; verts[18] = glyph.U1; verts[19] = lv1;
            verts[20] = x0; verts[21] = y1; verts[22] = glyph.U0; verts[23] = lv1;
        }
        else
        {
            // Rotation basis: right = (cosA, sinA) for local +x, down = (-sinA, cosA) for local +y.
            // Texture top-left in rotated screen space = ink top-left shifted by -padX along the
            // writing axis and -padY along the perpendicular — otherwise the ink inside the texture
            // would land off along either direction.
            var cosA = MathF.Cos(rotation);
            var sinA = MathF.Sin(rotation);
            var rxU = cosA;  var ryU = sinA;   // right unit
            var dxU = -sinA; var dyU = cosA;   // down unit
            var tlx = inkX - padX * rxU - padY * dxU;
            var tly = inkY - padX * ryU - padY * dyU;
            var wrx = w * rxU; var wry = w * ryU;       // "right" scaled by texture width
            var hdx = h * dxU; var hdy = h * dyU;       // "down"  scaled by texture height
            var trx = tlx + wrx;        var try_ = tly + wry;
            var blx = tlx + hdx;        var bly  = tly + hdy;
            var brx = tlx + wrx + hdx;  var bry  = tly + wry + hdy;

            verts[0] = tlx; verts[1] = tly; verts[2] = glyph.U0; verts[3] = lv0;
            verts[4] = trx; verts[5] = try_; verts[6] = glyph.U1; verts[7] = lv0;
            verts[8] = brx; verts[9] = bry;  verts[10] = glyph.U1; verts[11] = lv1;
            verts[12] = tlx; verts[13] = tly; verts[14] = glyph.U0; verts[15] = lv0;
            verts[16] = brx; verts[17] = bry; verts[18] = glyph.U1; verts[19] = lv1;
            verts[20] = blx; verts[21] = bly; verts[22] = glyph.U0; verts[23] = lv1;
        }

        // Accumulate into this glyph's page bucket; EndGlyphBatch writes each page to the vertex
        // ring and issues one bind+draw per page. (No immediate WriteVertices — draws are grouped
        // by page so each binds its own page descriptor set.)
        while (_sdfPageVertices.Count <= page)
            _sdfPageVertices.Add(new List<float>(24 * 64));
        var pageList = _sdfPageVertices[page];
        pageList.AddRange(verts);
        _glyphBatchVertexCount += 6;
    }

    /// <summary>
    /// Adds an SDF glyph to the current batch at the text baseline position (no draw call issued).
    /// Computes ink-top from baseline using the glyph's bearing, then delegates to
    /// <see cref="AddBatchedSdfGlyph"/>.
    /// <para>
    /// <paramref name="xIsInkLeft"/>: when false (default), <paramref name="baselineX"/> is
    /// treated as glyph origin (pen position) and the glyph's LSB is added to reach ink-left.
    /// When true, <paramref name="baselineX"/> already IS ink-left and the LSB add is skipped
    /// — see the non-SDF <see cref="AddBatchedGlyphAtBaseline"/> overload for details.
    /// </para>
    /// </summary>
    public void AddBatchedSdfGlyphAtBaseline(string fontPath, System.Text.Rune character, int charCode,
        float baselineX, float baselineY, float rotation = 0f,
        DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto,
        bool xIsInkLeft = false, float xScale = 1f)
    {
        if (_pipelines is null || _sdfFontAtlas is null || !_glyphBatchActive || !_glyphBatchIsSdf) return;

        // rasterizeOnMiss: false — see AddBatchedSdfGlyph. Not-yet-rasterized glyphs queue + skip.
        var glyph = _sdfFontAtlas.GetGlyph(fontPath, _glyphBatchFontSize, character,
            skipUnflushed: true, charCode: charCode, hint: hint, rasterizeOnMiss: false);
        if (glyph.Width == 0) return;

        // BearingX/BearingY on the SDF atlas are to the SDF TEXTURE edges (inc. spread padding).
        // Convert to INK bearings so we can pass ink-top-left to AddBatchedSdfGlyph:
        //   ink_bearing_X = texture_bearing_X + spread (ink is spread pixels right of texture left)
        //   ink_bearing_Y = texture_bearing_Y - spread (ink is spread pixels below texture top)
        // When the caller already has ink-left (xIsInkLeft), skip the horizontal LSB add —
        // otherwise monospace narrow glyphs (pdfium's GetCharQuad returns ink boxes, not pen
        // positions) drift right by their own LSB, producing visible gaps in the rendered run.
        // The bearing-X lives along the writing direction so it must scale with xScale —
        // otherwise compressed glyphs slip out of their narrow advance slots.
        var glyphScale = VkSdfFontAtlas.GetGlyphScale(_glyphBatchFontSize);
        var bx = xIsInkLeft ? 0f : (glyph.BearingX + glyph.Spread) * glyphScale * xScale;
        var by = (glyph.BearingY - glyph.Spread) * glyphScale;
        float inkX, inkY;
        if (MathF.Abs(rotation) < 0.001f)
        {
            inkX = baselineX + bx;
            inkY = baselineY - by;
        }
        else
        {
            // Same rotated-bearing transform as the bitmap AddBatchedGlyphAtBaseline — see
            // the comment there for the derivation.
            var cosR = MathF.Cos(rotation);
            var sinR = MathF.Sin(rotation);
            inkX = baselineX + bx * cosR + by * sinR;
            inkY = baselineY + bx * sinR - by * cosR;
        }

        // The glyph is already resolved above for its bearings — hand it straight to the
        // internal overload instead of the fontPath one, which would look it up again.
        AddBatchedSdfGlyph(in glyph, inkX, inkY, rotation, xScale);
    }

    /// <summary>
    /// Pre-warms an SDF glyph so it's available in the current frame's flush.
    /// Mirror of <see cref="PreWarmGlyph"/> for the SDF atlas.
    /// </summary>
    public void PreWarmSdfGlyph(string fontPath, float fontSize, System.Text.Rune character,
        int charCode = -1, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto)
    {
        _sdfFontAtlas?.GetGlyph(fontPath, fontSize, character, charCode: charCode, hint: hint);
    }

    /// <summary>
    /// Batch SDF prewarm with parallel rasterization. Hand the renderer a list of unique
    /// glyph keys to prime — it dedups against the atlas, rasterizes the missing ones across
    /// the thread pool, then inserts them serially. Use instead of looping
    /// <see cref="PreWarmSdfGlyph"/> when a page has tens-to-hundreds of unique glyphs:
    /// the per-glyph SDF distance-field computation is the expensive part and parallelizes
    /// well (4x on a 4-core box for typical architectural pages).
    /// </summary>
    public void PreWarmSdfGlyphBatch(IReadOnlyList<(string Font, System.Text.Rune Character, int CharCode, DIR.Lib.GlyphMapHint Hint)> keys)
    {
        _sdfFontAtlas?.PreRasterizeBatch(keys);
    }

    /// <summary>
    /// Ends the current glyph batch and issues a single draw call for all accumulated glyphs.
    /// Dispatches to TexturedPipeline (bitmap atlas) or SdfPipeline (SDF atlas) based on which
    /// Begin*GlyphBatch opened the batch.
    /// </summary>
    public void EndGlyphBatch()
    {
        if (!_glyphBatchActive) return;
        var isSdf = _glyphBatchIsSdf;
        _glyphBatchActive = false;
        _glyphBatchIsSdf = false;

        var api = Surface.DeviceApi;
        // SDF AA is driven by fwidth() in the fragment shader, so no caller-side edge softness is
        // needed regardless of fontSize. Slot 20 is unused by both pipelines; zero it for cleanliness.
        _pushConstants[20] = 0f;

        if (isSdf && _sdfFontAtlas is not null)
        {
            if (_glyphBatchVertexCount == 0) return;
            // The atlas spans one or more page textures, each its own descriptor set. Bind the SDF
            // pipeline + push constants once, then issue ONE bind(page descriptor)+draw per page.
            // Rebinding descriptor sets between draws is legal Vulkan and Adreno-safe — pages are
            // never destroyed mid-frame, unlike the old Grow() image swap.
            BindPipeline(_pipelines!.SdfPipeline);
            fixed (float* pPC = _pushConstants)
                api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                    VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

            for (var p = 0; p < _sdfPageVertices.Count; p++)
            {
                var list = _sdfPageVertices[p];
                if (list.Count == 0) continue;
                var vertOffset = Surface.WriteVertices(CollectionsMarshal.AsSpan(list));
                if (vertOffset == uint.MaxValue) continue; // ring full this frame; drop the page
                var descriptorSet = _sdfFontAtlas.GetPageDescriptorSet(p);
                api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
                    Surface.PipelineLayout, 0, 1, &descriptorSet, 0, null);
                var buffer = Surface.VertexBuffer;
                var vkOffset = (ulong)vertOffset;
                api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
                api.vkCmdDraw(_currentCmd, (uint)(list.Count / 4), 1, 0, 0); // 4 floats (pos.xy+uv.xy)/vertex
            }
            return;
        }

        // Bitmap atlas path: a single contiguous vertex range, one draw (unchanged).
        if (_glyphBatchVertexCount == 0 || _glyphBatchStartOffset == uint.MaxValue) return;
        BindPipeline(_pipelines!.TexturedPipeline);
        var bmpDescriptor = Surface.DescriptorSet;
        api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
            Surface.PipelineLayout, 0, 1, &bmpDescriptor, 0, null);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);
        var bmpBuffer = Surface.VertexBuffer;
        var bmpOffset = (ulong)_glyphBatchStartOffset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &bmpBuffer, &bmpOffset);
        api.vkCmdDraw(_currentCmd, (uint)_glyphBatchVertexCount, 1, 0, 0);
    }

    /// <summary>
    /// Creates a persistent vertex buffer. The buffer lives until explicitly destroyed.
    /// </summary>
    public (Vortice.Vulkan.VkBuffer Buffer, Vortice.Vulkan.VkDeviceMemory Memory) CreatePersistentVertexBuffer(ReadOnlySpan<float> data)
        => Surface.CreatePersistentVertexBuffer(data);

    public void DestroyBuffer(Vortice.Vulkan.VkBuffer buffer, Vortice.Vulkan.VkDeviceMemory memory)
        => Surface.DestroyBuffer(buffer, memory);

    /// <summary>
    /// GPU-efficient line drawing: computes a rotated quad (2 triangles) from the
    /// line endpoints and submits via the FlatPipeline in a single draw call.
    /// </summary>
    public override void DrawLine(float x0, float y0, float x1, float y1, DIR.Lib.RGBAColor32 color, int thickness = 1)
    {
        if (_pipelines is null) return;

        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return;

        // Perpendicular normal scaled to half-thickness
        var hw = Math.Max(thickness, 1) * 0.5f;
        var nx = -dy / len * hw;
        var ny = dx / len * hw;

        // 4 corners of the rotated quad
        var ax = x0 + nx; var ay = y0 + ny;
        var bx = x0 - nx; var by = y0 - ny;
        var cx = x1 - nx; var cy = y1 - ny;
        var ex = x1 + nx; var ey = y1 + ny;

        // 2 triangles (6 vertices, 2 floats each)
        ReadOnlySpan<float> vertices =
        [
            ax, ay, bx, by, cx, cy,
            ax, ay, cx, cy, ex, ey
        ];

        DrawTriangles(vertices, color);
    }

    public override void DrawRectangle(in RectInt rect, DIR.Lib.RGBAColor32 strokeColor, int strokeWidth)
    {
        if (_pipelines is null) return;

        var api = Surface.DeviceApi;
        var x0 = (float)rect.UpperLeft.X;
        var y0 = (float)rect.UpperLeft.Y;
        var x1 = (float)rect.LowerRight.X;
        var y1 = (float)rect.LowerRight.Y;
        var sw = (float)strokeWidth;

        // All four sides share the pipeline and color — emit one 24-vertex draw instead of
        // four FillRectangle calls (4x bind + push constants + bind buffers + draw).
        Span<float> verts = stackalloc float[48];
        WriteQuad(verts, 0, x0, y0, x1, y0 + sw);             // top
        WriteQuad(verts, 12, x0, y1 - sw, x1, y1);            // bottom
        WriteQuad(verts, 24, x0, y0 + sw, x0 + sw, y1 - sw);  // left
        WriteQuad(verts, 36, x1 - sw, y0 + sw, x1, y1 - sw);  // right

        SetColor(strokeColor);
        var offset = Surface.WriteVertices(verts);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.FlatPipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 24, 1, 0, 0);
    }

    private static void WriteQuad(Span<float> dst, int at, float x0, float y0, float x1, float y1)
    {
        dst[at + 0] = x0; dst[at + 1] = y0;
        dst[at + 2] = x1; dst[at + 3] = y0;
        dst[at + 4] = x1; dst[at + 5] = y1;
        dst[at + 6] = x0; dst[at + 7] = y0;
        dst[at + 8] = x1; dst[at + 9] = y1;
        dst[at + 10] = x0; dst[at + 11] = y1;
    }

    public override void FillEllipse(in RectInt rect, DIR.Lib.RGBAColor32 fillColor)
    {
        if (_pipelines is null) return;

        var api = Surface.DeviceApi;
        var x0 = (float)rect.UpperLeft.X;
        var y0 = (float)rect.UpperLeft.Y;
        var x1 = (float)rect.LowerRight.X;
        var y1 = (float)rect.LowerRight.Y;

        ReadOnlySpan<float> vertices =
        [
            x0, y0, -1f, -1f,
            x1, y0,  1f, -1f,
            x1, y1,  1f,  1f,
            x0, y0, -1f, -1f,
            x1, y1,  1f,  1f,
            x0, y1, -1f,  1f
        ];

        SetColor(fillColor);
        _pushConstants[20] = 0f; // innerRadius = 0 → filled
        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.EllipsePipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    /// <summary>
    /// GPU-efficient ellipse outline via the EllipsePipeline ring shader.
    /// </summary>
    public override void DrawEllipse(in RectInt rect, DIR.Lib.RGBAColor32 strokeColor, float strokeWidth)
        => DrawEllipseOutline(rect, strokeColor, strokeWidth);

    /// <summary>
    /// Draws an ellipse outline (ring) with the given stroke width in pixels.
    /// </summary>
    public void DrawEllipseOutline(in RectInt rect, DIR.Lib.RGBAColor32 strokeColor, float strokeWidth)
    {
        if (_pipelines is null) return;

        var api = Surface.DeviceApi;
        var x0 = (float)rect.UpperLeft.X;
        var y0 = (float)rect.UpperLeft.Y;
        var x1 = (float)rect.LowerRight.X;
        var y1 = (float)rect.LowerRight.Y;

        // Compute inner radius in normalized [-1,1] space
        var radiusPixels = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) / 2f;
        var innerRadius = radiusPixels > 0 ? Math.Max(0f, (radiusPixels - strokeWidth) / radiusPixels) : 0f;

        ReadOnlySpan<float> vertices =
        [
            x0, y0, -1f, -1f,
            x1, y0,  1f, -1f,
            x1, y1,  1f,  1f,
            x0, y0, -1f, -1f,
            x1, y1,  1f,  1f,
            x0, y1, -1f,  1f
        ];

        SetColor(strokeColor);
        _pushConstants[20] = innerRadius; // ring mode
        var offset = Surface.WriteVertices(vertices);
        if (offset == uint.MaxValue) return;

        BindPipeline(_pipelines.EllipsePipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize,
        DIR.Lib.RGBAColor32 fontColor, in RectInt layout, TextAlign horizAlignment = TextAlign.Center,
        TextAlign vertAlignment = TextAlign.Near)
    {
        if (_pipelines is null || _sdfFontAtlas is null || text.IsEmpty)
            return;

        // Split on '\n' without materializing a string or string[] — DrawText runs every
        // frame for every UI label, so this path must stay allocation-free.
        var lineCount = text.Count('\n') + 1;

        var glyphScale = VkSdfFontAtlas.GetGlyphScale(fontSize);
        var lineHeight = fontSize * 1.3f;
        var totalHeight = lineCount * lineHeight;

        var layoutX = (float)layout.UpperLeft.X;
        var layoutY = (float)layout.UpperLeft.Y;
        var layoutW = (float)layout.Width;
        var layoutH = (float)layout.Height;

        var startY = vertAlignment switch
        {
            TextAlign.Center => layoutY + (layoutH - totalHeight) / 2f,
            TextAlign.Far => layoutY + layoutH - totalHeight,
            _ => layoutY
        };

        // Open the SDF batch up front. Begin/EndGlyphBatch handles pipeline bind +
        // descriptor set + push constants + vkCmdDraw -- one set of commands per batch
        // run instead of one per glyph. Emoji (color) glyphs interrupt the SDF run and
        // switch to a bitmap batch; switching back reopens an SDF batch.
        BeginSdfGlyphBatch(fontColor, fontSize);
        var inSdfBatch = true;

        var remaining = text;
        for (var lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            var nl = remaining.IndexOf('\n');
            var line = nl < 0 ? remaining : remaining[..nl];
            if (nl >= 0) remaining = remaining[(nl + 1)..];
            if (line.IsEmpty) continue;

            // Shape the line once; both passes below iterate the shaped run. AdvanceShaper yields
            // one glyph per rune in input order (byte-identical to the old EnumerateRunes loops),
            // sourcing base metrics from the atlas as before; a real shaper substitutes/kerns. The
            // shaper contributes only XAdvanceAdjust (kern/GPOS advance) + X/YOffset, all zero by
            // default. Color-glyph routing still keys off the source codepoint (sg.Source), not id.
            TextShaper.Shape(line, fontFamily, fontSize, _sdfFontAtlas.Rasterizer, _shapedLine);

            // Compute visual text metrics (scaled from SDF raster size to display size)
            var advanceSum = 0f;
            var firstBearingX = 0f;
            var lastRightEdge = 0f;
            var maxAscent = 0f;
            var maxDescent = 0f;
            var first = true;
            foreach (var sg in _shapedLine)
            {
                var mc = sg.Source;
                // Use bitmap atlas metrics for color glyphs (emoji)
                var isEmoji = mc.Value >= 0x1F000
                    || (mc.Value >= 0x2600 && mc.Value <= 0x27BF)
                    || (mc.Value >= 0xFE00 && mc.Value <= 0xFE0F)
                    || mc.Value == 0x200D;
                float scaledBearingX, scaledBearingY, scaledWidth, scaledHeight, scaledAdvance;
                if (isEmoji && _fontAtlas is not null)
                {
                    var bg = _fontAtlas.GetGlyph(fontFamily, fontSize, mc);
                    var bScale = VkFontAtlas.GetGlyphScale(fontSize);
                    scaledBearingX = bg.BearingX * bScale;
                    scaledBearingY = bg.BearingY * bScale;
                    scaledWidth = bg.Width * bScale;
                    scaledHeight = bg.Height * bScale;
                    scaledAdvance = bg.AdvanceX * bScale;
                }
                else
                {
                    var g = _sdfFontAtlas.GetGlyph(fontFamily, fontSize, mc);
                    scaledBearingX = g.BearingX * glyphScale;
                    scaledBearingY = g.BearingY * glyphScale;
                    scaledWidth = g.Width * glyphScale;
                    scaledHeight = g.Height * glyphScale;
                    scaledAdvance = g.AdvanceX * glyphScale;
                }
                if (first && scaledWidth > 0) { firstBearingX = scaledBearingX; first = false; }
                if (scaledWidth > 0) { lastRightEdge = advanceSum + sg.XOffset + scaledBearingX + scaledWidth; }
                if (scaledBearingY > maxAscent) maxAscent = scaledBearingY;
                var descent = scaledHeight - scaledBearingY;
                if (descent > maxDescent) maxDescent = descent;
                advanceSum += scaledAdvance + sg.XAdvanceAdjust;
            }
            var visualWidth = first ? advanceSum : lastRightEdge - firstBearingX;

            var penX = horizAlignment switch
            {
                TextAlign.Center => layoutX + (layoutW - visualWidth) / 2f - firstBearingX,
                TextAlign.Far => layoutX + layoutW - visualWidth - firstBearingX,
                _ => layoutX
            };
            var penY = startY + lineIdx * lineHeight;

            var baseline = penY + (lineHeight + maxAscent - maxDescent) / 2f;

            foreach (var sg in _shapedLine)
            {
                var ch = sg.Source;
                // Color glyphs (emoji, symbols) can't render through the single-channel
                // SDF atlas. Fall back to the RGBA bitmap atlas + TexturedPipeline.
                var isColorGlyph = ch.Value >= 0x1F000 // Supplementary symbols & emoji
                    || (ch.Value >= 0x2600 && ch.Value <= 0x27BF) // Misc symbols, Dingbats
                    || (ch.Value >= 0xFE00 && ch.Value <= 0xFE0F) // Variation selectors
                    || (ch.Value >= 0x200D && ch.Value <= 0x200D); // ZWJ

                if (isColorGlyph && _fontAtlas is not null)
                {
                    if (inSdfBatch)
                    {
                        EndGlyphBatch();
                        BeginGlyphBatch(fontColor);
                        inSdfBatch = false;
                    }

                    var bitmapGlyph = _fontAtlas.GetGlyph(fontFamily, fontSize, ch, skipUnflushed: true);
                    var bScale = VkFontAtlas.GetGlyphScale(fontSize);
                    if (bitmapGlyph.Width > 0)
                    {
                        var bgx0 = penX + bitmapGlyph.BearingX * bScale + sg.XOffset;
                        var bgy0 = baseline - bitmapGlyph.BearingY * bScale - sg.YOffset;
                        AddBatchedGlyph(in bitmapGlyph, fontSize, bgx0, bgy0);
                    }
                    penX += bitmapGlyph.AdvanceX * bScale + sg.XAdvanceAdjust;
                    continue;
                }

                // SDF path for regular text glyphs
                if (!inSdfBatch)
                {
                    EndGlyphBatch();
                    BeginSdfGlyphBatch(fontColor, fontSize);
                    inSdfBatch = true;
                }

                var glyph = _sdfFontAtlas.GetGlyph(fontFamily, fontSize, ch, skipUnflushed: true);
                if (glyph.Width > 0)
                {
                    // gx0/gy0 is the TEXTURE quad top-left (BearingX/Y already include the
                    // SDF spread). AddBatchedSdfGlyph expects INK top-left and internally
                    // shifts by -pad to get texture top-left; adding pad here converts.
                    var pad = glyph.Spread * glyphScale;
                    var inkX = penX + glyph.BearingX * glyphScale + pad + sg.XOffset;
                    var inkY = baseline - glyph.BearingY * glyphScale + pad - sg.YOffset;
                    AddBatchedSdfGlyph(in glyph, inkX, inkY);
                }
                penX += glyph.AdvanceX * glyphScale + sg.XAdvanceAdjust;
            }
        }

        // Flush the final batch (SDF or bitmap, whichever was last).
        EndGlyphBatch();
    }

    public override void Dispose()
    {
        _sdfFontAtlas?.Dispose();
        _sdfFontAtlas = null;
        _fontAtlas?.Dispose();
        _fontAtlas = null;
        _pipelines?.Dispose();
        _pipelines = null;
    }

    private void UpdateProjection()
    {
        var w = (float)_width;
        var h = (float)_height;

        Array.Clear(_pushConstants, 0, 16);
        _pushConstants[0] = 2f / w;      // m00
        _pushConstants[5] = 2f / h;      // m11 (Vulkan Y already points down)
        _pushConstants[10] = -1f;        // m22
        _pushConstants[12] = -1f;        // m30
        _pushConstants[13] = -1f;        // m31
        _pushConstants[15] = 1f;         // m33
    }

    private void SetColor(DIR.Lib.RGBAColor32 color)
    {
        _pushConstants[16] = color.Red / 255f;
        _pushConstants[17] = color.Green / 255f;
        _pushConstants[18] = color.Blue / 255f;
        _pushConstants[19] = color.Alpha / 255f;
    }
}
