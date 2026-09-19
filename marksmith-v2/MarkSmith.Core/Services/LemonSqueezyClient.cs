using System.Net.Http.Json;
using System.Text.Json;

namespace MarkSmith.Services;

// ONLINE license activation via Lemon Squeezy's License API.
//
// Two licensing models ship in this app and they are NOT interchangeable:
//
//   * Offline signed keys (LicenseValidator) — we mint them ourselves with the private key. No
//     network, unforgeable, but every sale needs us to run sign-license.ps1 and email the result.
//   * Lemon Squeezy keys (this file) — LS generates a plain UUID on purchase and emails it to the
//     buyer automatically. Nothing to run, no backend, but the key is only meaningful to LS, so it
//     MUST be checked against their API. It will never pass the offline signature check.
//
// That second point is the whole reason this client has to be switched on: if the store issues LS
// keys while Enabled is false, a paying customer pastes their key, the offline check rejects it
// (correctly — it isn't a signed token), there is no fallback, and they are told the key they just
// paid for isn't valid.
//
// These three endpoints take no API key. That is deliberate on Lemon Squeezy's part and it matters
// here: a desktop binary cannot keep a secret, so anything requiring the store's API key would have
// to live on a server we do not have. Never put a Lemon Squeezy API key in this app.
//
// Docs: https://docs.lemonsqueezy.com/api/license-api
public static class LemonSqueezyClient
{
    // On when the store issues Lemon Squeezy license keys. Defaults from the environment so a build
    // does not have to be recompiled to switch the online path on, or off again if the store moves.
    // MARKSMITH_LS_ACTIVATION=0/false turns it off; anything else present turns it on.
    public static bool Enabled { get; set; } = ReadEnabledDefault();

    public static string ApiUrl { get; set; } = "https://api.lemonsqueezy.com/v1/licenses/activate";
    public static string ValidateApiUrl { get; set; } = "https://api.lemonsqueezy.com/v1/licenses/validate";
    public static string DeactivateApiUrl { get; set; } = "https://api.lemonsqueezy.com/v1/licenses/deactivate";

    private static HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    internal static HttpClient Http { get => _http; set => _http = value; }

    private static bool ReadEnabledDefault()
    {
        var v = Environment.GetEnvironmentVariable("MARKSMITH_LS_ACTIVATION");
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        return !(v.Equals("0", StringComparison.Ordinal)
                 || v.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("off", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What Lemon Squeezy told us about a key. Ok means the call succeeded AND the key is currently
    /// good; everything else is detail the license state needs so it can expire, revoke or explain.
    /// Reachable distinguishes "LS says no" from "we could not ask" — the difference between
    /// revoking someone's Pro and letting them keep working on a plane.
    /// </summary>
    public sealed record LicenseCheck(
        bool Ok,
        string Message,
        bool Reachable,
        string? Email = null,
        string? InstanceId = null,
        string? Status = null,
        DateTimeOffset? ExpiresUtc = null,
        int? ActivationLimit = null,
        int? ActivationUsage = null);

    // ---- Activation: claims a seat against the key and returns the instance id we must keep. ----

    public static async Task<LicenseCheck> ActivateDetailedAsync(string key, string? instanceName = null)
        => await PostAsync(ApiUrl, "activated", new Dictionary<string, string>
        {
            ["license_key"] = key,
            ["instance_name"] = string.IsNullOrWhiteSpace(instanceName) ? Environment.MachineName : instanceName,
        });

    // Existing 4-tuple shape, kept so callers and the test suite are unaffected.
    public static async Task<(bool ok, string message, string? email, string? instanceId)> ActivateAsync(string key)
    {
        var r = await ActivateDetailedAsync(key);
        return (r.Ok, r.Message, r.Email, r.InstanceId);
    }

    // ---- Validation: re-checks a key we already activated. This is what catches a refund. ----
    //
    // A key can go bad long after activation: the customer charges back, we refund them, or the
    // order is disabled. Activation alone can never notice — it happened once, months ago. Without
    // a periodic validate, a refunded customer keeps Pro forever.

    public static async Task<LicenseCheck> ValidateAsync(string key, string? instanceId = null)
    {
        var form = new Dictionary<string, string> { ["license_key"] = key };
        if (!string.IsNullOrWhiteSpace(instanceId)) form["instance_id"] = instanceId!;
        return await PostAsync(ValidateApiUrl, "valid", form);
    }

    // ---- Deactivation: releases the seat so the customer can use the key on another machine. ----

    public static async Task<LicenseCheck> DeactivateDetailedAsync(string key, string instanceId)
        => await PostAsync(DeactivateApiUrl, "deactivated", new Dictionary<string, string>
        {
            ["license_key"] = key,
            ["instance_id"] = instanceId,
        });

    // Existing 2-tuple shape, kept for the same reason as above.
    public static async Task<(bool ok, string message)> DeactivateAsync(string key, string instanceId)
    {
        var r = await DeactivateDetailedAsync(key, instanceId);
        return (r.Ok, r.Message);
    }

    // All three endpoints share a shape: POST form-encoded, JSON back, one boolean flag naming the
    // outcome ("activated" / "valid" / "deactivated") plus an "error" string and license_key/meta
    // objects. Only the flag name differs, so parsing lives here once.
    private static async Task<LicenseCheck> PostAsync(string url, string okFlag, Dictionary<string, string> form)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

            bool ok = json.TryGetProperty(okFlag, out var flag) && flag.ValueKind == JsonValueKind.True;
            var error = json.TryGetProperty("error", out var e) ? e.GetString() : null;

            string? email = null, instance = null, status = null;
            DateTimeOffset? expires = null;
            int? limit = null, usage = null;

            if (json.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("customer_email", out var ce))
            {
                email = ce.GetString();
            }

            if (json.TryGetProperty("instance", out var inst) && inst.ValueKind == JsonValueKind.Object
                && inst.TryGetProperty("id", out var id))
            {
                instance = id.GetString();
            }

            if (json.TryGetProperty("license_key", out var lk) && lk.ValueKind == JsonValueKind.Object)
            {
                if (lk.TryGetProperty("status", out var s)) status = s.GetString();
                if (lk.TryGetProperty("expires_at", out var x) && x.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(x.GetString(), out var parsed))
                {
                    expires = parsed;
                }
                if (lk.TryGetProperty("activation_limit", out var al) && al.ValueKind == JsonValueKind.Number) limit = al.GetInt32();
                if (lk.TryGetProperty("activation_usage", out var au) && au.ValueKind == JsonValueKind.Number) usage = au.GetInt32();
            }

            // We reached Lemon Squeezy and got a JSON answer, so whatever it says is authoritative —
            // including "no". Reachable is true even on a rejection; it is only false when the call
            // itself failed.
            return new LicenseCheck(
                ok,
                ok ? Outcome(okFlag) : (error ?? "Activation failed. Check the key or your connection."),
                Reachable: true,
                Email: email, InstanceId: instance, Status: status,
                ExpiresUtc: expires, ActivationLimit: limit, ActivationUsage: usage);
        }
        catch (Exception ex)
        {
            // Network down, DNS, timeout, LS outage, a proxy returning HTML. We did not get an
            // answer, so we must not treat this as "the key is bad" — the caller decides what an
            // unreachable check means, and for a customer who already activated it means "carry on".
            return new LicenseCheck(false, ex.Message, Reachable: false);
        }
    }

    private static string Outcome(string okFlag) => okFlag switch
    {
        "activated" => "Activated.",
        "deactivated" => "Deactivated.",
        _ => "Valid.",
    };
}
