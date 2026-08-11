using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkSmith.Services.DeltaUpdate;

/// <summary>One manifest entry: a file in the publish output with its SHA-256 and size.</summary>
public sealed record DeltaFileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>The per-release file manifest published with every release. The delta updater downloads
/// this small file first, diffs it against the hashes of the locally installed files, and then
/// downloads ONLY the files whose hash changed (see DeltaPlan/DeltaUpdateService).</summary>
public sealed class DeltaManifest
{
    public const string FormatName = "marksmith-file-manifest";
    public const int SchemaVersionNumber = 1;
    public const string ManifestFileName = "file-manifest.json";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("schema")] public int Schema { get; set; } = SchemaVersionNumber;
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("files")] public List<DeltaFileEntry> Files { get; set; } = new();

    public static DeltaManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Manifest is empty.");
        DeltaManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeltaManifest>(json, DeltaJson.Options)
                ?? throw new InvalidDataException("Manifest could not be parsed.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Manifest JSON is invalid: {ex.Message}");
        }
        if (manifest.Format != FormatName) throw new InvalidDataException($"Unsupported manifest format '{manifest.Format}'.");
        if (manifest.Schema != SchemaVersionNumber) throw new InvalidDataException($"Unsupported manifest schema {manifest.Schema}.");
        if (string.IsNullOrWhiteSpace(manifest.Release)) throw new InvalidDataException("Manifest has no release.");
        foreach (var f in manifest.Files)
        {
            if (!IsSafeRelativePath(f.Path)) throw new InvalidDataException($"Unsafe manifest path '{f.Path}'.");
            if (string.IsNullOrWhiteSpace(f.Sha256) || f.Sha256.Length != 64) throw new InvalidDataException($"Manifest entry '{f.Path}' has no valid SHA-256.");
        }
        return manifest;
    }

    /// <summary>Rejects absolute paths, drive letters, and ".." traversal — the path is joined onto
    /// the staging dir and the install dir, so it must stay a safe relative path.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('\\', '/');
        if (p.StartsWith('/') || p.StartsWith("//")) return false;
        if (p.Length >= 2 && p[1] == ':') return false; // drive letter
        foreach (var seg in p.Split('/'))
        {
            if (seg == "..") return false;
        }
        return true;
    }
}

/// <summary>Written into the staging dir after a successful delta download; consumed by the
/// apply step on the next launch (TryApplyPendingDeltaUpdate).</summary>
public sealed class ApplyManifest
{
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("base_url")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("changed")] public List<DeltaFileEntry> Changed { get; set; } = new();
    [JsonPropertyName("removed")] public List<string> Removed { get; set; } = new();
}
