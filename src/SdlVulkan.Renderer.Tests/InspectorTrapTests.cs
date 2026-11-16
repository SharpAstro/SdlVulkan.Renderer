using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using SdlVulkan.Renderer.Inspector;
using SdlVulkan.Renderer.Inspector.Client;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>Instance 0 with several inspected apps, and the real error text reaching an MCP caller.</summary>
public class InspectorTrapTests
{
    private static InspectorInstance Instance(string app, int pid)
        => new(IPAddress.Loopback, 40000 + pid % 1000, app, null, pid, 1, "sdl");

    [Fact]
    public void InstanceZeroWithTwoAppsNamesBothAndAsksForAPid()
    {
        var all = new[] { Instance("TianWen", 111), Instance("Other", 222) };

        var ex = Should.Throw<InspectorException>(() => InspectorClient.Resolve(all, 0));

        ex.Message.ShouldContain("2 inspected apps");
        ex.Message.ShouldContain("pass a pid");
        ex.Message.ShouldContain("TianWen(pid 111)");
        ex.Message.ShouldContain("Other(pid 222)");
    }

    [Fact]
    public void AnExplicitPidOrASoleInstanceStillResolves()
    {
        var two = new[] { Instance("A", 1), Instance("B", 2) };
        InspectorClient.Resolve(two, 2).App.ShouldBe("B");
        InspectorClient.Resolve([two[0]], 0).Pid.ShouldBe(1);
        Should.Throw<InspectorException>(() => InspectorClient.Resolve(two, 3)).Message.ShouldContain("No instance with pid 3");
        Should.Throw<InspectorException>(() => InspectorClient.Resolve([], 0)).Message.ShouldContain("No inspectable instances");
    }

    [Fact]
    public async Task TheSidecarFilterReturnsTheServersOwnMessage()
    {
        var filter = InspectorErrorFilter.Create();
        var handler = filter((_, _) => throw new InspectorException("screenshot", "'screenshot' is already in progress"));

        var result = await handler(null!, CancellationToken.None);

        result.IsError.ShouldBe(true);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.ShouldContain("'screenshot' is already in progress");
        text.ShouldNotContain("An error occurred invoking");
    }

    [Fact]
    public async Task TheSidecarFilterPassesASuccessThrough()
    {
        var ok = new CallToolResult();
        var handler = InspectorErrorFilter.Create()((_, _) => new ValueTask<CallToolResult>(ok));

        (await handler(null!, CancellationToken.None)).ShouldBeSameAs(ok);
    }
}
