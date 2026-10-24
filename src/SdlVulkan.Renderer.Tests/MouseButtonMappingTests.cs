using System.Collections.Generic;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// How SDL's mouse button numbers become DIR.Lib's <see cref="MouseButton"/>. SDL numbers left, middle,
/// right, X1 (back), X2 (forward) as 1..5; the side buttons used to fall through to Left, so a
/// back/forward click acted as a left click. No GPU is needed: the view's dispatch only reads the callbacks.
/// </summary>
public class MouseButtonMappingTests
{
    [Theory]
    [InlineData((byte)1, MouseButton.Left)]
    [InlineData((byte)2, MouseButton.Middle)]
    [InlineData((byte)3, MouseButton.Right)]
    [InlineData((byte)4, MouseButton.Back)]
    [InlineData((byte)5, MouseButton.Forward)]
    public void ToMouseButton_MapsSdlButtonNumbers(byte sdlButton, MouseButton expected)
        => SdlWindowView.ToMouseButton(sdlButton).ShouldBe(expected);

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)6)]
    [InlineData((byte)255)]
    public void ToMouseButton_UnknownButtonFallsBackToLeft(byte sdlButton)
        => SdlWindowView.ToMouseButton(sdlButton).ShouldBe(MouseButton.Left);

    [Theory]
    [InlineData((byte)1, MouseButton.Left)]
    [InlineData((byte)2, MouseButton.Middle)]
    [InlineData((byte)3, MouseButton.Right)]
    [InlineData((byte)4, MouseButton.Back)]
    [InlineData((byte)5, MouseButton.Forward)]
    public void DispatchPointer_DeliversPressAndReleaseWithMappedButton(byte sdlButton, MouseButton expected)
    {
        // The primary constructor stores the window and renderer without touching them, and the pointer
        // dispatch never reads them, so a view needs no live window here.
        var view = new SdlWindowView(null!, null!);
        var seen = new List<InputEvent>();
        view.OnPointerInput = evt => { seen.Add(evt); return true; };

        view.DispatchPointerDown(sdlButton, 10f, 20f, 1, InputModifier.None);
        view.DispatchPointerUp(sdlButton, 11f, 21f);

        seen.Count.ShouldBe(2);
        var down = seen[0].ShouldBeOfType<InputEvent.MouseDown>();
        down.Button.ShouldBe(expected);
        down.X.ShouldBe(10f);
        down.Y.ShouldBe(20f);
        var up = seen[1].ShouldBeOfType<InputEvent.MouseUp>();
        up.Button.ShouldBe(expected);
        up.X.ShouldBe(11f);
        up.Y.ShouldBe(21f);
    }

    [Fact]
    public void DispatchPointer_LegacyCallbacksStillGetTheRawSdlButton()
    {
        var view = new SdlWindowView(null!, null!);
        byte down = 0, up = 0;
        view.OnMouseDown = (b, _, _, _, _) => { down = b; return false; };
        view.OnMouseUp = b => up = b;

        view.DispatchPointerDown(4, 0f, 0f, 1, InputModifier.None);
        view.DispatchPointerUp(5, 0f, 0f);

        down.ShouldBe((byte)4);
        up.ShouldBe((byte)5);
    }
}
