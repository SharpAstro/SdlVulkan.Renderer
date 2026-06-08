using System.IO.Compression;
using System.Text.Json;
using SharpAstro.Png;

namespace SdlVulkan.Renderer.Inspector;

/// <summary>
/// Headless protocol self-test (invoked with the `selftest` argument). Discovers a running
/// instance and exercises ping -> describe -> screenshot end-to-end, printing a summary to stderr
/// and saving the screenshot PNG to the temp dir. Mirrors tools/WebViewSmoke's assert modes and
/// doubles as a smoke check that doesn't require an MCP client. Output goes to stderr/stdout (this
/// mode does not run the JSON-RPC server, so stdout is free to use).
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(InspectorDiscoveryClient discovery, InspectorSocketClient socket)
    {
        Console.Error.WriteLine("[selftest] discovering instances...");
        var all = await discovery.DiscoverAsync();
        if (all.Count == 0)
        {
            Console.Error.WriteLine("[selftest] FAIL: no debuggable instances found.");
            return 1;
        }
        foreach (var i in all)
            Console.Error.WriteLine($"[selftest] found pid={i.Pid} app={i.App} title={i.Title ?? "<none>"} {i.Address}:{i.TcpPort} proto={i.Proto}");

        var target = all[0];
        try
        {
            var pong = await socket.SendAsync(target, "ping");
            Console.Error.WriteLine($"[selftest] ping -> {pong.GetString()}");

            var tree = await socket.SendAsync(target, "describe");
            var regions = tree.GetProperty("regions");
            Console.Error.WriteLine($"[selftest] describe -> {regions.GetArrayLength()} region(s)");
            var shown = 0;
            foreach (var r in regions.EnumerateArray())
            {
                if (shown++ >= 8) break;
                var role = r.GetProperty("role").GetString();
                var label = r.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                Console.Error.WriteLine($"           [{role}] {label ?? "<no label>"}");
            }

            var shot = await socket.SendAsync(target, "screenshot");
            var width = shot.GetProperty("width").GetInt32();
            var height = shot.GetProperty("height").GetInt32();
            var format = shot.GetProperty("format").GetString();
            var payload = Convert.FromBase64String(shot.GetProperty("base64").GetString() ?? "");
            var rgba = format == "rgba+gzip" ? Gunzip(payload) : payload;
            var png = PngWriter.Encode(rgba, width, height);
            var path = Path.Combine(Path.GetTempPath(), "sdlvk-inspector-selftest.png");
            await File.WriteAllBytesAsync(path, png);
            Console.Error.WriteLine($"[selftest] screenshot -> {width}x{height} ({format}), wire={payload.Length} bytes, png={png.Length} bytes");
            Console.Error.WriteLine($"[selftest] saved: {path}");
            // Read-only on purpose: this smoke must stay app-agnostic, so it never injects input or
            // posts signals (which would mutate whatever app it is pointed at, and would need
            // app-specific labels/signals). Drive-path coverage belongs in the consuming app's tests.
            Console.Error.WriteLine("[selftest] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[selftest] FAIL: {ex.Message}");
            return 1;
        }
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
