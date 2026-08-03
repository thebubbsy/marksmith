using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class LicensingTests
{
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

    [Fact]
    public void LicenseValidator_Verifies_Valid_Key_With_Embedded_PublicKey()
    {
        // Use the current embedded public key to verify a key signed by private-key.pem if available
        var curr = new DirectoryInfo(AppContext.BaseDirectory);
        string? privateKeyPath = null;
        while (curr != null)
        {
            var p = Path.Combine(curr.FullName, "tools", "licensing", "private-key.pem");
            if (File.Exists(p)) { privateKeyPath = p; break; }
            curr = curr.Parent;
        }

        string privateKeyPem;
        if (privateKeyPath != null)
        {
            privateKeyPem = File.ReadAllText(privateKeyPath);
        }
        else
        {
            var pair = GenerateRsaKeyPair();
            privateKeyPem = pair.privateKeyPem;
        }

        var key = SignLicenseKey("pro_user@example.com", "pro", null, privateKeyPem);
        var payload = LicenseValidator.Verify(key);

        Assert.NotNull(payload);
        Assert.Equal("pro_user@example.com", payload.Email);
        Assert.Equal("pro", payload.Edition);
        Assert.Null(payload.Exp);
        Assert.Equal("MarkSmith", payload.Iss);
    }

    [Fact]
    public void LicenseValidator_Rejects_Tampered_Key()
    {
        var (privKey, pubKey) = GenerateRsaKeyPair();
        var key = SignLicenseKey("user@domain.com", "pro", null, privKey);
        
        var parts = key.Split('.');
        var tamperedKey = parts[0] + "X." + parts[1]; // Tamper payload

        var payload = LicenseValidator.Verify(tamperedKey, pubKey);
        Assert.Null(payload);
    }

    [Fact]
    public void LicenseValidator_Rejects_Invalid_Format()
    {
        Assert.Null(LicenseValidator.Verify("invalid-key-without-dot"));
        Assert.Null(LicenseValidator.Verify(""));
        Assert.Null(LicenseValidator.Verify(null));
    }

    [Fact]
    public async Task LicenseService_Activates_Pro_Features_Successfully()
    {
        var curr = new DirectoryInfo(AppContext.BaseDirectory);
        string? privateKeyPath = null;
        while (curr != null)
        {
            var p = Path.Combine(curr.FullName, "tools", "licensing", "private-key.pem");
            if (File.Exists(p)) { privateKeyPath = p; break; }
            curr = curr.Parent;
        }

        string privateKeyPem;
        if (privateKeyPath != null)
        {
            privateKeyPem = File.ReadAllText(privateKeyPath);
        }
        else
        {
            var pair = GenerateRsaKeyPair();
            privateKeyPem = pair.privateKeyPem;
        }

        var validKey = SignLicenseKey("buyer@MarkSmith.app", "pro", null, privateKeyPem);

        var service = new LicenseService();
        service.Load();

        var (ok, message) = await service.ActivateAsync(validKey);

        Assert.True(ok, $"Activation failed: {message}");
        Assert.Contains("MarkSmith Pro is unlocked", message);
        Assert.True(service.IsPro);
        Assert.True(service.CanExportDocx);
        Assert.True(service.CanAutomate);
        Assert.False(service.ShowFooter);
        Assert.Equal(Edition.Pro, service.State.Edition);
        Assert.Equal("buyer@MarkSmith.app", service.State.Email);

        // Deactivate removes activated key and reverts back to Trial/Free
        service.Deactivate();
        Assert.NotEqual(Edition.Pro, service.State.Edition);
        Assert.Null(service.State.Key);
        Assert.Null(service.State.Email);
    }

    [Fact]
    public async Task LemonSqueezy_Online_Activation_Integration_Test()
    {
        // Mock HTTP response for Lemon Squeezy activation endpoint
        var handler = new MockHttpMessageHandler((request, cancellationToken) =>
        {
            var content = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "";
            if (content.Contains("license_key=LS-VALID-KEY"))
            {
                var responseJson = @"{
                    ""activated"": true,
                    ""error"": null,
                    ""meta"": { ""customer_email"": ""ls_buyer@example.com"" },
                    ""instance"": { ""id"": ""inst_987654321"" }
                }";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var errorJson = @"{
                    ""activated"": false,
                    ""error"": ""License key not found or expired.""
                }";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
                };
            }
        });

        var prevEnabled = LemonSqueezyClient.Enabled;
        var prevHttp = LemonSqueezyClient.Http;
        try
        {
            LemonSqueezyClient.Enabled = true;
            LemonSqueezyClient.Http = new HttpClient(handler);

            // Direct LemonSqueezyClient test
            var (ok, msg, email, instId) = await LemonSqueezyClient.ActivateAsync("LS-VALID-KEY");
            Assert.True(ok);
            Assert.Equal("ls_buyer@example.com", email);
            Assert.Equal("inst_987654321", instId);

            // LicenseService integration test via Lemon Squeezy fallback
            var service = new LicenseService();
            service.Load();
            service.Deactivate(); // Start clean

            var (svcOk, svcMsg) = await service.ActivateAsync("LS-VALID-KEY");
            Assert.True(svcOk, svcMsg);
            Assert.True(service.IsPro);
            Assert.True(service.CanExportDocx);
            Assert.True(service.CanAutomate);
            Assert.False(service.ShowFooter);
            Assert.Equal("ls_buyer@example.com", service.State.Email);

            // Test invalid key
            var (failOk, failMsg) = await service.ActivateAsync("LS-INVALID-KEY");
            Assert.False(failOk);
            Assert.Contains("not found", failMsg);
        }
        finally
        {
            LemonSqueezyClient.Enabled = prevEnabled;
            LemonSqueezyClient.Http = prevHttp;
        }
    }
}

internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

    public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request, cancellationToken));
    }
}

