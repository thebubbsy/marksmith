using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Unified cloud-storage publishing (Task 9 & Task 22). The folder-sync providers (OneDrive, Google Drive,
// Dropbox, Box, iCloud) are detected by locating their local sync directory on this machine;
// "publishing" is then just a file copy into that directory and the provider's own desktop client
// syncs it up to the cloud. WebDAV / Nextcloud is endpoint-driven instead (an authenticated HTTP
// PUT). Environment-variable resolution and the HTTP handler are injectable so detection and
// upload can be unit-tested without a real cloud drive or network.
public sealed class CloudStorageService
{
    private readonly Func<string, string?> _env;
    private readonly Func<HttpMessageHandler> _handlerFactory;

    public CloudStorageService(Func<string, string?>? env = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _env = env ?? Environment.GetEnvironmentVariable;
        _handlerFactory = handlerFactory ?? (() => new HttpClientHandler());
    }

    private string? UserProfile => _env("USERPROFILE");
    private string? AppDataRoaming => _env("APPDATA");

    private static CloudProviderInfo Make(string id, string name, string root, string source) =>
        new() { Id = id, DisplayName = name, SyncRoot = root, Detected = true, DetectionSource = source };

    private static void AddDistinct(List<CloudProviderInfo> list, CloudProviderInfo p)
    {
        if (!list.Any(x => string.Equals(x.SyncRoot, p.SyncRoot, StringComparison.OrdinalIgnoreCase)))
            list.Add(p);
    }

    private static string? ReadDropboxRoot(string? infoPath)
    {
        if (infoPath is null || !File.Exists(infoPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("personal", out var personal) && personal.TryGetProperty("path", out var p))
                return p.GetString();
            if (root.TryGetProperty("business", out var business) && business.TryGetProperty("path", out var bp))
                return bp.GetString();
            return null;
        }
        catch { return null; }
    }

    public IReadOnlyList<CloudProviderInfo> DetectProviders()
    {
        var result = new List<CloudProviderInfo>();
        var up = UserProfile;

        if (!string.IsNullOrEmpty(up))
        {
            var personal = Path.Combine(up, "OneDrive");
            if (Directory.Exists(personal)) AddDistinct(result, Make("onedrive", "OneDrive", personal, "default path"));
            try
            {
                foreach (var biz in Directory.EnumerateDirectories(up, "OneDrive - *"))
                    AddDistinct(result, Make("onedrive", "OneDrive (Business)", biz, "business folder"));
            }
            catch { /* enumerating the profile root is best-effort */ }
        }

        if (!string.IsNullOrEmpty(up))
        {
            var gd = Path.Combine(up, "Google Drive");
            if (Directory.Exists(gd)) AddDistinct(result, Make("googledrive", "Google Drive", gd, "default path"));
        }
        const string gVolume = @"G:\My Drive";
        if (Directory.Exists(gVolume)) AddDistinct(result, Make("googledrive", "Google Drive", gVolume, "G: volume"));

        if (!string.IsNullOrEmpty(up))
        {
            var db = Path.Combine(up, "Dropbox");
            if (Directory.Exists(db)) AddDistinct(result, Make("dropbox", "Dropbox", db, "default path"));
        }
        var infoPath = AppDataRoaming is null ? null : Path.Combine(AppDataRoaming, "Dropbox", "info.json");
        var infoRoot = ReadDropboxRoot(infoPath);
        if (infoRoot is not null && Directory.Exists(infoRoot))
            AddDistinct(result, Make("dropbox", "Dropbox", infoRoot, "info.json"));

        if (!string.IsNullOrEmpty(up))
        {
            var box = Path.Combine(up, "Box");
            if (Directory.Exists(box)) AddDistinct(result, Make("box", "Box", box, "default path"));
            var icloud = Path.Combine(up, "iCloudDrive");
            if (Directory.Exists(icloud)) AddDistinct(result, Make("icloud", "iCloud Drive", icloud, "default path"));
        }

        result.Add(new CloudProviderInfo
        {
            Id = "webdav",
            DisplayName = "WebDAV / Nextcloud",
            Detected = false,
            DetectionSource = "manual endpoint",
        });

        return result;
    }

    public string? ResolveSyncRoot(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        return DetectProviders().FirstOrDefault(p =>
            p.Detected && string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))?.SyncRoot;
    }

    public string PublishToLocal(string filePath, string providerId, string subfolder)
    {
        var root = ResolveSyncRoot(providerId)
            ?? throw new InvalidOperationException($"No local sync folder detected for provider '{providerId}'.");
        var targetDir = string.IsNullOrWhiteSpace(subfolder) ? root : Path.Combine(root, subfolder);
        Directory.CreateDirectory(targetDir);
        var dest = Path.Combine(targetDir, Path.GetFileName(filePath));
        File.Copy(filePath, dest, overwrite: true);
        return dest;
    }

    public async Task<bool> UploadToWebDavAsync(string filePath, string endpoint, string? user, string? token, string? subfolder = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var baseUriStr = endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";
        if (!string.IsNullOrWhiteSpace(subfolder))
        {
            var cleanSub = subfolder.Trim('/', '\\');
            if (cleanSub.Length > 0)
            {
                baseUriStr += cleanSub + "/";
            }
        }
        var target = new Uri(new Uri(baseUriStr), Path.GetFileName(filePath));

        using var client = new HttpClient(_handlerFactory());
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
        else if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-CloudPublisher/2.0");

        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(filePath));
        var resp = await client.PutAsync(target, content);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> TestConnectionAsync(string endpoint, string? user, string? token)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        try
        {
            using var client = new HttpClient(_handlerFactory());
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            }
            else if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-CloudPublisher/2.0");

            var req = new HttpRequestMessage(HttpMethod.Head, endpoint);
            var resp = await client.SendAsync(req);
            return resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> PublishAsync(
        string filePath,
        string providerId,
        string subfolder,
        string webDavEndpoint = "",
        string webDavUser = "",
        string webDavToken = "")
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        if (string.Equals(providerId, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            var ok = await UploadToWebDavAsync(filePath, webDavEndpoint, webDavUser, webDavToken, subfolder);
            return ok ? webDavEndpoint : null;
        }
        return PublishToLocal(filePath, providerId, subfolder);
    }
}
