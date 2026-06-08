using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SdlVulkan.Renderer.Inspector;

// Stdio MCP server bridging an AI agent to running Debug-build SDL3+Vulkan apps. Spawned by the
// MCP client (e.g. Claude Code) as a child process via `dnx SdlVulkan.Renderer.Inspector --yes`.
// All logging goes to stderr -- stdout is the JSON-RPC channel and must stay clean.
//
// Discovery overrides (optional): --group <multicast-ip> / --port <n>, or the
// SDLVK_INSPECTOR_GROUP / SDLVK_INSPECTOR_PORT environment variables. Defaults match the framework.

var group = IPAddress.Parse(GetOption("--group", "SDLVK_INSPECTOR_GROUP") ?? "239.255.77.90");
var port = int.TryParse(GetOption("--port", "SDLVK_INSPECTOR_PORT"), out var p) ? p : 47891;

// Headless protocol self-test (no MCP server): discover + ping + describe + screenshot, then exit.
if (Array.Exists(args, a => a == "selftest"))
{
    return await SelfTest.RunAsync(new InspectorDiscoveryClient(group, port), new InspectorSocketClient());
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(new InspectorDiscoveryClient(group, port));
builder.Services.AddSingleton<InspectorSocketClient>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = """
            SdlVulkan live UI inspector -- discover and drive running Debug-build SDL3+Vulkan apps
            built on SdlVulkan.Renderer (e.g. TianWen GUI). The target app must be running in Debug
            with DebugInspector.Attach (the inspector is compiled out of Release builds entirely).
            Tools:
              - list_instances   Discover running instances (local + LAN). Start here.
              - ping             Confirm an instance is alive.
              - describe_ui      The live clickable-region tree (bounds + role + label) + app state.
              - screenshot       PNG of the instance's current window frame.
              - click / click_label   Synthesize a mouse click by pixel or by button label.
              - press_key / type_text  Inject keyboard input.
              - list_signals / post_signal   Inspect + post named app signals.
            When multiple instances are running, pass instance=<pid> (see list_instances).
            """;
    })
    .WithStdioServerTransport()
    .WithTools<InspectorTools>();

await builder.Build().RunAsync();
return 0;

string? GetOption(string argName, string envName)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == argName)
            return args[i + 1];
    return Environment.GetEnvironmentVariable(envName);
}
