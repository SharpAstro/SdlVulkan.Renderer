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

    public VkRenderer(VulkanContext ctx, uint width, uint height) : base(ctx)
    {
        _width = width;
        _height = height;
        _pipelines = VkPipelineSet.Create(ctx);
        _fontAtlas = new VkFontAtlas(ctx);
        _sdfFontAtlas = new VkSdfFontAtlas(ctx, _fontAtlas.Rasterizer);
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
    public VkCommandBuffer CurrentCommandBuffer => _currentCmd;

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
        var width = 0f;
        var maxAscent = 0f;
        var maxDescent = 0f;
        foreach (var ch in text.EnumerateRunes())
        {
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
            width += advance;
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
        return true;
    }

    /// <summary>
    /// Ends the current frame and presents.
    /// </summary>
    public void EndFrame()
    {
        Surface.EndFrame(_currentCmd);
    }

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
    /// Drop the current frame's command buffer reference and ask the underlying context to
    /// rebuild its sync objects and swapchain. Use from an outer try/catch when a Vulkan call
    /// (typically vkQueueSubmit/Present) throws mid-frame, so the next frame can start clean
    /// instead of hanging on a stuck fence from the failed submit.
    /// </summary>
    public void RecoverFromGpuError()
    {
        _currentCmd = VkCommandBuffer.Null;
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.FlatPipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)offset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
        api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);
    }

    public override void FillRectangles(ReadOnlySpan<(RectInt Rect, DIR.Lib.RGBAColor32 Color)> rectangles)
    {
        foreach (var (rect, color) in rectangles)
            FillRectangle(rect, color);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.FlatPipeline);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.FlatPipeline);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, pipelineOverride ?? _pipelines.FlatPipeline);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.StrokePipeline);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.StrokePipeline);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.PagePipeline);

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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.PagePipeline);

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
    /// </summary>
    public void DrawSingleGlyph(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, DIR.Lib.RGBAColor32 color, float inkX, float inkY,
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto)
    {
        if (_pipelines is null || _fontAtlas is null) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        var api = Surface.DeviceApi;

        // Scale glyph metrics when rasterized at a capped size (e.g., 128px max)
        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        var w = glyph.Width * glyphScale;
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.TexturedPipeline);

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
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto)
    {
        if (_pipelines is null || _fontAtlas is null) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        var inkX = baselineX + glyph.BearingX * glyphScale;
        var inkY = baselineY - glyph.BearingY * glyphScale;

        DrawSingleGlyph(fontPath, fontSize, character, charCode, color, inkX, inkY, rotation, hint);
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
        SetColor(color);
    }

    /// <summary>
    /// Adds a glyph to the current batch at the exact ink position (no draw call issued).
    /// </summary>
    public void AddBatchedGlyph(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, float inkX, float inkY,
        float rotation = 0f, DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto)
    {
        if (_pipelines is null || _fontAtlas is null || !_glyphBatchActive) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        AddBatchedGlyph(glyph, fontSize, inkX, inkY, rotation);
    }

    /// <summary>
    /// Assembly-internal overload that accepts a pre-resolved <see cref="VkFontAtlas.GlyphInfo"/>
    /// directly — skips the atlas dictionary lookup that the <c>fontPath</c> overload performs
    /// internally. Used by <see cref="DrawText"/> which already holds the <c>GlyphInfo</c> from
    /// its metrics pass and would otherwise pay for a second lookup. Passed by <c>in</c> to
    /// avoid copying the ~36-byte record struct on the per-glyph hot path.
    /// </summary>
    internal void AddBatchedGlyph(in VkFontAtlas.GlyphInfo glyph, float fontSize,
        float inkX, float inkY, float rotation = 0f)
    {
        if (_pipelines is null || _fontAtlas is null || !_glyphBatchActive) return;
        if (glyph.Width == 0) return;

        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        var w = glyph.Width * glyphScale;
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
        bool xIsInkLeft = false)
    {
        if (_pipelines is null || _fontAtlas is null || !_glyphBatchActive) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        var glyphScale = VkFontAtlas.GetGlyphScale(fontSize);
        // Skip the LSB shift when the caller already has ink-left — otherwise narrow glyphs
        // in monospace fonts (e.g. 'S' or 'i' in Courier) end up shifted right by their LSB,
        // opening a visible gap between them and the next glyph.
        var bx = xIsInkLeft ? 0f : glyph.BearingX * glyphScale;
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

        AddBatchedGlyph(fontPath, fontSize, character, charCode, inkX, inkY, rotation, hint);
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
        DIR.Lib.GlyphMapHint hint = DIR.Lib.GlyphMapHint.Auto)
    {
        if (_pipelines is null || _sdfFontAtlas is null || !_glyphBatchActive || !_glyphBatchIsSdf) return;

        var glyph = _sdfFontAtlas.GetGlyph(fontPath, _glyphBatchFontSize, character,
            skipUnflushed: true, charCode: charCode, hint: hint);
        AddBatchedSdfGlyph(glyph, inkX, inkY, rotation);
    }

    /// <summary>
    /// Assembly-internal overload that accepts a pre-resolved <see cref="VkSdfFontAtlas.GlyphInfo"/>
    /// directly — skips the atlas dictionary lookup that the <c>fontPath</c> overload performs
    /// internally. Uses the current batch's <c>fontSize</c> (set by <see cref="BeginSdfGlyphBatch"/>)
    /// to compute scale and spread padding. Passed by <c>in</c> to avoid copying the ~40-byte
    /// record struct on the per-glyph hot path.
    /// </summary>
    internal void AddBatchedSdfGlyph(in VkSdfFontAtlas.GlyphInfo glyph, float inkX, float inkY, float rotation = 0f)
    {
        if (_pipelines is null || _sdfFontAtlas is null || !_glyphBatchActive || !_glyphBatchIsSdf) return;
        if (glyph.Width == 0) return;

        var glyphScale = VkSdfFontAtlas.GetGlyphScale(_glyphBatchFontSize);
        var w = glyph.Width * glyphScale;   // SDF texture width (ink + 2*spread)
        var h = glyph.Height * glyphScale;  // SDF texture height (ink + 2*spread)
        var pad = glyph.Spread * glyphScale;

        Span<float> verts = stackalloc float[24];
        if (MathF.Abs(rotation) < 0.01f)
        {
            // Quad top-left = ink top-left shifted by (-pad, -pad) so the ink
            // inside the SDF texture lands at (inkX, inkY).
            var x0 = inkX - pad;
            var y0 = inkY - pad;
            var x1 = x0 + w;
            var y1 = y0 + h;
            verts[0] = x0; verts[1] = y0; verts[2] = glyph.U0; verts[3] = glyph.V0;
            verts[4] = x1; verts[5] = y0; verts[6] = glyph.U1; verts[7] = glyph.V0;
            verts[8] = x1; verts[9] = y1; verts[10] = glyph.U1; verts[11] = glyph.V1;
            verts[12] = x0; verts[13] = y0; verts[14] = glyph.U0; verts[15] = glyph.V0;
            verts[16] = x1; verts[17] = y1; verts[18] = glyph.U1; verts[19] = glyph.V1;
            verts[20] = x0; verts[21] = y1; verts[22] = glyph.U0; verts[23] = glyph.V1;
        }
        else
        {
            // Rotation basis: right = (cosA, sinA) for local +x, down = (-sinA, cosA) for local +y.
            // Texture top-left in rotated screen space = ink top-left shifted by -pad along both
            // local axes — otherwise the ink inside the texture would land `pad` pixels off along
            // the rotated direction.
            var cosA = MathF.Cos(rotation);
            var sinA = MathF.Sin(rotation);
            var rxU = cosA;  var ryU = sinA;   // right unit
            var dxU = -sinA; var dyU = cosA;   // down unit
            var tlx = inkX - pad * rxU - pad * dxU;
            var tly = inkY - pad * ryU - pad * dyU;
            var wrx = w * rxU; var wry = w * ryU;       // "right" scaled by texture width
            var hdx = h * dxU; var hdy = h * dyU;       // "down"  scaled by texture height
            var trx = tlx + wrx;        var try_ = tly + wry;
            var blx = tlx + hdx;        var bly  = tly + hdy;
            var brx = tlx + wrx + hdx;  var bry  = tly + wry + hdy;

            verts[0] = tlx; verts[1] = tly; verts[2] = glyph.U0; verts[3] = glyph.V0;
            verts[4] = trx; verts[5] = try_; verts[6] = glyph.U1; verts[7] = glyph.V0;
            verts[8] = brx; verts[9] = bry;  verts[10] = glyph.U1; verts[11] = glyph.V1;
            verts[12] = tlx; verts[13] = tly; verts[14] = glyph.U0; verts[15] = glyph.V0;
            verts[16] = brx; verts[17] = bry; verts[18] = glyph.U1; verts[19] = glyph.V1;
            verts[20] = blx; verts[21] = bly; verts[22] = glyph.U0; verts[23] = glyph.V1;
        }
        ReadOnlySpan<float> vertices = verts;

        var vertOffset = Surface.WriteVertices(vertices);
        if (vertOffset == uint.MaxValue) return;

        if (_glyphBatchStartOffset == uint.MaxValue)
            _glyphBatchStartOffset = vertOffset;
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
        bool xIsInkLeft = false)
    {
        if (_pipelines is null || _sdfFontAtlas is null || !_glyphBatchActive || !_glyphBatchIsSdf) return;

        var glyph = _sdfFontAtlas.GetGlyph(fontPath, _glyphBatchFontSize, character,
            skipUnflushed: true, charCode: charCode, hint: hint);
        if (glyph.Width == 0) return;

        // BearingX/BearingY on the SDF atlas are to the SDF TEXTURE edges (inc. spread padding).
        // Convert to INK bearings so we can pass ink-top-left to AddBatchedSdfGlyph:
        //   ink_bearing_X = texture_bearing_X + spread (ink is spread pixels right of texture left)
        //   ink_bearing_Y = texture_bearing_Y - spread (ink is spread pixels below texture top)
        // When the caller already has ink-left (xIsInkLeft), skip the horizontal LSB add —
        // otherwise monospace narrow glyphs (pdfium's GetCharQuad returns ink boxes, not pen
        // positions) drift right by their own LSB, producing visible gaps in the rendered run.
        var glyphScale = VkSdfFontAtlas.GetGlyphScale(_glyphBatchFontSize);
        var bx = xIsInkLeft ? 0f : (glyph.BearingX + glyph.Spread) * glyphScale;
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

        AddBatchedSdfGlyph(fontPath, character, charCode, inkX, inkY, rotation, hint);
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

        if (_glyphBatchVertexCount == 0 || _glyphBatchStartOffset == uint.MaxValue) return;

        var api = Surface.DeviceApi;
        Vortice.Vulkan.VkPipeline pipeline;
        Vortice.Vulkan.VkDescriptorSet descriptorSet;
        if (isSdf && _sdfFontAtlas is not null)
        {
            pipeline = _pipelines!.SdfPipeline;
            descriptorSet = _sdfFontAtlas.DescriptorSet;
            // SDF AA is driven by fwidth() in the fragment shader, so no caller-side
            // edge softness is needed regardless of fontSize. Zero the slot for cleanliness.
            _pushConstants[20] = 0f;
        }
        else
        {
            pipeline = _pipelines!.TexturedPipeline;
            descriptorSet = Surface.DescriptorSet;
            _pushConstants[20] = 0f; // unused by TexturedPipeline, keep push constants clean
        }

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, pipeline);
        api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
            Surface.PipelineLayout, 0, 1, &descriptorSet, 0, null);

        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

        var buffer = Surface.VertexBuffer;
        var vkOffset = (ulong)_glyphBatchStartOffset;
        api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
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
        var x0 = (float)rect.UpperLeft.X;
        var y0 = (float)rect.UpperLeft.Y;
        var x1 = (float)rect.LowerRight.X;
        var y1 = (float)rect.LowerRight.Y;
        var sw = (float)strokeWidth;

        FillRectangle(new RectInt((rect.LowerRight.X, (int)(y0 + sw)), (rect.UpperLeft.X, rect.UpperLeft.Y)), strokeColor);
        FillRectangle(new RectInt((rect.LowerRight.X, rect.LowerRight.Y), ((int)x0, (int)(y1 - sw))), strokeColor);
        FillRectangle(new RectInt(((int)(x0 + sw), (int)(y1 - sw)), (rect.UpperLeft.X, (int)(y0 + sw))), strokeColor);
        FillRectangle(new RectInt((rect.LowerRight.X, (int)(y1 - sw)), ((int)(x1 - sw), (int)(y0 + sw))), strokeColor);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.EllipsePipeline);
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.EllipsePipeline);
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

        var api = Surface.DeviceApi;
        var textStr = text.ToString();
        var lines = textStr.Split('\n');

        var glyphScale = VkSdfFontAtlas.GetGlyphScale(fontSize);
        var lineHeight = fontSize * 1.3f;
        var totalHeight = lines.Length * lineHeight;

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

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            if (string.IsNullOrEmpty(line)) continue;

            // Compute visual text metrics (scaled from SDF raster size to display size)
            var advanceSum = 0f;
            var firstBearingX = 0f;
            var lastRightEdge = 0f;
            var maxAscent = 0f;
            var maxDescent = 0f;
            var first = true;
            foreach (var mc in line.EnumerateRunes())
            {
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
                if (scaledWidth > 0) { lastRightEdge = advanceSum + scaledBearingX + scaledWidth; }
                if (scaledBearingY > maxAscent) maxAscent = scaledBearingY;
                var descent = scaledHeight - scaledBearingY;
                if (descent > maxDescent) maxDescent = descent;
                advanceSum += scaledAdvance;
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

            foreach (var ch in line.EnumerateRunes())
            {
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
                        var bgx0 = penX + bitmapGlyph.BearingX * bScale;
                        var bgy0 = baseline - bitmapGlyph.BearingY * bScale;
                        AddBatchedGlyph(in bitmapGlyph, fontSize, bgx0, bgy0);
                    }
                    penX += bitmapGlyph.AdvanceX * bScale;
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
                    var inkX = penX + glyph.BearingX * glyphScale + pad;
                    var inkY = baseline - glyph.BearingY * glyphScale + pad;
                    AddBatchedSdfGlyph(in glyph, inkX, inkY);
                }
                penX += glyph.AdvanceX * glyphScale;
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
