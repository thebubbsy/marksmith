using System.Net;
using System.Net.Http;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// GoogleAuthService device flow + token refresh against a stubbed transport — no network.
public class GoogleAuthServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<string> Calls { get; } = new();

        public void Enqueue(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(json) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.Method + " " + request.RequestUri!.AbsoluteUri);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static AppSettings Settings() => new() { GoogleClientId = "client-1", GoogleClientSecret = "secret-1" };

    [Fact]
    public async Task StartDeviceCodeAsync_Parses_Device_Response()
    {
        var handler = new StubHandler();
        handler.Enqueue("""{"device_code":"dc-1","user_code":"UC-123","verification_url":"https://www.google.com/device","expires_in":600,"interval":5}""");
        var auth = new GoogleAuthService(handler);

        var dc = await auth.StartDeviceCodeAsync(Settings());

        Assert.Equal("dc-1", dc.DeviceCode);
        Assert.Equal("UC-123", dc.UserCode);
        Assert.Equal("https://www.google.com/device", dc.VerificationUrl);
        Assert.Equal(600, dc.ExpiresIn);
        Assert.Equal(5, dc.Interval);
        Assert.Contains("oauth2.googleapis.com/device/code", handler.Calls[0]);
    }

    [Fact]
    public async Task PollForTokenAsync_Handles_Pending_Then_Succeeds()
    {
        var handler = new StubHandler();
        handler.Enqueue("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest);
        handler.Enqueue("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""");
        var auth = new GoogleAuthService(handler);

        var tok = await auth.PollForTokenAsync(Settings(), "dc-1", interval: 1, expiresIn: 600);

        Assert.Equal("at-1", tok.AccessToken);
        Assert.Equal("rt-1", tok.RefreshToken);
        Assert.Equal(2, handler.Calls.Count); // pending poll + success poll
    }

    [Fact]
    public async Task PollForTokenAsync_Throws_On_Denial()
    {
        var handler = new StubHandler();
        handler.Enqueue("""{"error":"access_denied"}""", HttpStatusCode.BadRequest);
        var auth = new GoogleAuthService(handler);

        await Assert.ThrowsAsync<GoogleAuthException>(
            () => auth.PollForTokenAsync(Settings(), "dc-1", interval: 1, expiresIn: 600));
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_Returns_Access_Token()
    {
        var handler = new StubHandler();
        handler.Enqueue("""{"access_token":"at-2","expires_in":3599}""");
        var auth = new GoogleAuthService(handler);

        var tok = await auth.RefreshAccessTokenAsync(Settings(), "rt-1");

        Assert.Equal("at-2", tok.AccessToken);
        Assert.Equal("", tok.RefreshToken); // refresh tokens are not re-issued on refresh
        Assert.Contains("oauth2.googleapis.com/token", handler.Calls[0]);
    }

    [Fact]
    public async Task RefreshAccessTokenAsync_Throws_When_Invalid()
    {
        var handler = new StubHandler();
        handler.Enqueue("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        var auth = new GoogleAuthService(handler);

        await Assert.ThrowsAsync<GoogleAuthException>(() => auth.RefreshAccessTokenAsync(Settings(), "bad"));
    }

    [Fact]
    public void IsConfigured_And_IsConnected_Reflect_Client_And_Tokens()
    {
        var auth = new GoogleAuthService(new StubHandler());
        Assert.False(auth.IsConfigured(new AppSettings()));
        Assert.True(auth.IsConfigured(Settings()));
        Assert.False(auth.IsConnected(Settings())); // client but no refresh token
        Assert.True(auth.IsConnected(new AppSettings { GoogleClientId = "c", GoogleRefreshToken = "r" }));
    }

    [Fact]
    public void EffectiveCredentials_Override_Then_Fall_Back_To_Baked_In_Defaults()
    {
        // User override wins when filled in…
        var (id, secret) = GoogleAuthService.EffectiveCredentials(new AppSettings { GoogleClientId = "override-id", GoogleClientSecret = "override-secret" });
        Assert.Equal("override-id", id);
        Assert.Equal("override-secret", secret);

        // …otherwise the built-in client is used so end users never configure anything.
        var (dId, dSecret) = GoogleAuthService.EffectiveCredentials(new AppSettings());
        Assert.Equal(GoogleDefaults.ClientId, dId);
        Assert.Equal(GoogleDefaults.ClientSecret, dSecret);
    }
}
