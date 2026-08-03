namespace MarkSmith.Models;

// Describes one cloud-storage provider that Marksmith can publish exports to (Task 9). For the
// folder-sync providers (OneDrive / Google Drive / Dropbox / Box / iCloud) the "upload" is a plain
// file copy into the provider's local sync root — the provider's own desktop client then syncs it
// to the cloud. WebDAV / Nextcloud is endpoint-driven instead (an HTTP PUT), so its SyncRoot is
// empty and the endpoint lives in AppSettings.
public sealed class CloudProviderInfo
{
    // Stable machine-readable id ("onedrive", "googledrive", "dropbox", "box", "icloud", "webdav").
    public string Id { get; set; } = "";

    // Human-friendly label for the Settings UI ("OneDrive", "Google Drive", …).
    public string DisplayName { get; set; } = "";

    // Absolute path to the provider's local sync folder. Empty for endpoint-only providers (WebDAV).
    public string SyncRoot { get; set; } = "";

    // True when the sync folder was actually found on this machine.
    public bool Detected { get; set; }

    // How the root was located ("default path", "OneDrive - Business", "info.json", "G: volume"…).
    public string DetectionSource { get; set; } = "";
}
