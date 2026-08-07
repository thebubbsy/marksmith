using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Xunit;
using MarkSmith.Services;
using MarkSmith.Models;

namespace MarkSmith.Tests;

// WebSocket streaming on the local REST API (ws://127.0.0.1:<port>/api/stream). OFF by default —
// the endpoint 403s unless Settings > Local REST API > Enable WebSocket streaming is flipped on.
[Collection("ApiServer")]
public class ApiServerWebSocketTests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Sink
    {
        public AppSettings Settings { get; } = new();
        public List<string> Ingested { get; } = new();
        public string Preview { get; set; } = "<html>preview</html>";

        public ApiServer CreateServer()
        {
            return new ApiServer(
                new LlmSourceService(),
                () => new List<string> { "GitHub Dark" },
                (md, orig, ovr) => { lock (Ingested) Ingested.Add(md); },
                (md, ovr) => Task.FromResult(Array.Empty<byte>()),
                new GovernanceService(),
                () => "",
                () => Settings,
                _ => { },
                (folder, fmt, ovr) => Task.FromResult<object>(new { done = 0 })
            )
            { PreviewHtmlProvider = () => Preview };
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int attempts = 100)
    {
        for (int i = 0; i < attempts && !condition(); i++) await Task.Delay(50);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken token = default)
    {
        var buffer = new byte[256 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(sb.ToString());
    }

    // Request/reply with retry: the server's replies are fire-and-forget sends, so under the full
    // suite's parallel load a reply can lose a scheduling race. Re-send until one arrives.
    private static async Task<JsonDocument> SendAndReceiveAsync(ClientWebSocket ws, object payload)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var send = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await ws.SendAsync(send, WebSocketMessageType.Text, true, CancellationToken.None);
            using var timeout = new CancellationTokenSource(1500);
            try
            {
                return await ReceiveJsonAsync(ws, timeout.Token);
            }
            catch (OperationCanceledException) { /* retry */ }
        }
        throw new TimeoutException("no reply after 10 attempts");
    }

    [Fact]
    public async Task Disabled_ByDefault_RejectsUpgrade()
    {
        var sink = new Sink();
        using var server = sink.CreateServer();
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using var ws = new ClientWebSocket();
            await Assert.ThrowsAsync<WebSocketException>(() =>
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None));
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Enabled_StreamsTextIntoEditorAndAcks()
    {
        var sink = new Sink();
        sink.Settings.EnableStreamingApi = true;
        using var server = sink.CreateServer();
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);

            using var reply = await SendAndReceiveAsync(ws, new { type = "streamText", text = "hello from a script" });
            Assert.Equal("ack", reply.RootElement.GetProperty("type").GetString());
            Assert.Equal(19, reply.RootElement.GetProperty("chars").GetInt32());
            Assert.Contains(sink.Ingested, s => s == "hello from a script");
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Enabled_ClientPullsPreviewSnapshot()
    {
        var sink = new Sink();
        sink.Settings.EnableStreamingApi = true;
        sink.Preview = "<html>the live preview</html>";
        using var server = sink.CreateServer();
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);

            using var reply = await SendAndReceiveAsync(ws, new { type = "preview" });
            Assert.Equal("preview", reply.RootElement.GetProperty("type").GetString());
            Assert.Equal("<html>the live preview</html>", reply.RootElement.GetProperty("html").GetString());
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task PublishStreamEvent_BroadcastsToConnectedClients()
    {
        var sink = new Sink();
        sink.Settings.EnableStreamingApi = true;
        using var server = sink.CreateServer();
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);
            Assert.Equal(1, server.StreamClientCount);

            JsonDocument? reply = null;
            for (int attempt = 0; attempt < 10 && reply is null; attempt++)
            {
                server.PublishStreamEvent(new { type = "status", text = "exporting…", busy = true });
                using var timeout = new CancellationTokenSource(1500);
                try { reply = await ReceiveJsonAsync(ws, timeout.Token); }
                catch (OperationCanceledException) { /* retry */ }
            }
            Assert.NotNull(reply);
            Assert.Equal("status", reply!.RootElement.GetProperty("type").GetString());
            Assert.Equal("exporting…", reply.RootElement.GetProperty("text").GetString());
            Assert.True(reply.RootElement.GetProperty("busy").GetBoolean());
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task ClientDisconnect_DropsFromBroadcastList()
    {
        var sink = new Sink();
        sink.Settings.EnableStreamingApi = true;
        using var server = sink.CreateServer();
        int port = GetFreePort();
        server.Start(port);
        try
        {
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);
                // The server registers the socket asynchronously AFTER the handshake completes —
                // poll both directions so a slow full-suite run can't flake either assertion.
                await WaitForAsync(() => server.StreamClientCount >= 1);
                Assert.Equal(1, server.StreamClientCount);
            }
            // Give the server's receive loop a moment to observe the close (poll — the loop can be
            // delayed under parallel full-suite load).
            await WaitForAsync(() => server.StreamClientCount == 0);
            Assert.Equal(0, server.StreamClientCount);
        }
        finally { server.Stop(); }
    }
}
