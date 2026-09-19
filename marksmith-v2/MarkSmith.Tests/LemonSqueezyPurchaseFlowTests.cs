using System.Net;
using System.Text;
using System.Text.Json;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// The online purchase path: a Lemon Squeezy key is an opaque UUID that only their API can vouch
// for, so every one of these behaviours depends on a network answer we have to interpret
// correctly. The offline signed-key tests next door cover the other model.
//
// These exist because the failure modes here are all silent and all expensive: a refunded customer
// who keeps Pro forever, a customer on a plane who loses the product they paid for, a reinstall
// that burns an activation seat and eventually locks someone out of their own licence.
[Collection("LicenseState")]
public class LemonSqueezyPurchaseFlowTests
{
    private const string Key = "LS-VALID-KEY";

    // Routes by endpoint so one handler can answer activate, validate and deactivate differently
    // within a single test — which is the only way to exercise "activated fine, later refunded".
    private static MockHttpMessageHandler Router(
        Func<string, HttpResponseMessage>? activate = null,
        Func<string, HttpResponseMessage>? validate = null,
        Func<string, HttpResponseMessage>? deactivate = null)
        => new((request, ct) =>
        {
            var body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            var path = request.RequestUri?.AbsolutePath ?? "";
            var handler = path.EndsWith("/activate", StringComparison.Ordinal) ? activate
                        : path.EndsWith("/validate", StringComparison.Ordinal) ? validate
                        : deactivate;
            return handler?.Invoke(body) ?? Json("{}");
        });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Activated(string? expiresAt = null, string status = "active") =>
        Json($@"{{
            ""activated"": true, ""error"": null,
            ""meta"": {{ ""customer_email"": ""buyer@example.com"" }},
            ""instance"": {{ ""id"": ""inst_1"" }},
            ""license_key"": {{ ""status"": ""{status}"", ""expires_at"": {(expiresAt is null ? "null" : $"\"{expiresAt}\"")} }}
        }}");

    private static LicenseService FreshService()
    {
        var s = new LicenseService();
        s.Load();
        s.ResetToFree();
        return s;
    }

    private static async Task WithClient(MockHttpMessageHandler handler, Func<Task> body)
    {
        var prevEnabled = LemonSqueezyClient.Enabled;
        var prevHttp = LemonSqueezyClient.Http;
        try
        {
            LemonSqueezyClient.Enabled = true;
            LemonSqueezyClient.Http = new HttpClient(handler);
            await body();
        }
        finally
        {
            LemonSqueezyClient.Enabled = prevEnabled;
            LemonSqueezyClient.Http = prevHttp;
            new LicenseService().ResetToFree();
        }
    }

    [Fact]
    public async Task Activating_A_Store_Issued_Key_Unlocks_Pro_And_Survives_A_Restart()
    {
        await WithClient(Router(activate: _ => Activated()), async () =>
        {
            var service = FreshService();
            var (ok, msg) = await service.ActivateAsync(Key);

            Assert.True(ok, msg);
            Assert.True(service.IsPro);
            Assert.Equal("buyer@example.com", service.State.Email);

            // The whole point of persisting the activation: the customer does not reactivate every
            // launch, and must not need the network to open the app they bought.
            var restarted = new LicenseService();
            restarted.Load();
            Assert.True(restarted.IsPro);
        });
    }

    [Fact]
    public async Task An_Expired_Key_Does_Not_Unlock_Pro()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        await WithClient(Router(activate: _ => Activated(expiresAt: past)), async () =>
        {
            var service = FreshService();
            await service.ActivateAsync(Key);

            // Lemon Squeezy said "activated" — it will happily activate a key whose term has run
            // out — so the expiry has to be enforced here or a lapsed licence unlocks Pro forever.
            Assert.False(service.IsPro);
        });
    }

    [Fact]
    public async Task A_Refunded_Key_Loses_Pro_On_The_Next_Revalidation()
    {
        // Activation succeeds (the sale was real), then the customer charges back and the order is
        // disabled. Nothing local changes; only a fresh validate can reveal it.
        var handler = Router(
            activate: _ => Activated(),
            validate: _ => Json(@"{ ""valid"": false, ""error"": ""This license key has been disabled."",
                                    ""license_key"": { ""status"": ""disabled"" } }"));

        await WithClient(handler, async () =>
        {
            var service = FreshService();
            await service.ActivateAsync(Key);
            Assert.True(service.IsPro);

            var stillPro = await service.RevalidateAsync(force: true);

            Assert.False(stillPro);
            Assert.False(service.IsPro);
            Assert.False(service.CanExportDocx);
        });
    }

    [Fact]
    public async Task An_Unreachable_Server_Does_Not_Take_Pro_Away()
    {
        var reachable = true;
        var handler = Router(
            activate: _ => Activated(),
            validate: _ => reachable ? Json(@"{ ""valid"": true }") : throw new HttpRequestException("no network"));

        await WithClient(handler, async () =>
        {
            var service = FreshService();
            await service.ActivateAsync(Key);

            reachable = false;
            var stillPro = await service.RevalidateAsync(force: true);

            // "I could not ask" is not "the answer is no". A customer offline for an afternoon
            // keeps the product they paid for.
            Assert.True(stillPro);
            Assert.True(service.IsPro);
        });
    }

    [Fact]
    public async Task An_Activation_Stale_Beyond_The_Grace_Window_Stops_Counting()
    {
        await WithClient(Router(activate: _ => Activated()), async () =>
        {
            var service = FreshService();
            await service.ActivateAsync(Key);
            Assert.True(service.IsPro);

            // Age the stored activation past the grace window, as it would be on a machine that
            // has not successfully checked in for months — including one deliberately kept off the
            // network to dodge a revocation.
            var path = Path.Combine(AppPaths.ConfigDir, "license.json");
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
            var aged = new Dictionary<string, object?>();
            foreach (var kv in doc) aged[kv.Key] = kv.Value;
            aged["LastValidatedUtc"] = DateTimeOffset.UtcNow.AddDays(-(LicenseService.OfflineGraceDays + 1));
            File.WriteAllText(path, JsonSerializer.Serialize(aged));

            var restarted = new LicenseService();
            restarted.Load();

            Assert.False(restarted.IsPro);
        });
    }

    [Fact]
    public async Task Deactivating_Releases_The_Activation_Seat()
    {
        var deactivateCalled = false;
        var handler = Router(
            activate: _ => Activated(),
            deactivate: body =>
            {
                // The seat can only be released if we send back the instance id we were given at
                // activation — losing it is what silently burns a customer's machine allowance.
                Assert.Contains("instance_id=inst_1", body);
                deactivateCalled = true;
                return Json(@"{ ""deactivated"": true, ""error"": null }");
            });

        await WithClient(handler, async () =>
        {
            var service = FreshService();
            await service.ActivateAsync(Key);

            var (ok, _) = await service.DeactivateAsync();

            Assert.True(ok);
            Assert.True(deactivateCalled);
            Assert.False(service.IsPro);
        });
    }

    [Fact]
    public async Task A_Reinstall_That_Hits_The_Activation_Limit_Still_Unlocks_Pro()
    {
        // Reinstalling loses our instance id while Lemon Squeezy still holds the seat, so a plain
        // activate comes back at the limit. The key is genuinely good and the customer is entitled
        // to Pro; making them email support to get back into software they bought is a refund.
        var handler = Router(
            activate: _ => Json(@"{ ""activated"": false,
                                    ""error"": ""This license key has reached the activation limit."",
                                    ""license_key"": { ""status"": ""active"" } }"),
            validate: _ => Json(@"{ ""valid"": true, ""error"": null,
                                    ""meta"": { ""customer_email"": ""buyer@example.com"" },
                                    ""license_key"": { ""status"": ""active"", ""expires_at"": null } }"));

        await WithClient(handler, async () =>
        {
            var service = FreshService();
            var (ok, msg) = await service.ActivateAsync(Key);

            Assert.True(ok, msg);
            Assert.True(service.IsPro);
        });
    }

    [Fact]
    public async Task A_Key_That_Does_Not_Exist_Is_Rejected()
    {
        var handler = Router(
            activate: _ => Json(@"{ ""activated"": false, ""error"": ""license_key not found."" }"),
            validate: _ => Json(@"{ ""valid"": false, ""error"": ""license_key not found."" }"));

        await WithClient(handler, async () =>
        {
            var service = FreshService();
            var (ok, msg) = await service.ActivateAsync("LS-NOT-A-KEY");

            Assert.False(ok);
            Assert.Contains("not found", msg);
            Assert.False(service.IsPro);
        });
    }

    [Fact]
    public void Checkout_Url_Prefills_The_Buyers_Email()
    {
        // Lemon Squeezy emails the licence key to whatever address goes through checkout, so a
        // typo there sends the key somewhere unrecoverable.
        var url = LicenseService.CheckoutUrl("buyer+tag@example.com");

        Assert.Contains("checkout[email]=", url);
        // The + has to survive as %2B: unescaped it decodes to a space and the receipt goes nowhere.
        Assert.Contains("buyer%2Btag%40example.com", url);
        Assert.StartsWith(LicenseService.StoreUrl, url);
        Assert.Equal(LicenseService.StoreUrl, LicenseService.CheckoutUrl(null));
    }
}
