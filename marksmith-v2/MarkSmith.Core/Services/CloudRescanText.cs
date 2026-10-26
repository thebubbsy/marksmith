using System;
using System.Collections.Generic;
using System.Linq;
using MarkSmith.Models;

namespace MarkSmith.Services;

// What Settings ▸ Cloud storage says after "Rescan for cloud drives". It counted the sync folders
// found but never mentioned the one the user picked, so a drive that had been removed or renamed
// went unnoticed until exports quietly stopped being copied.
public static class CloudRescanText
{
    public static string Summary(IReadOnlyList<CloudProviderInfo> providers, string? chosenId)
    {
        // WebDAV is always listed alongside the local sync folders.
        var local = providers.Count(p => !IsWebDav(p.Id));
        var found = local switch
        {
            0 => "No local cloud sync folders found on this PC (WebDAV is available).",
            1 => "Found 1 local cloud sync folder on this PC.",
            _ => $"Found {local} local cloud sync folders on this PC.",
        };

        if (string.IsNullOrWhiteSpace(chosenId) || IsWebDav(chosenId)) return found;
        if (providers.Any(p => string.Equals(p.Id, chosenId, StringComparison.OrdinalIgnoreCase))) return found;
        return $"{found} Your chosen drive ({NameOf(chosenId)}) isn't on this PC any more: pick another, or exports won't be copied.";
    }

    private static bool IsWebDav(string id) => string.Equals(id, "webdav", StringComparison.OrdinalIgnoreCase);

    internal static string NameOf(string id) => id.ToLowerInvariant() switch
    {
        "onedrive" => "OneDrive",
        "googledrive" => "Google Drive",
        "dropbox" => "Dropbox",
        "box" => "Box",
        "icloud" => "iCloud Drive",
        _ => id,
    };
}
