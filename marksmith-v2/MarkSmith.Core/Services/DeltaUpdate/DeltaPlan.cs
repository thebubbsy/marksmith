namespace MarkSmith.Services.DeltaUpdate;

/// <summary>The result of diffing a manifest against local hashes.</summary>
public sealed record DeltaResult(
    IReadOnlyList<DeltaFileEntry> ChangedOrAdded,
    IReadOnlyList<string> Removed,
    int Unchanged);

/// <summary>Pure, unit-testable delta computation: compare the release manifest's file hashes
/// against the locally installed files and return exactly what must be downloaded / deleted.</summary>
public static class DeltaPlan
{
    /// <summary>Keys are normalized relative paths ('/' separators, case-insensitive on Windows).</summary>
    public static DeltaResult ComputeDelta(DeltaManifest manifest, IReadOnlyDictionary<string, string> localSha256)
    {
        var changedOrAdded = new List<DeltaFileEntry>();
        foreach (var f in manifest.Files)
        {
            var key = Normalize(f.Path);
            if (!localSha256.TryGetValue(key, out var local) ||
                !string.Equals(local, f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changedOrAdded.Add(f);
            }
        }

        var manifestKeys = new HashSet<string>(manifest.Files.Select(f => Normalize(f.Path)), StringComparer.OrdinalIgnoreCase);
        var removed = localSha256.Keys.Where(k => !manifestKeys.Contains(k)).ToList();

        return new DeltaResult(changedOrAdded, removed, manifest.Files.Count - changedOrAdded.Count);
    }

    public static string Normalize(string path) => path.Replace('\\', '/');
}
