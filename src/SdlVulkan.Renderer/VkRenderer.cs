using DIR.Lib;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

public sealed unsafe class VkRenderer : Renderer<VulkanContext>
{
    private VkPipelineSet? _pipelines;
    private VkFontAtlas? _fontAtlas;
    private uint _width;
    private uint _height;
    private VkCommandBuffer _currentCmd;

    // Push constant data: mat4 (16 floats) + vec4 color (4 floats) + float innerRadius (1 float) = 84 bytes
    private readonly float[] _pushConstants = new float[21];

    public VkRenderer(VulkanContext ctx, uint width, uint height) : base(ctx)
    {
        _width = width;
        _height = height;
        _pipelines = VkPipelineSet.Create(ctx);
        _fontAtlas = new VkFontAtlas(ctx);
        UpdateProjection();
    }

    public override uint Width => _width;
    public override uint Height => _height;

    internal VkFontAtlas? FontAtlas => _fontAtlas;
    public FreeTypeGlyphRasterizer? GlyphRasterizer => _fontAtlas?.Rasterizer;
    public bool FontAtlasDirty => _fontAtlas?.IsDirty == true;

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
        if (_fontAtlas is null || text.IsEmpty)
            return (0f, 0f);

        var width = 0f;
        var maxAscent = 0;
        var maxDescent = 0;
        foreach (var ch in text.EnumerateRunes())
        {
            var glyph = _fontAtlas.GetGlyph(fontFamily, fontSize, ch);
            width += glyph.AdvanceX;
            if (glyph.BearingY > maxAscent) maxAscent = glyph.BearingY;
            var descent = glyph.Height - glyph.BearingY;
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
        OnPreFlush?.Invoke();
        _fontAtlas?.Flush(_currentCmd);

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

    public override void Resize(uint width, uint height)
    {
        _width = width;
        _height = height;
        Surface.RecreateSwapchain(width, height);
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
    /// Draws a textured quad using the PagePipeline (pass-through, no glyph color detection).
    /// The texture must have its own VkDescriptorSet from VkTexture.
    /// </summary>
    public void DrawTexture(VkDescriptorSet textureSet, float x, float y, float w, float h)
    {
        DrawTextureRegion(textureSet, x, y, w, h, 0f, 0f, 1f, 1f);
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
    public void PreWarmGlyph(string fontPath, float fontSize, System.Text.Rune character, int charCode = -1)
    {
        _fontAtlas?.GetGlyph(fontPath, fontSize, character, charCode: charCode);
    }

    /// <summary>
    /// Draws a single glyph at the exact ink position.
    /// Supports CID subset fonts via charCode for glyph index lookup.
    /// </summary>
    public void DrawSingleGlyph(string fontPath, float fontSize, System.Text.Rune character,
        int charCode, DIR.Lib.RGBAColor32 color, float inkX, float inkY,
        float rotation = 0f)
    {
        if (_pipelines is null || _fontAtlas is null) return;

        var glyph = _fontAtlas.GetGlyph(fontPath, fontSize, character, skipUnflushed: true, charCode: charCode);
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
    /// Creates a persistent vertex buffer. The buffer lives until explicitly destroyed.
    /// </summary>
    public (Vortice.Vulkan.VkBuffer Buffer, Vortice.Vulkan.VkDeviceMemory Memory) CreatePersistentVertexBuffer(ReadOnlySpan<float> data)
        => Surface.CreatePersistentVertexBuffer(data);

    public void DestroyBuffer(Vortice.Vulkan.VkBuffer buffer, Vortice.Vulkan.VkDeviceMemory memory)
        => Surface.DestroyBuffer(buffer, memory);

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
        if (_pipelines is null || _fontAtlas is null || text.IsEmpty)
            return;

        var api = Surface.DeviceApi;
        var textStr = text.ToString();
        var lines = textStr.Split('\n');

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

        SetColor(fontColor);

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.TexturedPipeline);

        var descriptorSet = Surface.DescriptorSet;
        api.vkCmdBindDescriptorSets(_currentCmd, VkPipelineBindPoint.Graphics,
            Surface.PipelineLayout, 0, 1, &descriptorSet, 0, null);

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            if (string.IsNullOrEmpty(line)) continue;

            // Compute visual text metrics
            var advanceSum = 0f;
            var firstBearingX = 0;
            var lastRightEdge = 0f;
            var maxAscent = 0;  // max BearingY (above baseline)
            var maxDescent = 0; // max (Height - BearingY) (below baseline)
            var first = true;
            foreach (var mc in line.EnumerateRunes())
            {
                var g = _fontAtlas.GetGlyph(fontFamily, fontSize, mc);
                if (first && g.Width > 0) { firstBearingX = g.BearingX; first = false; }
                if (g.Width > 0) { lastRightEdge = advanceSum + g.BearingX + g.Width; }
                if (g.BearingY > maxAscent) maxAscent = g.BearingY;
                var descent = g.Height - g.BearingY;
                if (descent > maxDescent) maxDescent = descent;
                advanceSum += g.AdvanceX;
            }
            var visualWidth = first ? advanceSum : lastRightEdge - firstBearingX;

            var penX = horizAlignment switch
            {
                TextAlign.Center => layoutX + (layoutW - visualWidth) / 2f - firstBearingX,
                TextAlign.Far => layoutX + layoutW - visualWidth - firstBearingX,
                _ => layoutX
            };
            var penY = startY + lineIdx * lineHeight;

            // Place baseline so the visual bounds (ascent + descent) are centered in the line
            var baseline = penY + (lineHeight + maxAscent - maxDescent) / 2f;

            foreach (var ch in line.EnumerateRunes())
            {
                // skipUnflushed: true — skip quads for glyphs not yet uploaded to GPU
                var glyph = _fontAtlas.GetGlyph(fontFamily, fontSize, ch, skipUnflushed: true);
                if (glyph.Width == 0)
                {
                    penX += glyph.AdvanceX;
                    continue;
                }

                var gx0 = penX + glyph.BearingX;
                var gy0 = baseline - glyph.BearingY;
                var gx1 = gx0 + glyph.Width;
                var gy1 = gy0 + glyph.Height;

                ReadOnlySpan<float> vertices =
                [
                    gx0, gy0, glyph.U0, glyph.V0,
                    gx1, gy0, glyph.U1, glyph.V0,
                    gx1, gy1, glyph.U1, glyph.V1,
                    gx0, gy0, glyph.U0, glyph.V0,
                    gx1, gy1, glyph.U1, glyph.V1,
                    gx0, gy1, glyph.U0, glyph.V1
                ];

                var vertOffset = Surface.WriteVertices(vertices);
                if (vertOffset == uint.MaxValue) break;

                fixed (float* pPC = _pushConstants)
                    api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                        VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 84, pPC);

                var buffer = Surface.VertexBuffer;
                var vkOffset = (ulong)vertOffset;
                api.vkCmdBindVertexBuffers(_currentCmd, 0, 1, &buffer, &vkOffset);
                api.vkCmdDraw(_currentCmd, 6, 1, 0, 0);

                penX += glyph.AdvanceX;
            }
        }
    }

    public override void Dispose()
    {
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
