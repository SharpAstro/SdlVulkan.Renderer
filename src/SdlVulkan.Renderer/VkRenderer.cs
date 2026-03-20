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

    // Push constant data: mat4 (16 floats) + vec4 (4 floats) = 80 bytes
    private readonly float[] _pushConstants = new float[20];

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
    public bool FontAtlasDirty => _fontAtlas?.IsDirty == true;

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
        _fontAtlas?.Flush(_currentCmd);

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
        Console.Error.WriteLine($"[VkRenderer] Resize: {_width}x{_height} -> {width}x{height}");
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

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.FlatPipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 80, pPC);

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
        var offset = Surface.WriteVertices(vertices);

        api.vkCmdBindPipeline(_currentCmd, VkPipelineBindPoint.Graphics, _pipelines.EllipsePipeline);
        fixed (float* pPC = _pushConstants)
            api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 80, pPC);

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
            var visualHeight = maxAscent + maxDescent;

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
                var glyph = _fontAtlas.GetGlyph(fontFamily, fontSize, ch);
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

                fixed (float* pPC = _pushConstants)
                    api.vkCmdPushConstants(_currentCmd, Surface.PipelineLayout,
                        VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, 80, pPC);

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
