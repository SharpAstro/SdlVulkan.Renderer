using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdlVulkan.Renderer.Inspector.Client;
using Shouldly;
using Xunit;

namespace SdlVulkan.Renderer.Tests;

/// <summary>
/// <see cref="InspectorClient"/> against a loopback fake server on an ephemeral port: the request each verb
/// puts on the wire, the token on every request, and the server's own error text surfacing as an
/// <see cref="InspectorException"/>. Every call is bounded by a deadline token, never a sleep.
/// </summary>
public sealed class InspectorClientTests
{
    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    /// <summary>Answers each connection's one request line through <paramref name="reply"/>; null closes with no reply.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<JsonElement, string?> _reply;
        public List<JsonElement> Requests { get; } = [];

        public FakeServer(Func<JsonElement, string?> reply)
        {
            _reply = reply;
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public InspectorInstance Instance => new(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port,
            "fake", null, Environment.ProcessId, 1, "sdl");

        private async Task AcceptLoop()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await Serve(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(_stop.Token);
            if (line is null) return;
            var root = JsonDocument.Parse(line).RootElement.Clone();
            lock (Requests) Requests.Add(root);
            var response = _reply(root);
            if (response is null) return;
            await writer.WriteLineAsync(response.AsMemory(), _stop.Token);
        }

        public JsonElement Single
        {
            get { lock (Requests) { Requests.Count.ShouldBe(1); return Requests[0]; } }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static string Ok(string resultJson = "\"ok\"") => $"{{\"id\":1,\"result\":{resultJson}}}";

    [Fact]
    public async Task Click_SendsMethodParamsAndToken()
    {
        using var server = new FakeServer(_ => Ok());
        var client = new InspectorClient(server.Instance, "s3cret");

        await client.ClickAsync(10.5f, 20f, "Ctrl", 2, Deadline());

        var req = server.Single;
        req.GetProperty("method").GetString().ShouldBe("click");
        req.GetProperty("token").GetString().ShouldBe("s3cret");
        var p = req.GetProperty("params");
        p.GetProperty("x").GetDouble().ShouldBe(10.5);
        p.GetProperty("y").GetDouble().ShouldBe(20);
        p.GetProperty("mods").GetString().ShouldBe("Ctrl");
        p.GetProperty("clicks").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task EveryVerb_SendsTheTokenAndItsWireShape()
    {
        using var server = new FakeServer(r => r.GetProperty("method").GetString() switch
        {
            "resize" => Ok("{\"width\":700,\"height\":900,\"clamped\":true}"),
            "frameStats" => Ok("{\"avgFrameMs\":4.5,\"slowFrameFloorMs\":16}"),
            _ => Ok("{}"),
        });
        var client = new InspectorClient(server.Instance, "tok");
        var ct = Deadline();

        await client.DescribeUiAsync(ct);
        await client.DescribeLayoutAsync(ct);
        await client.DragAsync(1, 2, 3, 4, "Shift", 5, ct);
        await client.MoveAsync(1, 2, 3, 4, 6, "None", ct);
        await client.PressKeyAsync("Enter", "Alt", ct);
        await client.TypeTextAsync("hi \"q\"", ct);
        var resized = await client.ResizeAsync(700, 900, ct);
        await client.RestoreAsync(ct);
        var stats = await client.FrameStatsAsync(ct);
        await client.ValidationReportAsync(ct);
        await client.SignalsAsync(ct);

        resized.ShouldBe(new InspectorResizeResult(700, 900, true));
        stats.ShouldBe(new InspectorFrameStats(4.5, 16));

        var seen = new List<string>();
        lock (server.Requests)
        {
            foreach (var r in server.Requests)
            {
                r.GetProperty("token").GetString().ShouldBe("tok");
                seen.Add(r.GetProperty("method").GetString()!);
            }
        }
        seen.ShouldBe(["describe", "describeLayout", "drag", "move", "key", "text", "resize", "restore",
            "frameStats", "validationReport", "signals"]);

        lock (server.Requests)
        {
            server.Requests[2].GetProperty("params").GetProperty("steps").GetInt32().ShouldBe(5);
            server.Requests[4].GetProperty("params").GetProperty("key").GetString().ShouldBe("Enter");
            server.Requests[4].GetProperty("params").GetProperty("mods").GetString().ShouldBe("Alt");
            server.Requests[5].GetProperty("params").GetProperty("s").GetString().ShouldBe("hi \"q\"");
            server.Requests[6].GetProperty("params").GetProperty("width").GetInt32().ShouldBe(700);
        }
    }

    [Fact]
    public async Task ErrorReply_ThrowsInspectorExceptionWithTheServersText()
    {
        using var server = new FakeServer(_ => "{\"id\":1,\"error\":\"unknown key 'Hyper'\"}");
        var client = new InspectorClient(server.Instance, "tok");

        var ex = await Should.ThrowAsync<InspectorException>(() => client.PressKeyAsync("Hyper", "None", Deadline()));

        ex.Method.ShouldBe("key");
        ex.ServerMessage.ShouldBe("unknown key 'Hyper'");
        ex.Message.ShouldContain("unknown key 'Hyper'");
        ex.Unauthorized.ShouldBeFalse();
    }

    [Fact]
    public async Task RefusedToken_ThrowsUnauthorized()
    {
        using var server = new FakeServer(_ => "{\"id\":1,\"error\":\"unauthorized\"}");
        var client = new InspectorClient(server.Instance, "wrong");

        var ex = await Should.ThrowAsync<InspectorException>(() => client.DescribeUiAsync(Deadline()));

        ex.Unauthorized.ShouldBeTrue();
        ex.Method.ShouldBe("describe");
    }

    [Fact]
    public async Task ClosedWithNoReply_ThrowsInspectorException()
    {
        using var server = new FakeServer(_ => null);
        var client = new InspectorClient(server.Instance, "tok");

        var ex = await Should.ThrowAsync<InspectorException>(() => client.RestoreAsync(Deadline()));

        ex.Method.ShouldBe("restore");
        ex.ServerMessage.ShouldContain("closed the connection");
    }

    [Fact]
    public async Task Screenshot_DecodesRgbaGzipAndEncodesPng()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 10, 20, 30, 40];   // 2 x 2
        string b64;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(rgba);
            b64 = Convert.ToBase64String(ms.ToArray());
        }
        using var server = new FakeServer(_ =>
            Ok($"{{\"width\":2,\"height\":2,\"format\":\"rgba+gzip\",\"base64\":\"{b64}\",\"unminimised\":true}}"));
        var client = new InspectorClient(server.Instance, "tok");

        var shot = await client.ScreenshotAsync(reminimize: true, width: 2, height: 2, ct: Deadline());

        shot.Width.ShouldBe(2);
        shot.Height.ShouldBe(2);
        shot.Rgba.ShouldBe(rgba);
        shot.Unminimised.ShouldBeTrue();
        shot.Png.AsSpan(0, 8).ToArray().ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var p = server.Single.GetProperty("params");
        p.GetProperty("reminimize").GetBoolean().ShouldBeTrue();
        p.GetProperty("width").GetInt32().ShouldBe(2);
        p.GetProperty("height").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task RenderLiveness_AliveWhenPingRoundTrips()
    {
        using var server = new FakeServer(_ => Ok("{\"ok\":true}"));
        var client = new InspectorClient(server.Instance, "tok");

        var r = await client.RenderLivenessAsync(TimeSpan.FromSeconds(10), Deadline());

        r.Status.ShouldBe(RenderLiveness.Alive);
        var req = server.Single;
        req.GetProperty("method").GetString().ShouldBe("ping");
        req.GetProperty("token").GetString().ShouldBe("tok");
    }

    [Fact]
    public async Task ConnectAsync_ResolvesThePidThroughTheRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sdlvk-inspector-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pid = Environment.ProcessId;
            File.WriteAllText(Path.Combine(dir, $"{pid}-4711.json"),
                $"{{\"app\":\"demo\",\"pid\":{pid},\"tcpPort\":4711,\"proto\":1,\"kind\":\"sdl\"}}");

            var client = await InspectorClient.ConnectAsync(pid, "tok", dir, Deadline());
            client.Instance.TcpPort.ShouldBe(4711);
            client.Instance.Pid.ShouldBe(pid);

            var none = await Should.ThrowAsync<InspectorException>(() => InspectorClient.ConnectAsync(pid + 1, "tok", dir, Deadline()));
            none.ServerMessage.ShouldContain("No instance with pid");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
