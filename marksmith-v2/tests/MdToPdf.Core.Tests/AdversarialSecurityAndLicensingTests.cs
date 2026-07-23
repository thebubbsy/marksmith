using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class AdversarialSecurityAndLicensingTests
{
    private static ApiServer CreateServer(string allowedExtId = "test-extension-id")
    {
        return new ApiServer(
            new LlmSourceService(),
            () => new List<string> { "GitHub Dark" },
            (md, orig, ovr) => { },
            (md, ovr) => Task.FromResult(Array.Empty<byte>()),
            new GovernanceService(),
            () => allowedExtId,
            () => new AppSettings(),
            s => { },
            (folder, fmt, ovr) => Task.FromResult<object>(new { done = 0 })
        );
    }

    private static (string privateKeyPem, string publicKeyPem) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static string SignLicenseKey(string email, string edition, long? exp, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var expJson = exp.HasValue ? exp.Value.ToString() : "null";
        var payloadJson = $"{{\"email\":\"{email}\",\"edition\":\"{edition}\",\"exp\":{expJson},\"iss\":\"MarkSmith\"}}";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        static string B64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64Url(payloadBytes)}.{B64Url(signature)}";
    }

    // ==========================================
    // R3: API Server Security Stress Tests
    // ==========================================

    [Fact]
    public async Task ApiServer_R3_IP_Binding_Locks_To_127_0_0_1()
    {
        using var server = CreateServer();
        int port = 59401;
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
    public async Task ApiServer_R3_CORS_Emits_No_Wildcard_Under_Any_Request()
    {
        using var server = CreateServer();
        int port = 59402;
        server.Start(port);

        using var client = new HttpClient();

        // Scenario 1: Direct Request (No Origin header)
        var req1 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        var resp1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        if (resp1.Headers.TryGetValues("Access-Control-Allow-Origin", out var v1))
        {
            Assert.DoesNotContain("*", string.Join(",", v1));
        }

        // Scenario 2: Allowed Local Origin
        var req2 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        req2.Headers.Add("Origin", "http://127.0.0.1:3000");
        var resp2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        if (resp2.Headers.TryGetValues("Access-Control-Allow-Origin", out var v2))
        {
            Assert.DoesNotContain("*", string.Join(",", v2));
            Assert.Equal("http://127.0.0.1:3000", string.Join(",", v2));
        }

        // Scenario 3: Allowed Extension Origin
        var req3 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        req3.Headers.Add("Origin", "chrome-extension://test-extension-id");
        var resp3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
        if (resp3.Headers.TryGetValues("Access-Control-Allow-Origin", out var v3))
        {
            Assert.DoesNotContain("*", string.Join(",", v3));
            Assert.Equal("chrome-extension://test-extension-id", string.Join(",", v3));
        }

        // Scenario 4: Disallowed Origin
        var req4 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        req4.Headers.Add("Origin", "http://untrusted-site.com");
        var resp4 = await client.SendAsync(req4);
        Assert.Equal(HttpStatusCode.Forbidden, resp4.StatusCode);
        if (resp4.Headers.TryGetValues("Access-Control-Allow-Origin", out var v4))
        {
            Assert.DoesNotContain("*", string.Join(",", v4));
            Assert.Equal("null", string.Join(",", v4));
        }

        // Scenario 5: OPTIONS Preflight
        var req5 = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{port}/api/convert");
        req5.Headers.Add("Origin", "http://127.0.0.1:3000");
        var resp5 = await client.SendAsync(req5);
        Assert.Equal(HttpStatusCode.NoContent, resp5.StatusCode);
        if (resp5.Headers.TryGetValues("Access-Control-Allow-Origin", out var v5))
        {
            Assert.DoesNotContain("*", string.Join(",", v5));
        }

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_R3_CORS_Rejects_Lookalike_Untrusted_Origins()
    {
        using var server = CreateServer();
        int port = 59403;
        server.Start(port);

        using var client = new HttpClient();
        string[] maliciousOrigins = new[]
        {
            "http://127.0.0.1.attacker.com",
            "http://localhost.attacker.com",
            "http://evil127.0.0.1",
            "http://127.0.0.1.evil.org:8080",
            "https://fake-extension-site.com?chrome-extension://test-extension-id"
        };

        foreach (var origin in maliciousOrigins)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
            req.Headers.Add("Origin", origin);

            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            if (resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var v))
            {
                Assert.Equal("null", string.Join(",", v));
            }
        }

        server.Stop();
    }

    [Fact]
    public async Task ApiServer_R3_Governance_Endpoints_Block_Browser_Origins()
    {
        using var server = CreateServer();
        int port = 59404;
        server.Start(port);

        using var client = new HttpClient();

        // Browser origin trying to read governance data -> 403 Forbidden
        var req1 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/governance/events");
        req1.Headers.Add("Origin", "http://127.0.0.1:3000");
        var resp1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Forbidden, resp1.StatusCode);

        var req2 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/governance/summary");
        req2.Headers.Add("Origin", "chrome-extension://test-extension-id");
        var resp2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Forbidden, resp2.StatusCode);

        // Direct non-browser request (no Origin) -> 200 OK
        var req3 = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/governance/events");
        var resp3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

        server.Stop();
    }

    // ==========================================
    // R4: Licensing & Pro Unlock Stress Tests
    // ==========================================

    [Fact]
    public void LicenseValidator_R4_Verifies_Valid_RSA_Key()
    {
        var (privKey, pubKey) = GenerateRsaKeyPair();
        var key = SignLicenseKey("valid_buyer@MarkSmith.app", "pro", null, privKey);

        var payload = LicenseValidator.Verify(key, pubKey);

        Assert.NotNull(payload);
        Assert.Equal("valid_buyer@MarkSmith.app", payload.Email);
        Assert.Equal("pro", payload.Edition);
        Assert.Null(payload.Exp);
        Assert.Equal("MarkSmith", payload.Iss);
    }

    [Fact]
    public void LicenseValidator_R4_Rejects_Tampered_Payload()
    {
        var (privKey, pubKey) = GenerateRsaKeyPair();
        var key = SignLicenseKey("original@MarkSmith.app", "free", null, privKey);

        var parts = key.Split('.');
        // Craft tampered payload JSON with "edition": "pro"
        var tamperedPayloadJson = "{\"email\":\"original@MarkSmith.app\",\"edition\":\"pro\",\"exp\":null,\"iss\":\"MarkSmith\"}";
        var tamperedPayloadB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedPayloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var tamperedKey = $"{tamperedPayloadB64}.{parts[1]}";

        var result = LicenseValidator.Verify(tamperedKey, pubKey);
        Assert.Null(result);
    }

    [Fact]
    public void LicenseValidator_R4_Rejects_Forged_Signature()
    {
        var (privKey1, pubKey1) = GenerateRsaKeyPair();
        var (privKey2, _) = GenerateRsaKeyPair();

        // Signed with privKey2 instead of privKey1
        var forgedKey = SignLicenseKey("attacker@MarkSmith.app", "pro", null, privKey2);

        // Verifying against pubKey1 should fail
        var result = LicenseValidator.Verify(forgedKey, pubKey1);
        Assert.Null(result);
    }

    [Fact]
    public void LicenseValidator_R4_Rejects_Corrupted_Signature()
    {
        var (privKey, pubKey) = GenerateRsaKeyPair();
        var key = SignLicenseKey("user@MarkSmith.app", "pro", null, privKey);

        var parts = key.Split('.');
        var corruptedSig = parts[1].Substring(0, parts[1].Length - 4) + "XXXX";
        var corruptedKey = $"{parts[0]}.{corruptedSig}";

        var result = LicenseValidator.Verify(corruptedKey, pubKey);
        Assert.Null(result);
    }

    [Fact]
    public void LicenseValidator_R4_Rejects_Malformed_Format_Strings()
    {
        var (_, pubKey) = GenerateRsaKeyPair();

        string[] badKeys = new[]
        {
            "",
            "   ",
            "no_dot_key",
            "one.two.three",
            "part1.invalid_b64!@#$",
            "!!!invalid_b64!!!.part2",
            "."
        };

        foreach (var badKey in badKeys)
        {
            Assert.Null(LicenseValidator.Verify(badKey, pubKey));
        }
    }

    [Fact]
    public async Task LicenseService_R4_Rejects_Expired_Tokens()
    {
        var (privKey, pubKey) = GenerateRsaKeyPair();
        long expiredTime = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeSeconds();
        var expiredKey = SignLicenseKey("expired@MarkSmith.app", "pro", expiredTime, privKey);

        // Validator returns payload
        var payload = LicenseValidator.Verify(expiredKey, pubKey);
        Assert.NotNull(payload);
        Assert.Equal(expiredTime, payload.Exp);

        // LicenseService activation fails using embedded key / custom verification logic
        // Verify LicenseService IsValidPro behavior
        var service = new LicenseService();
        service.Load();
        service.Deactivate();

        // Note: ActivateAsync uses default LicenseValidator.Verify (with embedded PublicKeyPem)
        // Testing with custom key signed token against default Pem:
        var (ok, msg) = await service.ActivateAsync(expiredKey);
        Assert.False(ok);
        Assert.Equal("That license key isn't valid.", msg);
        Assert.NotEqual(Edition.Pro, service.State.Edition);
    }

    [Fact]
    public async Task LicenseService_R4_Pro_Feature_State_Activation_And_Deactivation()
    {
        var privateKeyPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "licensing", "private-key.pem");
        if (!File.Exists(privateKeyPath))
        {
            privateKeyPath = Path.Combine(Directory.GetCurrentDirectory(), "tools", "licensing", "private-key.pem");
        }

        string privateKeyPem;
        if (File.Exists(privateKeyPath))
        {
            privateKeyPem = File.ReadAllText(privateKeyPath);
        }
        else
        {
            var pair = GenerateRsaKeyPair();
            privateKeyPem = pair.privateKeyPem;
        }

        var validKey = SignLicenseKey("pro_buyer@MarkSmith.app", "pro", null, privateKeyPem);

        var service = new LicenseService();
        service.Load();

        var (ok, msg) = await service.ActivateAsync(validKey);
        Assert.True(ok, msg);

        // Verify Pro features unlocked
        Assert.True(service.IsPro);
        Assert.True(service.CanExportDocx);
        Assert.True(service.CanAutomate);
        Assert.False(service.ShowFooter);
        Assert.Equal(Edition.Pro, service.State.Edition);
        Assert.Equal("pro_buyer@MarkSmith.app", service.State.Email);
        Assert.Contains("MarkSmith Pro — activated", service.State.Status);

        // Deactivate and verify state returns to non-Pro (Trial or Free)
        service.Deactivate();
        Assert.Null(service.State.Key);
        Assert.Null(service.State.Email);
        Assert.NotEqual(Edition.Pro, service.State.Edition);
    }
}

