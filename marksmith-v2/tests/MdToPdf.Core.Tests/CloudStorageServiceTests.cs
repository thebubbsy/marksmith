using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// Task 9 — CloudStorageService: provider detection, sync-root resolution, local sync-folder
// publishing and WebDAV upload. Detection runs against a throwaway "home"/"appdata" directory via
// an injected environment resolver, and the WebDAV PUT is captured by a fake HttpMessageHandler,
// so none of these tests need a real cloud drive or network.
public class CloudStorageServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;
    private readonly string _appdata;
    private readonly CloudStorageService _svc;

    public CloudStorageServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mk-cloud-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(_root, "home");
        _appdata = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_appdata);
        _svc = new CloudStorageService(env: name => name switch
        {
            "USERPROFILE" => _home,
            "APPDATA" => _appdata,
            _ => null,
        });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private string MkDir(params string[] parts)
    {
        var p = Path.Combine(new[] { _home }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    // ---- Detection: one test per provider target ----

    [Fact]
    public void Detects_OneDrive_personal_folder()
    {
        var dir = MkDir("OneDrive");
        var hit = _svc.DetectProviders().Single(p => p.Id == "onedrive");
        Assert.True(hit.Detected);
        Assert.Equal(dir, hit.SyncRoot);
    }

    [Fact]
    public void Detects_OneDrive_business_folder()
    {
        MkDir("OneDrive");
        var biz = MkDir("OneDrive - Contoso");
        var hits = _svc.DetectProviders().Where(p => p.Id == "onedrive").ToList();
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.SyncRoot == biz && h.DisplayName.Contains("Business"));
    }

    [Fact]
    public void Detects_GoogleDrive_folder()
    {
        var dir = MkDir("Google Drive");
        var hit = _svc.DetectProviders().First(p => p.Id == "googledrive" && p.SyncRoot == dir);
        Assert.True(hit.Detected);
    }

    [Fact]
    public void Detects_Dropbox_default_folder()
    {
        var dir = MkDir("Dropbox");
        var hit = _svc.DetectProviders().Single(p => p.Id == "dropbox");
        Assert.True(hit.Detected);
        Assert.Equal(dir, hit.SyncRoot);
    }

    [Fact]
    public void Detects_Dropbox_via_info_json()
    {
        // No default folder — the client's info.json points at a relocated root.
        var custom = Path.Combine(_root, "elsewhere", "Dropbox");
        Directory.CreateDirectory(custom);
        var infoDir = Path.Combine(_appdata, "Dropbox");
        Directory.CreateDirectory(infoDir);
        File.WriteAllText(Path.Combine(infoDir, "info.json"), $"{{\"personal\": {{\"path\": \"{custom.Replace("\\", "\\\\")}\"}}}}");

        var hit = _svc.DetectProviders().Single(p => p.Id == "dropbox");
        Assert.True(hit.Detected);
        Assert.Equal(custom, hit.SyncRoot);
        Assert.Equal("info.json", hit.DetectionSource);
    }

    [Fact]
    public void Detects_Box_folder()
    {
        var dir = MkDir("Box");
        var hit = _svc.DetectProviders().Single(p => p.Id == "box");
        Assert.True(hit.Detected);
        Assert.Equal(dir, hit.SyncRoot);
    }

    [Fact]
    public void Detects_iCloudDrive_folder()
    {
        var dir = MkDir("iCloudDrive");
        var hit = _svc.DetectProviders().Single(p => p.Id == "icloud");
        Assert.True(hit.Detected);
        Assert.Equal(dir, hit.SyncRoot);
    }

    [Fact]
    public void WebDav_always_listed_but_never_detected()
    {
        var hit = _svc.DetectProviders().Single(p => p.Id == "webdav");
        Assert.False(hit.Detected);
        Assert.Equal("", hit.SyncRoot);
    }

    [Fact]
    public void No_providers_detected_on_empty_machine()
    {
        // Only the always-present WebDAV entry should come back.
        var all = _svc.DetectProviders();
        Assert.Single(all);
        Assert.Equal("webdav", all[0].Id);
    }

    // ---- Sync-root resolution ----

    [Fact]
    public void ResolveSyncRoot_returns_detected_root()
    {
        var dir = MkDir("OneDrive");
        Assert.Equal(dir, _svc.ResolveSyncRoot("onedrive"));
    }

    [Fact]
    public void ResolveSyncRoot_null_for_unknown_or_empty()
    {
        MkDir("OneDrive");
        Assert.Null(_svc.ResolveSyncRoot("googledrive")); // not created -> not detected
        Assert.Null(_svc.ResolveSyncRoot(""));
        Assert.Null(_svc.ResolveSyncRoot(null!));
    }

    // ---- Local publish (copy into sync folder) ----

    [Fact]
    public void PublishToLocal_copies_into_subfolder()
    {
        var drive = MkDir("OneDrive");
        var src = Path.Combine(_root, "report.pdf");
        File.WriteAllText(src, "pdf-bytes");

        var dest = _svc.PublishToLocal(src, "onedrive", "Marksmith");

        Assert.Equal(Path.Combine(drive, "Marksmith", "report.pdf"), dest);
        Assert.True(File.Exists(dest));
        Assert.Equal("pdf-bytes", File.ReadAllText(dest));
    }

    [Fact]
    public void PublishToLocal_overwrites_existing()
    {
        MkDir("OneDrive");
        var src = Path.Combine(_root, "a.docx");
        File.WriteAllText(src, "v2");
        _svc.PublishToLocal(src, "onedrive", "");
        File.WriteAllText(src, "v2"); // same name again
        var dest = _svc.PublishToLocal(src, "onedrive", "");
        Assert.Equal("v2", File.ReadAllText(dest));
    }

    [Fact]
    public void PublishToLocal_throws_when_provider_missing()
    {
        var src = Path.Combine(_root, "x.pdf");
        File.WriteAllText(src, "x");
        Assert.Throws<InvalidOperationException>(() => _svc.PublishToLocal(src, "dropbox", "Marksmith"));
    }

    // ---- WebDAV upload (fake handler, no network) ----

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public HttpStatusCode Status = HttpStatusCode.Created;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }

    private CloudStorageService ServiceWith(FakeHandler handler) =>
        new(env: name => name switch { "USERPROFILE" => _home, "APPDATA" => _appdata, _ => null },
            handlerFactory: () => handler);

    [Fact]
    public async Task WebDav_put_uploads_file_with_basic_auth()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.Created };
        var svc = ServiceWith(handler);
        var src = Path.Combine(_root, "notes.pdf");
        File.WriteAllText(src, "hello-cloud");

        var ok = await svc.UploadToWebDavAsync(src, "https://cloud.example.com/dav/files/me", "me", "secret");

        Assert.True(ok);
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.EndsWith("/dav/files/me/notes.pdf", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("hello-cloud", handler.LastBody);
        Assert.Equal("Basic", handler.LastRequest.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task WebDav_uses_bearer_when_only_token()
    {
        var handler = new FakeHandler();
        var svc = ServiceWith(handler);
        var src = Path.Combine(_root, "t.pdf");
        File.WriteAllText(src, "x");

        await svc.UploadToWebDavAsync(src, "https://cloud.example.com/dav/", null, "tok123");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("tok123", handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task WebDav_returns_false_on_server_error()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.InternalServerError };
        var svc = ServiceWith(handler);
        var src = Path.Combine(_root, "e.pdf");
        File.WriteAllText(src, "x");

        var ok = await svc.UploadToWebDavAsync(src, "https://cloud.example.com/dav/", "u", "p");

        Assert.False(ok);
    }

    [Fact]
    public async Task WebDav_false_when_endpoint_empty()
    {
        var svc = ServiceWith(new FakeHandler());
        var src = Path.Combine(_root, "n.pdf");
        File.WriteAllText(src, "x");
        Assert.False(await svc.UploadToWebDavAsync(src, "", "u", "p"));
    }

    // ---- Unified dispatch ----

    [Fact]
    public async Task PublishAsync_routes_local_provider_to_sync_folder()
    {
        var drive = MkDir("Box");
        var src = Path.Combine(_root, "deck.pptx");
        File.WriteAllText(src, "slides");

        var dest = await _svc.PublishAsync(src, "box", "Marksmith");

        Assert.Equal(Path.Combine(drive, "Marksmith", "deck.pptx"), dest);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task PublishAsync_routes_webdav_to_endpoint()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.Created };
        var svc = ServiceWith(handler);
        var src = Path.Combine(_root, "w.epub");
        File.WriteAllText(src, "book");

        var result = await svc.PublishAsync(src, "webdav", "exports", "https://cloud.example.com/dav/", "u", "p");

        Assert.Equal("https://cloud.example.com/dav/", result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://cloud.example.com/dav/exports/w.epub", handler.LastRequest!.RequestUri?.ToString());
    }

    [Fact]
    public async Task TestConnectionAsync_returns_true_on_successful_head_request()
    {
        var handler = new FakeHandler { Status = HttpStatusCode.OK };
        var svc = ServiceWith(handler);

        var ok = await svc.TestConnectionAsync("https://cloud.example.com/dav/", "user", "token");

        Assert.True(ok);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Head, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task PublishAsync_null_when_no_provider()
    {
        var src = Path.Combine(_root, "z.pdf");
        File.WriteAllText(src, "x");
        Assert.Null(await _svc.PublishAsync(src, "", "Marksmith"));
    }
}
