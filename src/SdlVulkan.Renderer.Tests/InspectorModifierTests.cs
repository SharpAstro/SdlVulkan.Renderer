#if DEBUG
using System;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// How the inspector reads a driver's <c>mods</c> string. This decides what an injected key or click MEANS,
/// so a wrong answer here looks like the app misbehaving rather than like a bad request — which is why it is
/// tested directly rather than through a live window.
/// </summary>
public class InspectorModifierTests
{
    [Theory]
    [InlineData("Ctrl", InputModifier.Ctrl)]
    [InlineData("ctrl", InputModifier.Ctrl)]
    [InlineData("control", InputModifier.Ctrl)]
    [InlineData("Shift", InputModifier.Shift)]
    [InlineData("Alt", InputModifier.Alt)]
    [InlineData("option", InputModifier.Alt)]
    [InlineData("Ctrl+Shift", InputModifier.Ctrl | InputModifier.Shift)]
    [InlineData("CtrlShift", InputModifier.Ctrl | InputModifier.Shift)]
    [InlineData("control-alt", InputModifier.Ctrl | InputModifier.Alt)]
    public void ResolveModifier_AcceptsEverySpellingADriverPlausiblyUses(string raw, InputModifier expected)
        => DebugInspector.ResolveModifier(raw).ShouldBe(expected);

    /// <summary>Absent and explicitly-none are the two ways to ask for a bare key, and both must work.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]
    [InlineData("0")]
    public void ResolveModifier_TreatsAbsentAndNoneAsNoModifiers(string? raw)
        => DebugInspector.ResolveModifier(raw).ShouldBe(InputModifier.None);

    /// <summary>
    /// The behaviour change. This used to return <see cref="InputModifier.None"/>, which delivered a BARE
    /// key — and a bare key is frequently a different valid binding rather than a no-op, so the mistake
    /// surfaced as the app ignoring a correct chord. Chess is the worked example: Ctrl+F flips the board
    /// while bare <c>f</c> selects file f.
    /// </summary>
    [Theory]
    [InlineData("cmd")]
    [InlineData("super")]
    [InlineData("meta")]
    [InlineData("ctlr")]   // the transposition a human actually makes
    public void ResolveModifier_RefusesUnknownText_RatherThanSilentlyMeaningNone(string raw)
    {
        var ex = Should.Throw<ArgumentException>(() => DebugInspector.ResolveModifier(raw));

        ex.Message.ShouldContain(raw, Case.Insensitive, "the message must name what was rejected");
        ex.Message.ShouldContain("Ctrl", Case.Insensitive, "and list what is accepted");
    }

    /// <summary>
    /// A partial match still resolves. "ctrl+cmd" carries a modifier this inspector understands, so refusing
    /// it would block a chord that CAN be delivered; only text with nothing recognisable at all is refused.
    /// </summary>
    [Fact]
    public void ResolveModifier_KeepsWhatItUnderstandsFromAMixedString()
        => DebugInspector.ResolveModifier("ctrl+cmd").ShouldBe(InputModifier.Ctrl);

    /// <summary>
    /// The sibling resolver, asserted here because the two now behave alike: an unknown key was already
    /// refused, and that consistency is the point of the change.
    /// </summary>
    [Fact]
    public void ResolveInputKey_AlsoRefusesTheUnknown()
    {
        DebugInspector.ResolveInputKey("esc").ShouldBe(InputKey.Escape);
        DebugInspector.ResolveInputKey("4").ShouldBe(InputKey.D4);
        Should.Throw<ArgumentException>(() => DebugInspector.ResolveInputKey("nope"));
    }
}
#endif
