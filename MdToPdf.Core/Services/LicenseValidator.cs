using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdToPdf.Services;

// Offline verification of RSA-signed Marksmith license keys. A key is:
//     base64url(payloadJson) + "." + base64url(RSA-SHA256 signature)
// signed by the vendor's PRIVATE key. The app embeds only the PUBLIC key below, so keys validate
// with no network and cannot be forged. Generate your own keypair and sign keys with the scripts in
// tools/licensing/. (An online Lemon Squeezy path also exists — see LemonSqueezyClient.)
public static class LicenseValidator
{
    // Vendor public key. REPLACE with your own from tools/licensing/generate-keys.ps1 before selling.
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwV1ZGKpYGX0Ve9O7K1cL\n" +
        "vpv6UbLNtVk3W20V6XDILlWmBIkYjTVRs+XiN6/HUC66vqwNhxYMpOPViYJNic6n\n" +
        "/z5oZuJOWjyjd080HZGDH+pMrU4s2OmlYjXLu3YsnVMRhbugRrAncvcTqOfW3okC\n" +
        "u7ZPVz0WE8vNSfMXhqkYlk+O1OiWrcv3NgJ4N5Xk4B5TbkThTip7519uneXtH16S\n" +
        "bckB0ybs91FDUosjadCVw0sU/++A16k3vJWRz5AhmdZVxYXl1sUhAeSdoYpMPm7c\n" +
        "p7y7NVa3F1uiuPLmYG5i2rxolB0laCze6tuV8jgVpDNVKN8HpGstFO0xQqw+RwW0\n" +
        "KQIDAQAB\n" +
        "-----END PUBLIC KEY-----";

    public sealed record Payload(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("edition")] string? Edition,
        [property: JsonPropertyName("exp")] long? Exp,
        [property: JsonPropertyName("iss")] string? Iss);

    // Returns the verified payload, or null if the key is missing, malformed, or the signature fails.
    public static Payload? Verify(string? key, string? publicKeyPem = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
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
