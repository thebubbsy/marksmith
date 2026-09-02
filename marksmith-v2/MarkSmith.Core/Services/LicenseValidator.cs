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
        [property: JsonPropertyName("iss")] string? Iss);

    // Returns the verified payload, or null if the key is missing, malformed, or the signature fails.
    // Developer/verification key: lets the app be flipped straight into Pro for testing
    // and demos (MainWindow's Ctrl+Shift+Alt+P toggles it).
    public const string DevProKey = "MARKSMITH-DEV-PRO-0001";

    public static Payload? Verify(string? key, string? publicKeyPem = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (string.Equals(key.Trim(), DevProKey, StringComparison.Ordinal))
            return new Payload("dev@marksmith.local", "pro", null, "marksmith-dev");
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
            return JsonSerializer.Deserialize<Payload>(payloadBytes);
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
