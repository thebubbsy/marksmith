using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Threading.Tasks;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

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
    private static ApiServer CreateServer()
    {
        return new ApiServer(
            new LlmSourceService(),
            () => new List<string> { "GitHub Dark" },
            (md, orig, ovr) => { },
            (md, ovr) => Task.FromResult(Array.Empty<byte>()),
            new GovernanceService(),
            () => "test-extension-id",
            () => new AppSettings(),
            s => { },
            (folder, fmt, ovr) => Task.FromResult<object>(new { done = 0 })
        );
    }

    [Fact]
    public async Task ApiServer_Binds_To_127_0_0_1_And_Handles_Health()
    {
        using var server = CreateServer();
        int port = GetFreePort();
        server.Start(port);
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
        int port = GetFreePort();
        server.Start(port);

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
        int port = GetFreePort();
        server.Start(port);

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
        int port = GetFreePort();
        server.Start(port);

        using var client = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        req.Headers.Add("Origin", "http://untrusted-malicious-site.com");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        server.Stop();
    }
}
