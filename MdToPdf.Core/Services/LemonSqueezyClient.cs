using System.Net.Http.Json;
using System.Text.Json;

namespace MdToPdf.Services;

// Optional ONLINE license activation via Lemon Squeezy's License API. Off by default — flip Enabled
// to true once your Lemon Squeezy store issues license keys. Offline signed keys (LicenseValidator)
// are the primary path and need no network; this is the alternative if you'd rather validate keys
// server-side. Docs: https://docs.lemonsqueezy.com/help/licensing/license-api-introduction
public static class LemonSqueezyClient
{
    // Set to true when you sell via Lemon Squeezy and issue their license keys.
    public static bool Enabled { get; set; } = false;
    public static string ApiUrl { get; set; } = "https://api.lemonsqueezy.com/v1/licenses/activate";

    private static HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static HttpClient Http { get => _http; set => _http = value; }

    public static async Task<(bool ok, string message, string? email, string? instanceId)> ActivateAsync(string key)
    {
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["license_key"] = key,
                ["instance_name"] = Environment.MachineName,
            });
            using var resp = await Http.PostAsync(ApiUrl, body);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

            bool activated = json.TryGetProperty("activated", out var a) && a.ValueKind == JsonValueKind.True;
            if (!activated)
            {
                var err = json.TryGetProperty("error", out var e) ? e.GetString() : null;
                return (false, err ?? "Activation failed. Check the key or your connection.", null, null);
            }

            string? email = null, inst = null;
            if (json.TryGetProperty("meta", out var m) && m.TryGetProperty("customer_email", out var ce))
                email = ce.GetString();
            if (json.TryGetProperty("instance", out var i) && i.TryGetProperty("id", out var id))
                inst = id.GetString();
            return (true, "Activated.", email, inst);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null);
        }
    }
}
