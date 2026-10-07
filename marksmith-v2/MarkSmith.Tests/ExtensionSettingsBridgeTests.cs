using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// The browser extension's "Live app settings" (GET/POST /api/extension/settings) and its email
/// endpoint (POST /api/email). Applies run against a stand-in with MainViewModel's property names,
/// so they never write the shared settings other test classes read in parallel; one test pins that
/// the real view model has every property the bridge lists.
/// </summary>
[Collection("LicenseState")]
public class ExtensionSettingsBridgeTests : IDisposable
{
    public void Dispose() => AppServices.License.ResetToFree();

    // Same names and types as MainViewModel's bindable properties.
    private sealed class FakeVm
    {
        public string SelectedThemeName { get; set; } = "GitHub Dark";
        public bool ThemeLightInfluence { get; set; }
        public string FontPreset { get; set; } = "System";
        public bool A4FixedWidth { get; set; }
        public int ContentWidth { get; set; } = 800;
        public bool UnlimitedHeight { get; set; }
        public bool IncludeToc { get; set; }
        public bool ShowWordCount { get; set; }
        public string PdfPageNumberPosition { get; set; } = "None";
        public string PdfHeaderTemplate { get; set; } = "";
        public string PdfFooterTemplate { get; set; } = "";
        public bool PageBorder { get; set; }
        public bool TrackChanges { get; set; }
        public int MermaidDocxMode { get; set; } = 1;
        public bool SmartConnectors { get; set; } = true;
        public string ConnectorArrowhead { get; set; } = "default";
        public string EmailTo { get; set; } = "";
        public string EmailCc { get; set; } = "";
        public string EmailSubjectTemplate { get; set; } = "{title}";
        public bool EmailRepeatTitleInBody { get; set; }
        public bool EmailAttachPdf { get; set; }
        public bool EmailAttachDocx { get; set; }
        public string EmailImportHistory { get; set; } = "collapse";
        public bool MermaidEnabled { get; set; } = true;
        public bool NormalizeLlm { get; set; }
        public bool ShowAttribution { get; set; }
        public bool NoEmoji { get; set; }
        public int DashMode { get; set; }
        public string DashCustom { get; set; } = "";
        public int HeadingShift { get; set; }
        public int BoldMode { get; set; }
        public int ItalicMode { get; set; }
        public bool BrandCoverPage { get; set; }
        public string BrandFontFamily { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string TargetFormat { get; set; } = "pdf";
        public string FileNameTemplate { get; set; } = "{title}";
        // Gated like the real one: a Free licence can't switch it on.
        private bool _clip;
        public bool AutoClipboardIngest { get => _clip; set => _clip = value && AppServices.License.CanAutomate; }
        public bool AutoConvertIngests { get; set; }
        public bool AppendToRunningDoc { get; set; }
        public bool WatchFolderAutoConvert { get; set; }
        public bool MinimizeToTray { get; set; }
    }

    private static ExtensionSettingsBridge Bridge(FakeVm vm) =>
        new(vm, () => new[] { "GitHub Dark", "Dracula" });

    private static Dictionary<string, JsonElement> Changes(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Every_listed_option_is_a_real_view_model_property_of_the_right_type()
    {
        var bridge = new ExtensionSettingsBridge(new FakeVm(), () => new[] { "GitHub Dark" });
        foreach (var field in bridge.Groups().SelectMany(g => g.Fields))
        {
            var real = typeof(MainViewModel).GetProperty(field.Property, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(real is not null, $"MainViewModel has no {field.Property} (extension key {field.Key})");
            Assert.True(real!.CanWrite, field.Property);
            var fake = typeof(FakeVm).GetProperty(field.Property)!;
            Assert.Equal(real.PropertyType, fake.PropertyType);
        }
    }

    [Fact]
    public void Nothing_secret_or_path_like_is_exposed()
    {
        var bridge = Bridge(new FakeVm());
        var props = bridge.Groups().SelectMany(g => g.Fields).Select(f => f.Property).ToList();
        Assert.Equal(props.Count, props.Distinct().Count());
        foreach (var p in props)
            Assert.DoesNotMatch("(?i)password|token|secret|licen|(folder|path)$|^api", p);
    }

    [Fact]
    public async Task Describe_reports_current_values_and_licence()
    {
        AppServices.License.Load();
        AppServices.License.ResetToFree();
        var vm = new FakeVm { IncludeToc = true, DashMode = 2 };
        var json = JsonSerializer.Serialize(await Bridge(vm).DescribeAsync(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("key").GetString()!);
        Assert.True(fields["includeToc"].GetProperty("value").GetBoolean());
        Assert.Equal("2", fields["dashMode"].GetProperty("value").GetString());
        Assert.Equal(2, fields["theme"].GetProperty("choices").GetArrayLength());
        Assert.True(fields["autoClipboardIngest"].GetProperty("locked").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("license").GetProperty("canAutomate").GetBoolean());
    }

    [Fact]
    public async Task Valid_changes_reach_the_view_model()
    {
        var vm = new FakeVm();
        var result = await Bridge(vm).ApplyAsync(Changes(
            "{\"theme\":\"Dracula\",\"includeToc\":true,\"contentWidth\":1000,\"dashMode\":\"3\",\"dashCustom\":\" / \"," +
            "\"emailTo\":\"ann@example.com\",\"targetFormat\":\"docx\",\"mermaidDocxMode\":0}"));

        Assert.Empty(result.Rejected);
        Assert.Equal(8, result.Applied.Count);
        Assert.Equal("Dracula", vm.SelectedThemeName);
        Assert.True(vm.IncludeToc);
        Assert.Equal(1000, vm.ContentWidth);
        Assert.Equal(3, vm.DashMode);
        Assert.Equal(" / ", vm.DashCustom);
        Assert.Equal("ann@example.com", vm.EmailTo);
        Assert.Equal("docx", vm.TargetFormat);
        Assert.Equal(0, vm.MermaidDocxMode);
    }

    [Fact]
    public async Task Invalid_changes_are_refused_with_a_reason_and_change_nothing()
    {
        var vm = new FakeVm();
        var result = await Bridge(vm).ApplyAsync(Changes(
            "{\"contentWidth\":50,\"theme\":\"Nope\",\"includeToc\":\"yes\",\"watchFolder\":\"C:\\\\\",\"headingShift\":1.5,\"dashCustom\":\"a\\u0007\"}"));

        Assert.Empty(result.Applied);
        var reasons = result.Rejected.ToDictionary(r => r.Key, r => r.Reason);
        Assert.Equal("Page width (px) must be between 400 and 2400.", reasons["contentWidth"]);
        Assert.Contains("isn't one of the choices", reasons["theme"]);
        Assert.Equal("Table of contents must be on or off.", reasons["includeToc"]);
        Assert.Equal("Not a setting the extension can change.", reasons["watchFolder"]);
        Assert.Contains("whole number", reasons["headingShift"]);
        Assert.Contains("control characters", reasons["dashCustom"]);
        Assert.Equal(800, vm.ContentWidth);
        Assert.Equal("GitHub Dark", vm.SelectedThemeName);
    }

    [Fact]
    public async Task Automation_stays_a_Pro_feature_but_can_always_be_switched_off()
    {
        AppServices.License.Load();
        AppServices.License.ResetToFree();
        var vm = new FakeVm();
        var result = await Bridge(vm).ApplyAsync(Changes("{\"autoClipboardIngest\":true,\"autoConvertIngests\":false,\"minimizeToTray\":true}"));

        Assert.Contains(result.Rejected, r => r.Key == "autoClipboardIngest" && r.Reason.Contains("Pro feature"));
        Assert.Contains("autoConvertIngests", result.Applied);
        Assert.Contains("minimizeToTray", result.Applied);
        Assert.False(vm.AutoClipboardIngest);
    }

    // ── the endpoints ─────────────────────────────────────────────────────────

    private static ApiServer Server(Func<string, OutputOverride?, Task<byte[]>> convert) => new(
        new LlmSourceService(), () => new List<string> { "GitHub Dark" }, (md, o, ovr) => { }, convert,
        new GovernanceService(), () => "", () => new AppSettings(), s => { },
        (f, fmt, ovr) => Task.FromResult<object>(new { }));

    private static async Task<int> StartAsync(ApiServer server)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            try { server.Start(port); return port; }
            catch (HttpListenerException) when (attempt < 5) { await Task.Delay(50); }
        }
    }

    private static Task<HttpResponseMessage> Send(int port, HttpMethod method, string path, string? json, string? origin)
    {
        var client = new HttpClient();
        var req = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (origin is not null) req.Headers.Add("Origin", origin);
        return client.SendAsync(req);
    }

    [Fact]
    public async Task Settings_endpoint_serves_the_extension_and_refuses_web_pages()
    {
        var vm = new FakeVm();
        using var server = Server((md, o) => Task.FromResult(Array.Empty<byte>()));
        server.ExtensionSettings = Bridge(vm);
        var port = await StartAsync(server);
        try
        {
            var ext = "chrome-extension://abcdefghijklmnop";
            var get = await Send(port, HttpMethod.Get, "/api/extension/settings", null, ext);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Contains("\"groups\"", await get.Content.ReadAsStringAsync());

            var post = await Send(port, HttpMethod.Post, "/api/extension/settings", "{\"changes\":{\"noEmoji\":true,\"bogus\":1}}", ext);
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            var body = await post.Content.ReadAsStringAsync();
            Assert.Contains("\"applied\":[\"noEmoji\"]", body);
            Assert.Contains("\"key\":\"bogus\"", body);
            Assert.True(vm.NoEmoji);

            // A loopback-hosted page is allowed to use the API, but not to flip the user's settings.
            var page = await Send(port, HttpMethod.Post, "/api/extension/settings", "{\"changes\":{\"noEmoji\":false}}", $"http://127.0.0.1:{port}");
            Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
            Assert.True(vm.NoEmoji);

            var empty = await Send(port, HttpMethod.Post, "/api/extension/settings", "{}", ext);
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task Email_endpoint_opens_a_draft_or_returns_the_file_and_checks_addresses()
    {
        AppServices.License.Load();
        AppServices.License.ResetToFree(); // email is free: no 402 anywhere below
        OutputOverride? seen = null;
        var emlStub = Encoding.ASCII.GetBytes("Subject: Plan\r\nX-Unsent: 1\r\n\r\nbody");
        using var server = Server((md, o) => { seen = o; return Task.FromResult(emlStub); });
        string? openedFor = null;
        server.OpenEmailDraft = (md, o) =>
        {
            openedFor = md;
            seen = o;
            return Task.FromResult(new ApiServer.EmailDraftResult(true, @"C:\outbox\Plan.eml", "Plan", Array.Empty<string>()));
        };
        var port = await StartAsync(server);
        try
        {
            var ext = "chrome-extension://abcdefghijklmnop";
            var open = await Send(port, HttpMethod.Post, "/api/email",
                "{\"markdown\":\"# Plan\",\"output\":{\"emailTo\":\"ann@example.com\",\"emailSubject\":\"Q3\"}}", ext);
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
            Assert.Contains("\"opened\":true", await open.Content.ReadAsStringAsync());
            Assert.Equal("# Plan", openedFor);
            Assert.Equal("eml", seen!.Format);
            Assert.Equal("ann@example.com", seen.EmailTo);
            Assert.Equal("Q3", seen.EmailSubject);

            var file = await Send(port, HttpMethod.Post, "/api/email", "{\"markdown\":\"# Plan\",\"open\":false}", ext);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("message/rfc822", file.Content.Headers.ContentType?.MediaType);
            Assert.Equal(emlStub, await file.Content.ReadAsByteArrayAsync());

            var bad = await Send(port, HttpMethod.Post, "/api/email", "{\"markdown\":\"x\",\"output\":{\"emailTo\":\"bob\"}}", ext);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("bob", await bad.Content.ReadAsStringAsync());
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void Email_recipients_in_a_profile_replace_the_settings_for_that_draft()
    {
        var s = new AppSettings { EmailTo = "default@example.com" }.CloneWith(new OutputOverride { EmailTo = "ann@example.com", EmailCc = "" });
        Assert.Equal("ann@example.com", s.EmailTo);
        Assert.Equal("", s.EmailCc);
        Assert.Equal("default@example.com", new AppSettings { EmailTo = "default@example.com" }.CloneWith(new OutputOverride()).EmailTo);
    }
}
