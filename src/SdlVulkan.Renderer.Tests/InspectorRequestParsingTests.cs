#if DEBUG
using System;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// How a request's parameters are read. Several verbs are reachable BOTH directly and as a batch step,
/// and the two spellings had drifted: a step following the batch contract either threw or, worse,
/// reported success and did nothing. Both failure modes are diagnosed from the far end of a socket,
/// where the only evidence is the reply, so the parsers accept either spelling and name what they
/// wanted when it is absent.
/// </summary>
public class InspectorRequestParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The direct text verb sends "s"; the batch contract advertises "text". Before this, a batch step
    /// that passed "text" threw KeyNotFoundException, whose message ("The given key was not present in
    /// the dictionary") reached the caller as the entire explanation.
    /// </summary>
    [Theory]
    [InlineData("{\"text\":\"NGC4565\"}")]
    [InlineData("{\"s\":\"NGC4565\"}")]
    public void RequiredString_AcceptsEitherSpelling(string json)
        => DebugInspector.RequiredString(Json(json), "text", "s").ShouldBe("NGC4565");

    [Fact]
    public void RequiredString_NamesWhatItWanted_RatherThanBlamingADictionary()
    {
        var ex = Should.Throw<ArgumentException>(
            () => DebugInspector.RequiredString(Json("{\"other\":1}"), "text", "s"));

        ex.Message.ShouldContain("text");
        ex.Message.ShouldContain("s");
    }

    [Fact]
    public void RequiredString_SingleName_SaysSoWithoutTheListWording()
    {
        var ex = Should.Throw<ArgumentException>(
            () => DebugInspector.RequiredString(Json("{}"), "label"));

        ex.Message.ShouldContain("label");
        ex.Message.ShouldNotContain("one of");
    }

    /// <summary>
    /// A wrong TYPE is as absent as a missing key: reading a number as a string would otherwise throw
    /// from inside System.Text.Json with no mention of the parameter.
    /// </summary>
    [Fact]
    public void RequiredString_RefusesANonString()
        => Should.Throw<ArgumentException>(() => DebugInspector.RequiredString(Json("{\"label\":7}"), "label"));

    /// <summary>
    /// postSignal read only "args", so a step passing "argsJson" (what the direct verb carries) posted
    /// the signal with NO arguments. Every field then fell back to its declared default, and for a
    /// signal whose fields all default to "leave unchanged" that is indistinguishable from working:
    /// the reply said queued and the app did nothing.
    /// </summary>
    [Fact]
    public void SignalArgs_ReadsTheObjectForm()
    {
        var args = DebugInspector.SignalArgs(Json("{\"name\":\"X\",\"args\":{\"fieldOfViewDeg\":40}}"));
        args.GetProperty("fieldOfViewDeg").GetDouble().ShouldBe(40.0);
    }

    [Fact]
    public void SignalArgs_ReadsTheJsonStringForm()
    {
        var args = DebugInspector.SignalArgs(Json("{\"name\":\"X\",\"argsJson\":\"{\\\"fieldOfViewDeg\\\":40}\"}"));
        args.GetProperty("fieldOfViewDeg").GetDouble().ShouldBe(40.0);
    }

    [Fact]
    public void SignalArgs_PrefersTheObjectWhenBothArePresent()
    {
        var args = DebugInspector.SignalArgs(
            Json("{\"args\":{\"fieldOfViewDeg\":10},\"argsJson\":\"{\\\"fieldOfViewDeg\\\":40}\"}"));
        args.GetProperty("fieldOfViewDeg").GetDouble().ShouldBe(10.0);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"args\":null}")]
    [InlineData("{\"argsJson\":\"\"}")]
    [InlineData("{\"argsJson\":\"   \"}")]
    public void SignalArgs_AbsentOrBlank_YieldsUndefined_SoTheSignalUsesItsOwnDefaults(string json)
        => DebugInspector.SignalArgs(Json(json)).ValueKind.ShouldBe(JsonValueKind.Undefined);
}
#endif
