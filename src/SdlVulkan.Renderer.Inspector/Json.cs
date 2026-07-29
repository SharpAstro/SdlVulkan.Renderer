using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>
/// JSON writing by hand, for requests going out to an app's inspector.
///
/// <para><b>Why not <c>JsonSerializer.Serialize</c>.</b> It was serializing an anonymous type per request,
/// which is reflection-based: under trimming or AOT (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>)
/// it throws at runtime, and the trim analyser flags it as IL2026/IL3050. The in-process half of this
/// inspector already avoids the serializer for exactly that reason — see the comment beside
/// <c>DebugInspector</c>'s error path — and the same failure has since been observed for real in a sibling:
/// an AOT-configured app threw on a plain string and the symptom was a socket that closed the instant it was
/// written to, which looks nothing like a serialization fault.</para>
///
/// <para>A <c>dnx</c> tool is a plausible AOT candidate — startup time is the whole point of one — so the
/// driver side is worth making safe before, not after, someone turns AOT on.</para>
/// </summary>
internal static class Json
{
    /// <summary>A JSON string literal.</summary>
    public static string Quote(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// A JSON object from name/value pairs. Nulls are written as <c>null</c> rather than omitted, so the wire
    /// shape matches what the reflective serializer produced and no app-side <c>GetProperty</c> starts
    /// failing.
    /// </summary>
    public static string Obj(params (string Name, object? Value)[] fields)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(Quote(fields[i].Name)).Append(':').Append(Value(fields[i].Value));
        }
        return sb.Append('}').ToString();
    }

    /// <summary>
    /// One value. The accepted set is closed on purpose: an unsupported type throws HERE, at the call site
    /// that added it, rather than silently producing something the app cannot parse.
    /// </summary>
    private static string Value(object? value) => value switch
    {
        null => "null",
        string s => Quote(s),
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        // "R" round-trips, and InvariantCulture is what stops a comma decimal separator producing JSON that
        // parses as two values on a machine with a European locale.
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        JsonElement e => e.GetRawText(),
        _ => throw new NotSupportedException(
            $"{value.GetType().Name} is not a supported inspector parameter type; add it to Json.Value"),
    };
}
