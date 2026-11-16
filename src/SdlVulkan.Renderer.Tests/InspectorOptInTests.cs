using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using DIR.Lib.Diagnostics;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// The run-time opt-in of <see cref="DebugInspector.Attach(SdlEventLoop, SdlWindowView, DebugInspectorOptions)"/>.
/// DIR.Lib offers no seam that takes the variable values (its overload that does is internal), so these set the
/// two real variables and restore them; the class is its own collection so nothing else runs while they are set.
/// No sleeps: the one socket exchange is awaited.
/// </summary>
[Collection("InspectorEnvironment")]
public class InspectorOptInTests
{
    private sealed class EnvScope : IDisposable
    {
        private readonly string? _optIn = Environment.GetEnvironmentVariable(DebugInspectorCore.OptInVariable);
        private readonly string? _token = Environment.GetEnvironmentVariable(DebugInspectorCore.TokenVariable);

        public EnvScope(string? optIn, string? token)
        {
            Environment.SetEnvironmentVariable(DebugInspectorCore.OptInVariable, optIn);
            Environment.SetEnvironmentVariable(DebugInspectorCore.TokenVariable, token);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(DebugInspectorCore.OptInVariable, _optIn);
            Environment.SetEnvironmentVariable(DebugInspectorCore.TokenVariable, _token);
        }
    }

    // Attach only stores the view, and a request refused for its token never reaches Poke, so a stand-in will do.
    private static SdlWindowView View() => (SdlWindowView)RuntimeHelpers.GetUninitializedObject(typeof(SdlWindowView));

    private static DebugInspectorOptions Options() => new() { EnableDiscovery = false };

#if !DEBUG
    // A Debug build stays always-on when DIR_INSPECTOR is absent, so "unset means inert" is the Release contract.
    [Fact]
    public void Unset_AttachesInert_NoListenerAndNoHook()
    {
        using var _ = new EnvScope(null, null);
        var loop = new SdlEventLoop();
        Action hook = () => { };
        loop.OnLoopIteration = hook;

        using var inspector = DebugInspector.Attach(loop, View(), Options());

        inspector.IsActive.ShouldBeFalse();
        inspector.Port.ShouldBe(0);
        loop.OnLoopIteration.ShouldBeSameAs(hook, "an inert inspector must not chain onto the loop");
    }
#endif

    [Fact]
    public void OptInWithoutToken_AttachesInert()
    {
        using var _ = new EnvScope("1", null);
        var loop = new SdlEventLoop();

        using var inspector = DebugInspector.Attach(loop, View(), Options());

        inspector.IsActive.ShouldBeFalse();
        loop.OnLoopIteration.ShouldBeNull();
    }

    [Fact]
    public async Task OptedIn_StartsTheCore_AndRefusesARequestWithoutTheToken()
    {
        using var _ = new EnvScope("1", "s3cret");
        var loop = new SdlEventLoop();

        using var inspector = DebugInspector.Attach(loop, View(), Options());

        inspector.IsActive.ShouldBeTrue();
        inspector.Port.ShouldBeGreaterThan(0);
        loop.OnLoopIteration.ShouldNotBeNull("the pump rides the loop's per-iteration hook");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, inspector.Port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync("""{"id":1,"method":"describe"}""");
        (await reader.ReadLineAsync()).ShouldBe("""{"id":1,"error":"unauthorized"}""");

        await writer.WriteLineAsync("""{"id":2,"method":"describe","token":"wrong"}""");
        (await reader.ReadLineAsync()).ShouldBe("""{"id":2,"error":"unauthorized"}""");
    }
}
