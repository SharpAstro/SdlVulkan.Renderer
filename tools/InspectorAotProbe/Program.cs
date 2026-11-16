using System.Runtime.CompilerServices;
using SdlVulkan.Renderer;

// Touch a renderer type either way, so the baseline links the same library as the other two variants.
var loop = new SdlEventLoop();

#if REFS_ONLY
// Same renderer types the Attach variant names (SdlEventLoop, SdlWindowView), no Attach call: isolates what the types cost.
var refView = (SdlWindowView)RuntimeHelpers.GetUninitializedObject(typeof(SdlWindowView));
Console.WriteLine("refs only: " + loop.GetType().Name + " " + refView.GetType().Name);
return 0;
#elif NO_ATTACH
Console.WriteLine("attach: not called (" + loop.GetType().Name + ")");
return 0;
#else
// Attach only stores the view, so a stand-in will do (as in InspectorOptInTests).
var view = (SdlWindowView)RuntimeHelpers.GetUninitializedObject(typeof(SdlWindowView));
using var inspector = DebugInspector.Attach(loop, view, new DebugInspectorOptions { EnableDiscovery = false });
Console.WriteLine($"IsActive={inspector.IsActive} Port={inspector.Port}");
return 0;
#endif
