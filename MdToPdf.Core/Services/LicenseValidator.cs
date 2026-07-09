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
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqbzQ0tZg6fFCbhN2xZ4X\n" +
        "Tya4qkRsnCx/aGuLY8E9MZuq4KH/SdN96dM8VggaDEV1z3Yf2yQlxSYA2cbSxd04\n" +
        "W7bjN3lul2J3cFhy7xR5HjWtBVTiLAYE5kA1MsEPbZLJBO5/hk+BZHk6CgOaiwDj\n" +
        "xtTFanuIvzIV72e6fyKJFjj3Nhu2rBHbPWmjF8tsCy+/7uv9G7Waky4f6CTS5dA7\n" +
        "nxW0Agv8eFIQqkw9fT7C/MYhLxFOKO9i5QOWTEAyHLB+ZC638Tk7gPAXGPGQzfs7\n" +
        "qXqSrfNmau19Dtz0Ypj9uSdXcVtx3BtT0wcYF5VOvq9SOO0mdAdYt1o0dxj54mmf\n" +
        "kQIDAQAB\n" +
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
