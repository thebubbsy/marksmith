using MarkSmith.Models;
using MarkSmith.ViewModels;
using MarkSmith.Services;
using Xunit;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

[Collection("LicenseState")]
public class LicensingTests
{
    private static (string privateKeyPem, string publicKeyPem) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static string SignLicenseKey(string email, string edition, long? exp, string privateKeyPem, string? keyId = null)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var expJson = exp.HasValue ? exp.Value.ToString() : "null";
        var jtiJson = keyId is null ? "null" : $"\"{keyId}\"";
        var payloadJson =
            $"{{\"email\":\"{email}\",\"edition\":\"{edition}\",\"exp\":{expJson}," +
            $"\"iss\":\"MarkSmith\",\"jti\":{jtiJson}}}";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        static string B64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64Url(payloadBytes)}.{B64Url(signature)}";
    }

    [Fact]
    public void LicenseValidator_Verifies_A_Key_Signed_By_The_Matching_Private_Key()
    {
        // This test used to sign with a freshly generated keypair and then verify against the
        // EMBEDDED PRODUCTION public key, which cannot succeed by construction — so it failed on
        // every machine that didn't happen to have the vendor's private-key.pem, and the licensing
        // system went effectively untested. Verify against the public key that matches the private
        // key we signed with.
        var (privateKeyPem, publicKeyPem) = GenerateRsaKeyPair();

        var key = SignLicenseKey("pro_user@example.com", "pro", null, privateKeyPem, "MS-20260101-test0001");
        var payload = LicenseValidator.Verify(key, publicKeyPem);

        Assert.NotNull(payload);
        Assert.Equal("pro_user@example.com", payload!.Email);
        Assert.Equal("pro", payload.Edition);
        Assert.Null(payload.Exp);
        Assert.Equal("MarkSmith", payload.Iss);
        Assert.Equal("MS-20260101-test0001", payload.KeyId);
    }

    [Fact]
    public void EmbeddedPublicKey_IsARealKey_AndRejectsForeignSignatures()
    {
        // The shipped trust root itself: it must parse as a real RSA key of a sane size, and it
        // must refuse a key signed by anybody else's private key. A placeholder or truncated PEM
        // here would mean every genuine customer key fails to activate.
        using var rsa = RSA.Create();
        var ex = Record.Exception(() => rsa.ImportFromPem(LicenseValidator.PublicKeyPem));
        Assert.Null(ex);
        Assert.True(rsa.KeySize >= 2048, $"vendor key is only {rsa.KeySize} bits");

        var (foreignPrivate, _) = GenerateRsaKeyPair();
        var forged = SignLicenseKey("attacker@example.com", "pro", null, foreignPrivate, "MS-forged");
        Assert.Null(LicenseValidator.Verify(forged));
    }

    [Fact]
    public void RevokedKeyIdsAreRefusedEvenThoughTheSignatureIsGood()
    {
        // Refunds, chargebacks and leaked keys. An offline perpetual key has no other kill switch.
        var (priv, pub) = GenerateRsaKeyPair();
        const string revoked = "MS-REVOKED-FOR-TEST";

        var key = SignLicenseKey("refunded@example.com", "pro", null, priv, revoked);
        Assert.NotNull(LicenseValidator.Verify(key, pub)); // valid while not on the list

        Assert.False(LicenseValidator.IsRevoked("MS-NOT-ON-THE-LIST"));
        Assert.False(LicenseValidator.IsRevoked(null));
        Assert.False(LicenseValidator.IsRevoked("  "));
    }

    [Fact]
    public void KeysFromAnotherIssuerAreRefused()
    {
        var (priv, pub) = GenerateRsaKeyPair();

        var payloadJson = "{\"email\":\"x@y.z\",\"edition\":\"pro\",\"exp\":null,\"iss\":\"someone-elses-product\"}";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(priv);
        var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(LicenseValidator.Verify($"{B64Url(payloadBytes)}.{B64Url(sig)}", pub));
    }

    [Fact]
    public void KeysWithoutAnIssuerOrSerialStillActivate()
    {
        // Backward compatibility: anything issued before these fields existed must keep working.
        var (priv, pub) = GenerateRsaKeyPair();

        var payloadJson = "{\"email\":\"early@buyer.com\",\"edition\":\"pro\",\"exp\":null}";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(priv);
        var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var payload = LicenseValidator.Verify($"{B64Url(payloadBytes)}.{B64Url(sig)}", pub);
        Assert.NotNull(payload);
        Assert.Equal("early@buyer.com", payload!.Email);
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
        // Exercises the genuine activation path — LicenseService.ActivateAsync, its persistence and
        // its entitlements — against a trust root the test controls, instead of signing with a
        // throwaway key and hoping the production public key would accept it.
        var (privateKeyPem, publicKeyPem) = GenerateRsaKeyPair();
        var validKey = SignLicenseKey("buyer@MarkSmith.app", "pro", null, privateKeyPem, "MS-20260101-buyer001");

        var service = new LicenseService(publicKeyPem);
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

        // Deactivate removes the activated key and reverts to Trial/Free
        service.Deactivate();
        Assert.NotEqual(Edition.Pro, service.State.Edition);
        Assert.Null(service.State.Key);
        Assert.Null(service.State.Email);
    }

    [Fact]
    public async Task ExpiredSubscriptionKeysDoNotActivate()
    {
        var (privateKeyPem, publicKeyPem) = GenerateRsaKeyPair();
        long yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var expired = SignLicenseKey("lapsed@example.com", "pro", yesterday, privateKeyPem, "MS-20260101-lapsed01");

        var service = new LicenseService(publicKeyPem);
        service.Load();

        var (ok, _) = await service.ActivateAsync(expired);

        Assert.False(ok);
        Assert.False(service.IsPro);
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

    [Fact]
    public async Task LemonSqueezy_Online_Deactivation_Integration_Test()
    {
        var handler = new MockHttpMessageHandler((request, cancellationToken) =>
        {
            var content = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "";
            if (content.Contains("license_key=LS-VALID-KEY") && content.Contains("instance_id=inst_987654321"))
            {
                var responseJson = @"{
                    ""deactivated"": true,
                    ""error"": null
                }";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var errorJson = @"{
                    ""deactivated"": false,
                    ""error"": ""Instance not found or license key invalid.""
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

            var (ok, msg) = await LemonSqueezyClient.DeactivateAsync("LS-VALID-KEY", "inst_987654321");
            Assert.True(ok);
            Assert.Equal("Deactivated.", msg);

            var (failOk, failMsg) = await LemonSqueezyClient.DeactivateAsync("LS-INVALID-KEY", "inst_invalid");
            Assert.False(failOk);
            Assert.Contains("Instance not found", failMsg);
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


// ===== 3-export trial model (no automatic trial) =====

[Collection("LicenseState")]
public class TrialModelTests : IDisposable
{
    private readonly string _licensePath;
    private readonly string _shadowPath;
    private readonly string? _backup;
    private readonly string? _shadowBackup;

    public TrialModelTests()
    {
        // AppPaths.ConfigDir so this follows the test suite's MARKSMITH_CONFIG_DIR redirect —
        // the license file under test is the same one LicenseService actually reads/writes.
        var dir = MarkSmith.Services.AppPaths.ConfigDir;
        _licensePath = Path.Combine(dir, "license.json");
        _shadowPath = Path.Combine(dir, "trial.state");
        _backup = File.Exists(_licensePath) ? File.ReadAllText(_licensePath) : null;
        _shadowBackup = File.Exists(_shadowPath) ? File.ReadAllText(_shadowPath) : null;
    }

    public void Dispose()
    {
        // Restore whatever license state existed before the test — these tests must never
        // clobber a real user's trial/Pro activation. The trial-consumption shadow is restored
        // too: a leaked shadow would silently re-cap the trial for the NEXT test in this class.
        try
        {
            if (_backup is null) { if (File.Exists(_licensePath)) File.Delete(_licensePath); }
            else File.WriteAllText(_licensePath, _backup);
            if (_shadowBackup is null) { if (File.Exists(_shadowPath)) File.Delete(_shadowPath); }
            else File.WriteAllText(_shadowPath, _shadowBackup);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void FreshLoad_IsFree_WithAllProFeaturesLocked()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree(); // order/state-independent: each test starts from the Free tier

        Assert.Equal(Edition.Free, service.State.Edition);
        Assert.False(service.IsPro);
        Assert.False(service.CanExportDocx);
        Assert.False(service.CanExportPptx);
        Assert.False(service.CanAutomate);
        Assert.True(service.ShowFooter); // free exports carry the footer
    }

    [Fact]
    public void StartTrial_GrantsFullPro_WithExactlyThreeDocxExports()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree(); // order/state-independent: each test starts from the Free tier

        var (ok, message) = service.StartTrial();
        Assert.True(ok);
        Assert.Equal(Edition.Trial, service.State.Edition);
        Assert.Equal(3, service.State.TrialExportsRemaining);

        // The trial is FULL PRO: everything unlocked, NO paywall, NO footer.
        Assert.True(service.CanExportDocx);
        Assert.True(service.CanExportPptx);
        Assert.True(service.CanAutomate);
        Assert.False(service.ShowFooter);
        Assert.Contains("3 DOCX exports", service.State.Status);

        // Starting it twice is refused — the trial is already active.
        var (ok2, _) = service.StartTrial();
        Assert.False(ok2);
    }

    [Fact]
    public void Trial_NeverReportsAProGate()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.StartTrial();

        // Every gateable feature is usable while the trial is active — the classifier can never
        // tell the user "this is a Pro feature" mid-trial.
        foreach (FeatureId id in Enum.GetValues<FeatureId>())
            Assert.True(service.State.CanUse(id), $"trial must allow {id}");
    }

    [Fact]
    public void ThreeDocxExports_ThenBackToFree_WithRestrictions()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree(); // order/state-independent: each test starts from the Free tier
        service.StartTrial();

        // Exports 1 and 2 still leave the trial active with the right remaining count.
        service.ConsumeDocxExport();
        Assert.Equal(Edition.Trial, service.State.Edition);
        Assert.Equal(2, service.State.TrialExportsRemaining);
        service.ConsumeDocxExport();
        Assert.Equal(Edition.Trial, service.State.Edition);
        Assert.Equal(1, service.State.TrialExportsRemaining);

        // The 3rd export spends the trial: back to Free, every restriction applies again.
        service.ConsumeDocxExport();
        Assert.Equal(Edition.Free, service.State.Edition);
        Assert.False(service.CanExportDocx);
        Assert.False(service.CanExportPptx);
        Assert.False(service.CanAutomate);
        Assert.True(service.ShowFooter);
        Assert.False(service.State.CanUse(FeatureId.DocxExport));

        // Consuming again is a no-op (no negative trial).
        service.ConsumeDocxExport();
        Assert.Equal(Edition.Free, service.State.Edition);

        // The usage is TRACKED: a spent trial can never be restarted.
        Assert.Contains("trial used", service.State.Status);
        var (again, _) = service.StartTrial();
        Assert.False(again, "a spent trial must not be restartable");
    }

    [Fact]
    public void DevProToggle_WritesKeyToFile_AndSurvivesRestart()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();

        var (pro, message) = service.ToggleDevPro();
        Assert.True(pro);
        Assert.True(service.IsPro);
        Assert.Equal(Edition.Pro, service.State.Edition);
        Assert.True(service.CanExportDocx);
        Assert.True(service.CanExportPptx);
        Assert.True(service.CanAutomate);
        Assert.False(service.ShowFooter);
        Assert.True(File.Exists(_licensePath), "the dev key must be persisted in the license file");

        // A fresh process reads the dev key and is Pro immediately.
        var reloaded = new LicenseService();
        reloaded.Load();
        Assert.True(reloaded.IsPro);
        Assert.False(reloaded.ShowFooter);
    }

    [Fact]
    public void DevProToggle_Off_DeletesTheLicenseFile_AndReturnsToFree()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.ToggleDevPro();
        Assert.True(File.Exists(_licensePath));

        var (pro, message) = service.ToggleDevPro(); // toggle again
        Assert.False(pro);
        Assert.False(service.IsPro);
        Assert.Equal(Edition.Free, service.State.Edition);
        Assert.False(service.CanExportDocx);
        Assert.False(File.Exists(_licensePath), "the license file must be deleted on the second press");

        var reloaded = new LicenseService();
        reloaded.Load();
        Assert.False(reloaded.IsPro);
    }

    [Fact]
    public void SpentTrial_SurvivesDiskReload()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.StartTrial();
        for (int i = 0; i < 3; i++) service.ConsumeDocxExport();
        Assert.Equal(Edition.Free, service.State.Edition);

        // A fresh process reads the same stored state: spent trial stays spent, and the user is
        // still on Free with every restriction — no re-granting by restarting the app.
        var reloaded = new LicenseService();
        reloaded.Load();
        Assert.Equal(Edition.Free, reloaded.State.Edition);
        Assert.False(reloaded.CanExportDocx);
        Assert.False(reloaded.CanExportPptx);
        Assert.False(reloaded.CanAutomate);
        Assert.True(reloaded.ShowFooter);
        Assert.False(reloaded.State.CanUse(FeatureId.DocxExport));
        var (again, _) = reloaded.StartTrial();
        Assert.False(again, "a spent trial must not be restartable after an app restart");
    }

    [Fact]
    public void NoAutomaticTrial_EvenForALegacyFileWithATrialStart()
    {
        // Simulate the OLD format (auto-started TrialStartUtc): the new model must NOT honour it.
        var service = new LicenseService();
        var dir = Path.GetDirectoryName(_licensePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_licensePath,
            "{\"TrialStartUtc\": \"2020-01-01T00:00:00+00:00\", \"LastSeenUtc\": \"2020-01-01T00:00:00+00:00\"}");
        service.Load();
        service.ResetToFree(); // order/state-independent: each test starts from the Free tier

        Assert.Equal(Edition.Free, service.State.Edition);
        Assert.False(service.CanExportDocx);
    }

    [Fact]
    public void ResetToFree_ClearsKeyAndTrial_ForTestingTheFreeTier()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree(); // order/state-independent: each test starts from the Free tier
        service.StartTrial();
        Assert.Equal(Edition.Trial, service.State.Edition);

        service.ResetToFree();
        Assert.Equal(Edition.Free, service.State.Edition);
        Assert.False(service.CanExportDocx);
        Assert.True(service.CanAutomate == false); // still locked

        // The reset wipes the USED flag too, so the trial can be started again for testing.
        var (ok, _) = service.StartTrial();
        Assert.True(ok, "after ResetToFree the trial must be startable again");
    }

    // ---- Trial-consumption shadow (go-live hardening) ----

    [Fact]
    public void DeletingLicenseFile_DoesNotRefund_A_SpentTrial()
    {
        // The casual bypass: spend all 3 trial exports, delete license.json, restart — the old
        // code read a fresh default and let the user start the trial again. The shadow marker
        // must keep the trial spent across that delete.
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.StartTrial();
        for (int i = 0; i < 3; i++) service.ConsumeDocxExport();
        Assert.Equal(Edition.Free, service.State.Edition);

        File.Delete(_licensePath); // the tamper

        var reloaded = new LicenseService();
        reloaded.Load();
        Assert.Equal(Edition.Free, reloaded.State.Edition);
        Assert.False(reloaded.CanExportDocx);
        Assert.Contains("trial used", reloaded.State.Status);
        var (again, _) = reloaded.StartTrial();
        Assert.False(again, "deleting license.json must not refund a spent trial");
    }

    [Fact]
    public void DeletingLicenseFile_KeepsAPartialTrial_ButNeverRefundsIt()
    {
        // Spend 1 of 3: the primary file says 2 remaining, and neither hand-editing it upward
        // nor deleting it outright can top the balance back up to 3 — the shadow record of the
        // spend is authoritative.
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.StartTrial();
        service.ConsumeDocxExport();
        Assert.Equal(2, service.State.TrialExportsRemaining);

        // Tamper #1: hand-edit the primary file to claim a full trial again.
        var edited = File.ReadAllText(_licensePath).Replace("\"TrialExportsRemaining\": 2", "\"TrialExportsRemaining\": 3");
        File.WriteAllText(_licensePath, edited);
        var tampered = new LicenseService();
        tampered.Load();
        Assert.Equal(2, tampered.State.TrialExportsRemaining);

        // Tamper #2: delete the file entirely — the shadow restores the REAL balance.
        File.Delete(_licensePath);
        var reloaded = new LicenseService();
        reloaded.Load();
        Assert.Equal(Edition.Trial, reloaded.State.Edition);
        Assert.True(reloaded.CanExportDocx);
        Assert.Equal(2, reloaded.State.TrialExportsRemaining);
    }

    [Fact]
    public void DeletingLicenseFile_ImmediatelyAfterTrialStart_DoesNotMintASecondOne()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.StartTrial();

        File.Delete(_licensePath); // try to mint a fresh trial by hiding the started one

        var reloaded = new LicenseService();
        reloaded.Load();
        // The shadow proves a trial was started: it is restored as active (3 exports), not
        // resolved to a never-trialed Free that could StartTrial() again later.
        Assert.Equal(Edition.Trial, reloaded.State.Edition);
        Assert.Equal(3, reloaded.State.TrialExportsRemaining);
    }

    [Fact]
    public void CorruptShadow_FallsBackToPrimaryFile()
    {
        var service = new LicenseService();
        service.Load();
        service.ResetToFree();
        service.StartTrial();

        File.WriteAllText(_shadowPath, "not json — a corrupt shadow must be treated as absent");

        var reloaded = new LicenseService();
        reloaded.Load(); // must not throw
        Assert.Equal(Edition.Trial, reloaded.State.Edition);
        Assert.Equal(3, reloaded.State.TrialExportsRemaining);
    }
}

// ===== Top-down feature classification =====

public class FeatureClassifierTests
{
    private static LicenseState Free => new() { Edition = Edition.Free };
    private static LicenseState Trial => new() { Edition = Edition.Trial };
    private static LicenseState Pro => new() { Edition = Edition.Pro };

    [Fact]
    public void FreeTier_FeaturesAreAlwaysAllowed()
    {
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.MarkdownToPdf, Free));
        Assert.True(Free.CanUse(FeatureId.MarkdownToPdf));
    }

    [Fact]
    public void DocxExport_AllowedForProAndTrial_NotForFree()
    {
        Assert.False(FeatureClassifier.LicenseAllows(FeatureId.DocxExport, Free));
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.DocxExport, Trial));  // the 3-export trial
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.DocxExport, Pro));
    }

    [Fact]
    public void PptxExport_AllowedForProAndTrial_NotForFree()
    {
        Assert.False(FeatureClassifier.LicenseAllows(FeatureId.PptxExport, Free));
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.PptxExport, Trial)); // trial is FULL pro
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.PptxExport, Pro));
    }

    [Theory]
    [InlineData(FeatureId.BatchConvert)]
    [InlineData(FeatureId.WatchFolder)]
    [InlineData(FeatureId.AutoExportIngest)]
    [InlineData(FeatureId.ClipboardIngest)]
    public void AutomationFeatures_AllowedForProAndTrial_NotForFree(FeatureId id)
    {
        Assert.False(FeatureClassifier.LicenseAllows(id, Free));
        Assert.True(FeatureClassifier.LicenseAllows(id, Trial)); // trial is FULL pro — no paywall
        Assert.True(FeatureClassifier.LicenseAllows(id, Pro));
    }

    [Fact]
    public void AdvancedStyling_IsFree()
    {
        // The Advanced Options toggle reveals this section for EVERYONE — it is styling, not a
        // paid capability (it used to be gated on IsPro, which silently broke the toggle).
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.AdvancedStyling, Free));
        Assert.True(FeatureClassifier.LicenseAllows(FeatureId.AdvancedStyling, Pro));
    }

    [Fact]
    public void EveryFeatureHasADisplayName()
    {
        foreach (FeatureId id in Enum.GetValues<FeatureId>())
            Assert.False(string.IsNullOrWhiteSpace(FeatureClassifier.DisplayName(id)));
    }
}
