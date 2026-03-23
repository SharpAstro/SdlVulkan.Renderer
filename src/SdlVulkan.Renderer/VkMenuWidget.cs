using DIR.Lib;

namespace SdlVulkan.Renderer;

/// <summary>
/// A generic Vulkan menu widget that renders a title, prompt, and selectable items.
/// Handles arrow-key, digit-key, Enter, and mouse click navigation.
/// Implements <see cref="IWidget"/> for input routing.
/// <para>
/// This is the pixel-renderer counterpart to Console.Lib's <c>MenuBase&lt;T&gt;</c>.
/// </para>
/// </summary>
public class VkMenuWidget : IWidget
{
    private static readonly RGBAColor32 DefaultTitleColor = new(0xff, 0xce, 0x9e, 0xff);
    private static readonly RGBAColor32 DefaultPromptColor = new(0xdd, 0xdd, 0xdd, 0xff);
    private static readonly RGBAColor32 DefaultItemColor = new(0xcc, 0xcc, 0xcc, 0xff);
    private static readonly RGBAColor32 DefaultSelectedBg = new(0x30, 0x50, 0x90, 0xff);
    private static readonly RGBAColor32 DefaultSelectedFg = new(0xff, 0xd7, 0x00, 0xff);

    private readonly string _fontPath;
    private string _title;
    private string _prompt;
    private string[] _items;
    private int _selected;
    private uint _lastWidth;
    private uint _lastHeight;

    /// <summary>Color for the title text.</summary>
    public RGBAColor32 TitleColor { get; set; } = DefaultTitleColor;

    /// <summary>Color for the prompt text.</summary>
    public RGBAColor32 PromptColor { get; set; } = DefaultPromptColor;

    /// <summary>Color for unselected item text.</summary>
    public RGBAColor32 ItemColor { get; set; } = DefaultItemColor;

    /// <summary>Background color for the selected item row.</summary>
    public RGBAColor32 SelectedBackground { get; set; } = DefaultSelectedBg;

    /// <summary>Foreground color for the selected item text.</summary>
    public RGBAColor32 SelectedForeground { get; set; } = DefaultSelectedFg;

    /// <summary>The currently selected item index.</summary>
    public int SelectedIndex => _selected;

    /// <summary>True after the user has confirmed a selection (Enter, digit, or click).</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>
    /// Creates a new menu widget.
    /// </summary>
    /// <param name="fontPath">Absolute path to the TTF font to render with.</param>
    /// <param name="title">Title displayed at the top of the menu.</param>
    /// <param name="prompt">Prompt displayed below the title.</param>
    /// <param name="items">Selectable menu items.</param>
    public VkMenuWidget(string fontPath, string title, string prompt, string[] items)
    {
        _fontPath = fontPath;
        _title = title;
        _prompt = prompt;
        _items = items;
    }

    /// <summary>
    /// Resets the menu with new content, clearing the confirmed state.
    /// </summary>
    public void Reset(string title, string prompt, string[] items, int selected = 0)
    {
        _title = title;
        _prompt = prompt;
        _items = items;
        _selected = Math.Clamp(selected, 0, Math.Max(0, items.Length - 1));
        IsConfirmed = false;
    }

    /// <summary>
    /// Renders the menu using the given <see cref="VkRenderer"/>.
    /// Must be called between <see cref="VkRenderer.BeginFrame"/> and <see cref="VkRenderer.EndFrame"/>.
    /// </summary>
    public void Render(VkRenderer renderer)
    {
        _lastWidth = renderer.Width;
        _lastHeight = renderer.Height;

        var w = (float)_lastWidth;
        var h = (float)_lastHeight;
        var fontSize = MathF.Max(16f, h / 25f);
        var titleSize = fontSize * 1.6f;
        var lineH = fontSize * 2f;

        var totalH = titleSize * 2f + lineH + _items.Length * lineH;
        var startY = (h - totalH) / 2f;

        var titleRect = new RectInt(((int)w, (int)(startY + titleSize * 2f)), (0, (int)startY));
        renderer.DrawText(_title.AsSpan(), _fontPath, titleSize, TitleColor, titleRect, vertAlignment: TextAlign.Center);

        var promptY = startY + titleSize * 2f + lineH * 0.5f;
        var promptRect = new RectInt(((int)w, (int)(promptY + lineH)), (0, (int)promptY));
        renderer.DrawText(_prompt.AsSpan(), _fontPath, fontSize, PromptColor, promptRect, vertAlignment: TextAlign.Center);

        var itemsStartY = promptY + lineH * 1.5f;
        for (var i = 0; i < _items.Length; i++)
        {
            var itemY = itemsStartY + i * lineH;
            var itemRect = new RectInt(((int)w, (int)(itemY + lineH)), (0, (int)itemY));

            if (i == _selected)
            {
                var highlightPad = w * 0.2f;
                var bgRect = new RectInt(((int)(w - highlightPad), (int)(itemY + lineH)), ((int)highlightPad, (int)itemY));
                renderer.FillRectangle(bgRect, SelectedBackground);

                var label = $"\u25B6  {_items[i]}";
                renderer.DrawText(label.AsSpan(), _fontPath, fontSize, SelectedForeground, itemRect, vertAlignment: TextAlign.Center);
            }
            else
            {
                var label = $"   {_items[i]}";
                renderer.DrawText(label.AsSpan(), _fontPath, fontSize, ItemColor, itemRect, vertAlignment: TextAlign.Center);
            }
        }
    }

    /// <inheritdoc/>
    public bool HandleInput(InputEvent evt) => evt switch
    {
        InputEvent.KeyDown(var key, _) => HandleKeyDown(key),
        InputEvent.MouseDown(var x, var y, _, _, _) => HandleMouseDown(x, y),
        _ => false
    };

    /// <summary>Handles keyboard navigation. Returns true if consumed.</summary>
    public bool HandleKeyDown(InputKey key)
    {
        if (IsConfirmed || _items.Length == 0) return false;

        switch (key)
        {
            case InputKey.Up:
                _selected = (_selected - 1 + _items.Length) % _items.Length;
                return true;
            case InputKey.Down:
                _selected = (_selected + 1) % _items.Length;
                return true;
            case InputKey.Enter:
                IsConfirmed = true;
                return true;
            default:
                var digit = key switch
                {
                    InputKey.D1 => 0,
                    InputKey.D2 => 1,
                    InputKey.D3 => 2,
                    InputKey.D4 => 3,
                    InputKey.D5 => 4,
                    InputKey.D6 => 5,
                    InputKey.D7 => 6,
                    InputKey.D8 => 7,
                    InputKey.D9 => 8,
                    _ => -1
                };
                if (digit >= 0 && digit < _items.Length)
                {
                    _selected = digit;
                    IsConfirmed = true;
                    return true;
                }
                return false;
        }
    }

    /// <summary>Handles mouse click on menu items. Returns true if consumed.</summary>
    public bool HandleMouseDown(float x, float y)
    {
        if (IsConfirmed || _lastWidth == 0 || _items.Length == 0) return false;

        var h = (float)_lastHeight;
        var fontSize = MathF.Max(16f, h / 25f);
        var titleSize = fontSize * 1.6f;
        var lineH = fontSize * 2f;

        var totalH = titleSize * 2f + lineH + _items.Length * lineH;
        var startY = (h - totalH) / 2f;
        var promptY = startY + titleSize * 2f + lineH * 0.5f;
        var itemsStartY = promptY + lineH * 1.5f;

        for (var i = 0; i < _items.Length; i++)
        {
            var itemY = itemsStartY + i * lineH;
            if (y >= itemY && y < itemY + lineH)
            {
                _selected = i;
                IsConfirmed = true;
                return true;
            }
        }

        return false;
    }
}
