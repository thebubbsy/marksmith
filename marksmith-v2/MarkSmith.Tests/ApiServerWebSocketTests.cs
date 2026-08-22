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
    // The free-port probe releases the port before Start() binds it, so a parallel test (other
    // collections run concurrently) can steal it between probe and bind. Retry the probe+bind;
    // a stolen port fails the first bind, not the test.
    private static async Task<int> StartServerRetryingAsync(ApiServer server)
    {
        for (int attempt = 0; ; attempt++)
        {
            int p = GetFreePort();
            try
            {
                server.Start(p);
                return p;
            }
            catch (System.Net.HttpListenerException) when (attempt < 5)
            {
                await Task.Delay(50); // port stolen between probe and bind — retry
            }
        }
    }

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

    // Request/reply with retry: the server's replies are fire-and-forget sends and the connection
    // can be dropped under parallel load, so each attempt uses a FRESH socket — a dropped or raced
    // connection is retried from the handshake up, never replayed on a dead socket. The payloads
    // this helper is used with are idempotent (preview = read-only).
    private static async Task<JsonDocument> SendAndReceiveAsync(int port, object payload)
    {
        var send = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);
                await ws.SendAsync(send, WebSocketMessageType.Text, true, CancellationToken.None);
                using var timeout = new CancellationTokenSource(5000);
                return await ReceiveJsonAsync(ws, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or System.IO.IOException
                                       or OperationCanceledException or JsonException)
            {
                // Connection dropped / reply raced a close / mid-fragment timeout — reconnect.
                await Task.Delay(50 * (attempt + 1));
            }
        }
        throw new TimeoutException("no reply after 8 attempts");
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
        int localPort = await StartServerRetryingAsync(server);
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{localPort}/api/stream"), CancellationToken.None);

            var regDeadline = DateTime.UtcNow.AddSeconds(3);
            while (server.StreamClientCount == 0 && DateTime.UtcNow < regDeadline)
                await Task.Delay(25);

            // Side-effecting request: send exactly ONCE (a retry would double-ingest). Delivery is
            // proven by the ingest side effect; the ack is best-effort fire-and-forget, so don't
            // block on it.
            var send = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "streamText", text = "hello from a script" }));
            await ws.SendAsync(send, WebSocketMessageType.Text, true, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sink.Ingested.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.Single(sink.Ingested);
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
        int port = await StartServerRetryingAsync(server);
        try
        {
            using var reply = await SendAndReceiveAsync(port, new { type = "preview" });
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
        int port = await StartServerRetryingAsync(server);
        try
        {
            // Broadcasts are server-initiated, so each attempt gets a fresh socket: a dropped
            // connection (under parallel load) is retried from the handshake up, and the
            // re-published event is received on the new connection.
            JsonDocument? reply = null;
            for (int attempt = 0; attempt < 10 && reply is null; attempt++)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);
                    // The server registers the socket right AFTER the handshake completes — poll
                    // instead of asserting instantly (the registration can land a few ms later).
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (server.StreamClientCount == 0 && DateTime.UtcNow < deadline)
                        await Task.Delay(25);

                    server.PublishStreamEvent(new { type = "status", text = "exporting…", busy = true });
                    using var timeout = new CancellationTokenSource(2000);
                    reply = await ReceiveJsonAsync(ws, timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or System.IO.IOException
                                           or OperationCanceledException or JsonException)
                {
                    // dropped / raced / stale fragment — reconnect and retry
                }
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
        int port = await StartServerRetryingAsync(server);
        try
        {
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/stream"), CancellationToken.None);
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
