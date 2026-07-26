using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Unified cloud-storage publishing (Task 9). The folder-sync providers (OneDrive, Google Drive,
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

    // Adds a detected provider unless the same sync root is already listed (Dropbox's info.json
    // usually points at the same folder as the default path — we don't want to show it twice).
    private static void AddDistinct(List<CloudProviderInfo> list, CloudProviderInfo p)
    {
        if (!list.Any(x => string.Equals(x.SyncRoot, p.SyncRoot, StringComparison.OrdinalIgnoreCase)))
            list.Add(p);
    }

    // Reads the Dropbox desktop client's info.json to find the real sync root (it can be relocated
    // off the default path). Returns null when the file is missing or malformed.
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

    // Scans the machine for known cloud-drive sync folders and returns one entry per provider found
    // (plus an always-present, never-"detected" WebDAV entry so the Settings UI can offer it).
    public IReadOnlyList<CloudProviderInfo> DetectProviders()
    {
        var result = new List<CloudProviderInfo>();
        var up = UserProfile;

        // OneDrive — the personal folder plus any "OneDrive - <tenant>" business folders.
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

        // Google Drive — the desktop app's folder, or the mapped "My Drive" volume.
        if (!string.IsNullOrEmpty(up))
        {
            var gd = Path.Combine(up, "Google Drive");
            if (Directory.Exists(gd)) AddDistinct(result, Make("googledrive", "Google Drive", gd, "default path"));
        }
        const string gVolume = @"G:\My Drive";
        if (Directory.Exists(gVolume)) AddDistinct(result, Make("googledrive", "Google Drive", gVolume, "G: volume"));

        // Dropbox — the default folder, or the path recorded by the client in info.json.
        if (!string.IsNullOrEmpty(up))
        {
            var db = Path.Combine(up, "Dropbox");
            if (Directory.Exists(db)) AddDistinct(result, Make("dropbox", "Dropbox", db, "default path"));
        }
        var infoPath = AppDataRoaming is null ? null : Path.Combine(AppDataRoaming, "Dropbox", "info.json");
        var infoRoot = ReadDropboxRoot(infoPath);
        if (infoRoot is not null && Directory.Exists(infoRoot))
            AddDistinct(result, Make("dropbox", "Dropbox", infoRoot, "info.json"));

        // Box and iCloud Drive.
        if (!string.IsNullOrEmpty(up))
        {
            var box = Path.Combine(up, "Box");
            if (Directory.Exists(box)) AddDistinct(result, Make("box", "Box", box, "default path"));
            var icloud = Path.Combine(up, "iCloudDrive");
            if (Directory.Exists(icloud)) AddDistinct(result, Make("icloud", "iCloud Drive", icloud, "default path"));
        }

        // WebDAV / Nextcloud is configured by endpoint, never detected locally — always offered.
        result.Add(new CloudProviderInfo
        {
            Id = "webdav",
            DisplayName = "WebDAV / Nextcloud",
            Detected = false,
            DetectionSource = "manual endpoint",
        });

        return result;
    }

    // Returns the detected sync root for a provider id, or null when it isn't present on this machine.
    public string? ResolveSyncRoot(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        return DetectProviders().FirstOrDefault(p =>
            p.Detected && string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))?.SyncRoot;
    }

    // Copies an exported file into the provider's local sync folder (under an optional subfolder) and
    // returns the destination path. The provider's desktop client takes it from there.
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

    // Uploads a file to a WebDAV / Nextcloud endpoint with an authenticated HTTP PUT. Uses Basic auth
    // when both a user and a token/password are supplied, Bearer when only a token is, and anonymous
    // otherwise. Returns true on any 2xx response.
    public async Task<bool> UploadToWebDavAsync(string filePath, string endpoint, string? user, string? token)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var baseUri = endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";
        var target = new Uri(new Uri(baseUri), Path.GetFileName(filePath));

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

        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(filePath));
        var resp = await client.PutAsync(target, content);
        return resp.IsSuccessStatusCode;
    }

    // One-call dispatch used by the export pipeline: route to the local sync-folder copy or the WebDAV
    // PUT based on the chosen provider. Returns the destination (path or endpoint) or null on failure.
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
            var ok = await UploadToWebDavAsync(filePath, webDavEndpoint, webDavUser, webDavToken);
            return ok ? webDavEndpoint : null;
        }
        return PublishToLocal(filePath, providerId, subfolder);
    }
}
