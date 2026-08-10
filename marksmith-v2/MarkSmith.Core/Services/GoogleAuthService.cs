using System.Net.Http;
using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

/// <summary>
/// OAuth 2.0 device-flow client for Google (Docs + Drive). The user supplies their own Google
/// Cloud OAuth client (id + secret) in Settings → Google; the app then runs the device flow: it
/// shows a code + URL, the user authorizes in their browser, and we poll for tokens. The refresh
/// token is persisted so later exports need no browser interaction.
/// </summary>
public sealed class GoogleAuthService
{
    public const string DeviceCodeUrl = "https://oauth2.googleapis.com/device/code";
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    public const string TokenInfoUrl = "https://oauth2.googleapis.com/tokeninfo";

    // documents: create + edit the Google Doc. drive: upload the images that go inside it (full
    // Drive access so uploaded images can be shared with anyone-with-link for rendering in shared
    // docs — drive.file cannot create permissions).
    public const string Scope = "https://www.googleapis.com/auth/documents https://www.googleapis.com/auth/drive";

    private readonly HttpClient _http;

    public GoogleAuthService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public bool IsConfigured(AppSettings settings) => EffectiveCredentials(settings).Id.Length > 0;
    public bool IsConnected(AppSettings settings) => IsConfigured(settings) && !string.IsNullOrWhiteSpace(settings.GoogleRefreshToken);

    /// <summary>
    /// The client credentials to use: the user's Settings override wins; otherwise the app's
    /// built-in client (GoogleDefaults) is used so end users never configure anything.
    /// </summary>
    public static (string Id, string Secret) EffectiveCredentials(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.GoogleClientId)
            ? (settings.GoogleClientId, settings.GoogleClientSecret ?? "")
            : (GoogleDefaults.ClientId, GoogleDefaults.ClientSecret);

    public sealed record DeviceCodeResult(string DeviceCode, string UserCode, string VerificationUrl, int ExpiresIn, int Interval);

    public sealed record TokenResult(string AccessToken, string RefreshToken, int ExpiresIn);

    /// <summary>Starts the device flow: returns the code to show the user and the poll parameters.</summary>
    public async Task<DeviceCodeResult> StartDeviceCodeAsync(AppSettings settings, CancellationToken ct = default)
    {
        var creds = EffectiveCredentials(settings);
        var form = new Dictionary<string, string> { ["client_id"] = creds.Id, ["scope"] = Scope };
        if (!string.IsNullOrWhiteSpace(creds.Secret)) form["client_secret"] = creds.Secret;

        using var resp = await _http.PostAsync(DeviceCodeUrl, new FormUrlEncodedContent(form), ct);
        var json = await ReadJsonAsync(resp, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleAuthException($"Google sign-in couldn't start ({resp.StatusCode}): {Truncate(json)}");

        return new DeviceCodeResult(
            json.GetProperty("device_code").GetString() ?? "",
            json.GetProperty("user_code").GetString() ?? "",
            json.TryGetProperty("verification_url", out var u) ? u.GetString() ?? "https://www.google.com/device" : "https://www.google.com/device",
            json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 600,
            json.TryGetProperty("interval", out var i) ? i.GetInt32() : 5);
    }

    /// <summary>
    /// Polls the token endpoint until the user approves (or denies/expires). Slow-down and
    /// pending responses are handled per the device-flow spec.
    /// </summary>
    public async Task<TokenResult> PollForTokenAsync(AppSettings settings, string deviceCode, int interval, int expiresIn, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(expiresIn, 60));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var form = new Dictionary<string, string>
            {
                ["client_id"] = EffectiveCredentials(settings).Id,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            };
            if (!string.IsNullOrWhiteSpace(EffectiveCredentials(settings).Secret)) form["client_secret"] = EffectiveCredentials(settings).Secret;

            using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
            var json = await ReadJsonAsync(resp, ct);
            if (resp.IsSuccessStatusCode) return ParseToken(json);

            var err = json.TryGetProperty("error", out var er) ? er.GetString() : "";
            switch (err)
            {
                case "authorization_pending": await Task.Delay(interval * 1000, ct); continue;
                case "slow_down": await Task.Delay((interval + 5) * 1000, ct); continue;
                case "access_denied": throw new GoogleAuthException("Sign-in was not approved in the browser.");
                case "expired_token" or "expired": throw new GoogleAuthException("The sign-in code expired — start again.");
                default: throw new GoogleAuthException($"Google sign-in failed: {err ?? resp.StatusCode.ToString()}");
            }
        }
        throw new GoogleAuthException("Sign-in timed out — start again.");
    }

    /// <summary>Exchanges the persisted refresh token for a fresh access token.</summary>
    public async Task<TokenResult> RefreshAccessTokenAsync(AppSettings settings, string refreshToken, CancellationToken ct = default)
    {
        var creds = EffectiveCredentials(settings);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = creds.Id,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        };
        if (!string.IsNullOrWhiteSpace(creds.Secret)) form["client_secret"] = creds.Secret;

        using var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var json = await ReadJsonAsync(resp, ct);
        if (!resp.IsSuccessStatusCode)
            throw new GoogleAuthException("Google connection expired — reconnect in Settings → Google.");
        return ParseToken(json);
    }

    /// <summary>Best-effort account email for the access token (empty when the scope doesn't expose it).</summary>
    public async Task<string> FetchAccountEmailAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(TokenInfoUrl + "?access_token=" + Uri.EscapeDataString(accessToken), ct);
            if (!resp.IsSuccessStatusCode) return "";
            var json = await ReadJsonAsync(resp, ct);
            return json.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static TokenResult ParseToken(JsonElement json) => new(
        json.GetProperty("access_token").GetString() ?? "",
        json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
        json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        try { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
        catch { throw new GoogleAuthException($"Unexpected Google response ({resp.StatusCode})."); }
    }

    private static string Truncate(JsonElement json)
    {
        var s = json.ToString();
        return s.Length <= 200 ? s : s[..200];
    }
}

/// <summary>User-facing Google auth failure (device flow, token refresh, transport).</summary>
public sealed class GoogleAuthException : Exception
{
    public GoogleAuthException(string message) : base(message) { }
    public GoogleAuthException(string message, Exception inner) : base(message, inner) { }
}
