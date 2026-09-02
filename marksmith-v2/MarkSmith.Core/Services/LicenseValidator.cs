using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkSmith.Services;

// Offline verification of RSA-signed Marksmith license keys. A key is:
//     base64url(payloadJson) + "." + base64url(RSA-SHA256 signature)
// signed by the vendor's PRIVATE key. The app embeds only the PUBLIC key below, so keys validate
// with no network and cannot be forged. Generate your own keypair and sign keys with the scripts in
// tools/licensing/. (An online Lemon Squeezy path also exists — see LemonSqueezyClient.)
public static class LicenseValidator
{
    // Production vendor public key. The corresponding private key is stored securely outside this
    // repository at %USERPROFILE%\.marksmith-keys\private-key.pem — never commit it to version control.
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAvTtpzcuLc0jYT1pgEaLd\n" +
        "TEUyLSePjudpAIfsPnIkIq1uJ2bl5Wq7jUxDyVSYC/dI0PeRtH19aS4ym/XTtZCo\n" +
        "8xLAWPDrFQm5k2IywvTu6W69WCIC9j45QbAPA+pjxYoTRTAuxfUFNcv+7Qpo0Gem\n" +
        "2CR5zwsBGai+3en6YTEgDu0lH6QceAh2s6aQqoFVgyUziFIRRfhKlInQMOJEDHwy\n" +
        "GPJci74SJ6guyTrAWTQ55xH6SWBijuyJa2M/qM+n4Z9C6FYCaU+728Yf8Df7vKI4\n" +
        "7jzaJIAzOl3Ze3iRkBBpl5nICAmG3NEwF4cMm9+MDxbL+gUeHIpmmx1+pBi+dp9d\n" +
        "BQIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    public sealed record Payload(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("edition")] string? Edition,
        [property: JsonPropertyName("exp")] long? Exp,
        [property: JsonPropertyName("iss")] string? Iss,
        // Serial number of this individual key. Every issued key carries one so a specific key can
        // be revoked after a refund, a chargeback, or a public leak. Nullable only for keys issued
        // before serials existed — those can never be revoked, which is the whole reason to stamp
        // one on every key from the very first sale.
        [property: JsonPropertyName("jti")] string? KeyId = null);

    /// <summary>The only issuer this build trusts.</summary>
    public const string ExpectedIssuer = "marksmith";

    /// <summary>
    /// Serial numbers that no longer entitle anyone to Pro — refunds, chargebacks and keys posted
    /// publicly. Offline perpetual keys have no other kill switch: a revoked key stops working when
    /// the customer takes an app update, so add the serial here and ship a release.
    ///
    /// Format is whatever sign-license.ps1 stamps ("MS-yyyyMMdd-xxxxxxxx"). Matching is exact and
    /// case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> RevokedKeyIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // "MS-20260901-a1b2c3d4",   // example: refunded 2026-09-05
        };

    public static bool IsRevoked(string? keyId) =>
        !string.IsNullOrWhiteSpace(keyId) && RevokedKeyIds.Contains(keyId.Trim());

    // Returns the verified payload, or null if the key is missing, malformed, or the signature fails.
#if DEBUG
    // Hidden developer/verification key: lets the app be flipped straight into Pro for testing
    // and demos (MainWindow's Ctrl+Shift+Alt+P toggles it). DEBUG-ONLY: it is compiled OUT of
    // Release builds so no shipped binary carries a hardcoded free-Pro backdoor.
    public const string DevProKey = "MARKSMITH-DEV-PRO-0001";
#endif

    public static Payload? Verify(string? key, string? publicKeyPem = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
#if DEBUG
        if (string.Equals(key.Trim(), DevProKey, StringComparison.Ordinal))
            return new Payload("dev@marksmith.local", "pro", null, ExpectedIssuer, "MS-DEV-LOCAL");
#endif
        var parts = key.Trim().Split('.');
        if (parts.Length != 2) return null;
        try
        {
            var payloadBytes = FromB64Url(parts[0]);
            var sig = FromB64Url(parts[1]);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem ?? PublicKeyPem);
            if (!rsa.VerifyData(payloadBytes, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;

            var payload = JsonSerializer.Deserialize<Payload>(payloadBytes);
            if (payload is null) return null;

            // A signature only proves "somebody with a private key signed this". Checking the
            // issuer stops a key minted for a different product — or a future product of ours with
            // its own keypair — from unlocking this one. Keys predating the field are still
            // honoured so nothing already sold stops working.
            if (!string.IsNullOrWhiteSpace(payload.Iss) &&
                !string.Equals(payload.Iss.Trim(), ExpectedIssuer, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (IsRevoked(payload.KeyId)) return null;

            return payload;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
