using System.Globalization;
using System.Text.Json;
using SdlVulkan.Renderer.Inspector;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The sidecar's request wire format, built by hand since a reflective serializer throws under trimming or
/// AOT. That shape is otherwise only observable against a live app, so it is pinned here: every request must
/// be valid JSON whose values round-trip to what the caller passed.
/// </summary>
public class InspectorJsonTests
{
    [Fact]
    public void Obj_ProducesValidJsonForEveryParameterShapeTheToolsUse()
    {
        using var args = JsonDocument.Parse("""{"a":1,"b":[2,3]}""");

        var json = Json.Obj(
            ("x", 12.5f), ("y", -3f), ("steps", 8), ("seconds", 1.25),
            ("mods", "CtrlShift"), ("label", "Start \"now\""), ("args", args.RootElement), ("nothing", null));

        using var doc = JsonDocument.Parse(json);   // throws if the hand-built string is malformed
        var r = doc.RootElement;

        r.GetProperty("x").GetSingle().ShouldBe(12.5f);
        r.GetProperty("y").GetSingle().ShouldBe(-3f);
        r.GetProperty("steps").GetInt32().ShouldBe(8);
        r.GetProperty("seconds").GetDouble().ShouldBe(1.25);
        r.GetProperty("mods").GetString().ShouldBe("CtrlShift");
        r.GetProperty("label").GetString().ShouldBe("Start \"now\"", "quotes must survive escaping");
        r.GetProperty("args").GetProperty("b")[1].GetInt32().ShouldBe(3, "a raw element is embedded, not re-quoted");
        r.GetProperty("nothing").ValueKind.ShouldBe(JsonValueKind.Null,
            "nulls are written, not omitted, so an app's GetProperty keeps working");
    }

    /// <summary>
    /// A decimal comma would split one number into two JSON values. This is the failure that only shows up on
    /// someone else's machine, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void Obj_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var json = Json.Obj(("x", 1.5f), ("seconds", 2.75));

            json.ShouldContain("1.5");
            json.ShouldNotContain("1,5");
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("seconds").GetDouble().ShouldBe(2.75);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Quote_EscapesControlCharacters()
    {
        // Verbatim strings, so what is being escaped stays legible: input holds a real newline, tab,
        // backslash and quote; output must hold their two-character JSON escapes.
        Json.Quote("a\nb\tc\\d\"e").ShouldBe(@"""a\nb\tc\\d\""e""");
        Json.Quote(null).ShouldBe("null");
    }
}
