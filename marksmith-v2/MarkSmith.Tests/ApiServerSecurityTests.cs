using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ApiServerSecurityTests
{
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<int> StartServerRetryingAsync(ApiServer server)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int port = GetFreePort();
            try
            {
                server.Start(port);
                return port;
            }
            catch (HttpListenerException)
            {
                await Task.Delay(50);
            }
        }
        int fallbackPort = GetFreePort();
        server.Start(fallbackPort);
        return fallbackPort;
    }

    private static ApiServer CreateServer(
        AppSettings? currentSettings = null,
        Action<AppSettings>? onSaveSettings = null,
        Func<string, string, OutputOverride?, Task<object>>? onBatchConvert = null)
    {
        var settings = currentSettings ?? new AppSettings { Theme = "GitHub Dark" };
        return new ApiServer(
            new LlmSourceService(),
            () => new List<string> { "GitHub Dark" },
            (md, orig, ovr) => { },
            (md, ovr) => Task.FromResult(Array.Empty<byte>()),
            new GovernanceService(),
            () => "test-extension-id",
            () => settings,
            s => { settings = s; onSaveSettings?.Invoke(s); },
            onBatchConvert ?? ((folder, fmt, ovr) => Task.FromResult<object>(new { done = 1 }))
        );
    }

    [Fact]
    public async Task ApiServer_Binds_To_127_0_0_1_And_Handles_Health()
    {
        using var server = CreateServer();
        int port = await StartServerRetryingAsync(server);
        Assert.True(server.IsRunning);
        Assert.Equal(port, server.Port);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"ok\"", content);

        server.Stop();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task ApiServer_Cors_Does_Not_Return_Wildcard_For_Direct_Requests()
    {
        using var server = CreateServer();
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://127.0.0.1:{port}/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        if (resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values))
        {
            var header = string.Join(",", values);
            Assert.DoesNotContain("*", header);
            Assert.Contains("127.0.0.1", header);
        }

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Cors_Echoes_Allowed_Origin()
    {
        using var server = CreateServer();
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        req.Headers.Add("Origin", "http://127.0.0.1:3000");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        if (resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values))
        {
            var header = string.Join(",", values);
            Assert.Equal("http://127.0.0.1:3000", header);
        }

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Cors_Rejects_Untrusted_Origin()
    {
        using var server = CreateServer();
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        req.Headers.Add("Origin", "http://untrusted-malicious-site.com");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Settings_Get_Blocks_Browser_Web_Origin()
    {
        using var server = CreateServer();
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/settings");
        req.Headers.Add("Origin", "http://127.0.0.1:3000");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("settings are not readable cross-origin", content);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Settings_Get_Blocks_Extension_Origin()
    {
        using var server = CreateServer();
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/settings");
        req.Headers.Add("Origin", "chrome-extension://test-extension-id");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("settings are not readable cross-origin", content);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Settings_Get_Allows_Direct_Requests()
    {
        using var server = CreateServer(new AppSettings { Theme = "GitHub Dark" });
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://127.0.0.1:{port}/api/settings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"theme\":\"GitHub Dark\"", content, StringComparison.OrdinalIgnoreCase);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Settings_Post_Blocks_Browser_Web_Origin()
    {
        bool saveCalled = false;
        using var server = CreateServer(onSaveSettings: _ => saveCalled = true);
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/settings");
        req.Headers.Add("Origin", "http://127.0.0.1:3000");
        req.Content = new StringContent(JsonSerializer.Serialize(new { theme = "Dracula" }), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("settings cannot be modified cross-origin", content);
        Assert.False(saveCalled);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Settings_Post_Blocks_Extension_Origin()
    {
        bool saveCalled = false;
        using var server = CreateServer(onSaveSettings: _ => saveCalled = true);
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/settings");
        req.Headers.Add("Origin", "chrome-extension://test-extension-id");
        req.Content = new StringContent(JsonSerializer.Serialize(new { theme = "Dracula" }), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("settings cannot be modified cross-origin", content);
        Assert.False(saveCalled);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Settings_Post_Allows_Direct_Requests()
    {
        AppSettings? savedSettings = null;
        using var server = CreateServer(onSaveSettings: s => savedSettings = s);
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/settings");
        req.Content = new StringContent(JsonSerializer.Serialize(new { theme = "Nord" }), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", content);
        Assert.NotNull(savedSettings);
        Assert.Equal("Nord", savedSettings.Theme);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Batch_Post_Blocks_Browser_Web_Origin()
    {
        bool batchCalled = false;
        using var server = CreateServer(onBatchConvert: (folder, fmt, ovr) =>
        {
            batchCalled = true;
            return Task.FromResult<object>(new { converted = 5 });
        });
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/batch");
        req.Headers.Add("Origin", "http://127.0.0.1:3000");
        req.Content = new StringContent(JsonSerializer.Serialize(new { folder = "C:\\docs", format = "pdf" }), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("batch conversion is not permitted cross-origin", content);
        Assert.False(batchCalled);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Batch_Post_Blocks_Extension_Origin()
    {
        bool batchCalled = false;
        using var server = CreateServer(onBatchConvert: (folder, fmt, ovr) =>
        {
            batchCalled = true;
            return Task.FromResult<object>(new { converted = 5 });
        });
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/batch");
        req.Headers.Add("Origin", "chrome-extension://test-extension-id");
        req.Content = new StringContent(JsonSerializer.Serialize(new { folder = "C:\\docs", format = "pdf" }), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("batch conversion is not permitted cross-origin", content);
        Assert.False(batchCalled);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Batch_Post_Allows_Direct_Requests()
    {
        bool batchCalled = false;
        using var server = CreateServer(onBatchConvert: (folder, fmt, ovr) =>
        {
            batchCalled = true;
            return Task.FromResult<object>(new { converted = 5 });
        });
        int port = await StartServerRetryingAsync(server);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/batch");
        req.Content = new StringContent(JsonSerializer.Serialize(new { folder = "C:\\docs", format = "pdf" }), Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"converted\":5", content);
        Assert.True(batchCalled);

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Empirical_Stress_Test_Origin_Matrix()
    {
        int saveCount = 0;
        int batchCount = 0;
        var currentSettings = new AppSettings { Theme = "InitialTheme" };

        using var server = CreateServer(
            currentSettings: currentSettings,
            onSaveSettings: s => { saveCount++; currentSettings = s; },
            onBatchConvert: (folder, fmt, ovr) =>
            {
                batchCount++;
                return Task.FromResult<object>(new { processed = 10 });
            });

        int port = await StartServerRetryingAsync(server);
        using var client = new HttpClient();

        var testMatrix = new[]
        {
            // Untrusted external web origins
            ("http://evil.com", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),
            ("https://evil.com", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),
            ("http://evil.com:8080", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),
            ("http://127.0.0.1.attacker.com", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),
            ("http://localhost.attacker.com", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),
            ("http://192.168.1.50:8080", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),

            // Extensions (pinned)
            ("chrome-extension://test-extension-id", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("moz-extension://test-extension-id", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("safari-web-extension://test-extension-id", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),

            // Extensions (unpinned/different)
            ("chrome-extension://untrusted-extension-id", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),
            ("moz-extension://untrusted-extension-id", 403, 403, 403, "forbidden origin", "forbidden origin", "forbidden origin"),

            // Loopback web origins
            ("http://127.0.0.1:8080", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("http://127.0.0.1:3000", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("http://localhost:3000", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("http://localhost:8080", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("http://[::1]:3000", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),
            ("http://[::1]:8080", 403, 403, 403, "settings are not readable cross-origin", "settings cannot be modified cross-origin", "batch conversion is not permitted cross-origin"),

            // Direct / Non-browser / Opaque
            ((string?)null, 200, 200, 200, (string?)null, (string?)null, (string?)null),
            ("null", 200, 200, 200, (string?)null, (string?)null, (string?)null)
        };

        foreach (var (origin, expGet, expPost, expBatch, expGetMsg, expPostMsg, expBatchMsg) in testMatrix)
        {
            // 1. GET /api/settings
            var reqGet = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/settings");
            if (origin != null) reqGet.Headers.Add("Origin", origin);
            var respGet = await client.SendAsync(reqGet);
            Assert.Equal((HttpStatusCode)expGet, respGet.StatusCode);
            var getBody = await respGet.Content.ReadAsStringAsync();
            if (expGetMsg != null) Assert.Contains(expGetMsg, getBody);

            // 2. POST /api/settings
            int preSave = saveCount;
            var reqPost = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/settings");
            if (origin != null) reqPost.Headers.Add("Origin", origin);
            reqPost.Content = new StringContent(JsonSerializer.Serialize(new { theme = "AttackerTheme" }), Encoding.UTF8, "application/json");
            var respPost = await client.SendAsync(reqPost);
            Assert.Equal((HttpStatusCode)expPost, respPost.StatusCode);
            var postBody = await respPost.Content.ReadAsStringAsync();
            if (expPostMsg != null) Assert.Contains(expPostMsg, postBody);

            if (expPost == 403)
                Assert.Equal(preSave, saveCount);
            else
                Assert.Equal(preSave + 1, saveCount);

            // 3. POST /api/batch
            int preBatch = batchCount;
            var reqBatch = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/batch");
            if (origin != null) reqBatch.Headers.Add("Origin", origin);
            reqBatch.Content = new StringContent(JsonSerializer.Serialize(new { folder = "C:\\docs", format = "pdf" }), Encoding.UTF8, "application/json");
            var respBatch = await client.SendAsync(reqBatch);
            Assert.Equal((HttpStatusCode)expBatch, respBatch.StatusCode);
            var batchBody = await respBatch.Content.ReadAsStringAsync();
            if (expBatchMsg != null) Assert.Contains(expBatchMsg, batchBody);

            if (expBatch == 403)
                Assert.Equal(preBatch, batchCount);
            else
                Assert.Equal(preBatch + 1, batchCount);
        }

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_Challenger_Adversarial_Edge_Cases()
    {
        int saveCount = 0;
        int batchCount = 0;
        var currentSettings = new AppSettings { Theme = "InitialTheme" };

        using var server = CreateServer(
            currentSettings: currentSettings,
            onSaveSettings: s => { saveCount++; currentSettings = s; },
            onBatchConvert: (folder, fmt, ovr) =>
            {
                batchCount++;
                return Task.FromResult<object>(new { processed = 1 });
            });

        int port = await StartServerRetryingAsync(server);
        using var client = new HttpClient();

        // 1. PUT / PATCH / DELETE to /api/settings
        var reqPut = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/api/settings");
        reqPut.Content = new StringContent("{\"theme\":\"Bad\"}", Encoding.UTF8, "application/json");
        var respPut = await client.SendAsync(reqPut);
        Assert.Equal(HttpStatusCode.NotFound, respPut.StatusCode);
        Assert.Equal(0, saveCount);

        var reqDel = new HttpRequestMessage(HttpMethod.Delete, $"http://127.0.0.1:{port}/api/settings");
        var respDel = await client.SendAsync(reqDel);
        Assert.Equal(HttpStatusCode.NotFound, respDel.StatusCode);
        Assert.Equal(0, saveCount);

        // 2. GET /api/batch (GET on a POST-only endpoint)
        var reqGetBatch = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/batch");
        var respGetBatch = await client.SendAsync(reqGetBatch);
        Assert.Equal(HttpStatusCode.NotFound, respGetBatch.StatusCode);
        Assert.Equal(0, batchCount);

        // 3. POST /api/settings with invalid JSON body (Direct caller)
        var reqBadJson = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/settings");
        reqBadJson.Content = new StringContent("{{invalid json", Encoding.UTF8, "application/json");
        var respBadJson = await client.SendAsync(reqBadJson);
        Assert.Equal(HttpStatusCode.InternalServerError, respBadJson.StatusCode); // Deserializer throws JsonException -> 500
        Assert.Equal(0, saveCount);

        // 4. POST /api/batch with missing folder (Direct caller)
        var reqNoFolder = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/batch");
        reqNoFolder.Content = new StringContent("{\"format\":\"pdf\"}", Encoding.UTF8, "application/json");
        var respNoFolder = await client.SendAsync(reqNoFolder);
        Assert.Equal(HttpStatusCode.BadRequest, respNoFolder.StatusCode);
        Assert.Contains("folder is required", await respNoFolder.Content.ReadAsStringAsync());
        Assert.Equal(0, batchCount);

        // 5. Case-variant headers (e.g. UPPERCASE ORIGIN)
        var reqCaseOrigin = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/settings");
        reqCaseOrigin.Headers.Add("Origin", "HTTP://EVIL.COM");
        var respCaseOrigin = await client.SendAsync(reqCaseOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, respCaseOrigin.StatusCode);

        // 6. Loopback with user-info / path / query in origin
        var reqComplexLoopback = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/settings");
        reqComplexLoopback.Headers.Add("Origin", "http://127.0.0.1:8080/some/path?param=val");
        var respComplexLoopback = await client.SendAsync(reqComplexLoopback);
        Assert.Equal(HttpStatusCode.Forbidden, respComplexLoopback.StatusCode);
        Assert.Contains("settings are not readable cross-origin", await respComplexLoopback.Content.ReadAsStringAsync());

        server.Stop();
    }
}
