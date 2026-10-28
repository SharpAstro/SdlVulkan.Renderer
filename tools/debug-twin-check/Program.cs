// Compiles only against a Debug-built SdlVulkan.Renderer: DebugInspector and DebugInspectorOptions are
// #if DEBUG, so the Release package has neither type.
using SdlVulkan.Renderer;

Console.WriteLine(typeof(DebugInspector).FullName);
Console.WriteLine(typeof(DebugInspectorOptions).FullName);
