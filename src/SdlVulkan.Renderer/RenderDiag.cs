using System.Diagnostics;
using Vortice.Vulkan;

namespace SdlVulkan.Renderer;

/// <summary>
/// Structured, DEBUG-only renderer diagnostics. Every method carries <c>[Conditional("DEBUG")]</c>,
/// so in a Release build of this assembly the C# compiler removes the call sites entirely — zero
/// runtime cost and no console output. In a Debug build each call emits one structured line to
/// stderr: <c>[rdiag] &lt;category&gt; &lt;detail&gt;</c>.
/// <para>
/// Categories use a dotted prefix (e.g. <c>sdf.grow</c>, <c>sdf.evict</c>, <c>sdf.full</c>,
/// <c>vk.submit</c>) so the output is easy to grep/filter. Keep each method's <c>detail</c> argument as
/// space-separated <c>key=value</c> pairs for the same reason.
/// </para>
/// </summary>
internal static class RenderDiag
{
    [Conditional("DEBUG")]
    public static void Log(string category, string detail) =>
        Console.Error.WriteLine($"[rdiag] {category} {detail}");

    /// <summary>Logs a Vulkan call's result, but only when it is not <see cref="VkResult.Success"/>.
    /// (Success is the common case and would just be noise.)</summary>
    [Conditional("DEBUG")]
    public static void Vk(string op, VkResult result, string detail = "")
    {
        if (result != VkResult.Success)
            Console.Error.WriteLine($"[rdiag] vk.{op} result={result} {detail}");
    }
}
