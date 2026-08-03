namespace MarkSmith.Mermaid.Sync;

using System.Text.RegularExpressions;

/// <summary>
/// The Diagram Studio persists node positions as <c>%% {"id":...,"x":...}</c> comment lines
/// inside mermaid fences. Renderers ignore them, but they read as noise in the raw Markdown
/// editor. This service strips them out of the text shown in the editor (stashing them per
/// fence) and re-injects them whenever the full fidelity code is needed again — opening the
/// studio or syncing an export. <c>%%{init}</c> directives and ordinary comments are untouched.
/// </summary>
public static class MermaidSpatialMetadataService
{
    // A spatial comment is "%%" + JSON object whose first key is id (any casing the studio and
    // its tests have ever emitted). "%%{init:..}" fails this because "{" follows "%%" without
    // the object containing an id-first shape — anchor on the quoted id key to be safe.
    private static readonly Regex SpatialLineRegex = new(
        @"^\s*%%\s*\{\s*""(?:id|Id|ID)""\s*:", RegexOptions.Compiled);

    public static bool IsSpatialMetadataLine(string line) => SpatialLineRegex.IsMatch(line);

    /// <summary>
    /// Removes spatial metadata lines from every mermaid fence in <paramref name="markdown"/>.
    /// Returns the cleaned markdown; <paramref name="stash"/> maps mermaid block index to the
    /// removed lines (in order) so <see cref="Reinject"/> can restore them.
    /// </summary>
    public static string Strip(string markdown, out Dictionary<int, List<string>> stash)
    {
        stash = new Dictionary<int, List<string>>();
        if (string.IsNullOrEmpty(markdown))
            return markdown ?? string.Empty;

        var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(markdown);
        if (blocks.Count == 0)
            return markdown;

        // Rewrite fences back-to-front so earlier offsets stay valid.
        string result = markdown;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            var kept = new List<string>();
            var removed = new List<string>();
            foreach (var line in block.Code.Split('\n'))
            {
                var normalized = line.TrimEnd('\r');
                if (IsSpatialMetadataLine(normalized))
                    removed.Add(normalized.Trim());
                else
                    kept.Add(normalized);
            }

            if (removed.Count == 0)
                continue;

            stash[i] = removed;
            string cleaned = string.Join("\n", kept).Trim('\n');
            result = MermaidMarkdownSyncService.ReplaceMermaidBlock(result, i, cleaned);
        }

        return result;
    }

    /// <summary>
    /// Re-inserts stashed spatial metadata lines at the top of their mermaid fences. Fences
    /// that already contain spatial metadata, or indices that no longer exist (the user
    /// deleted/reordered fences in the editor), are skipped — re-injection is best-effort
    /// and idempotent.
    /// </summary>
    public static string Reinject(string markdown, IReadOnlyDictionary<int, List<string>>? stash)
    {
        if (string.IsNullOrEmpty(markdown) || stash == null || stash.Count == 0)
            return markdown ?? string.Empty;

        string result = markdown;
        foreach (var index in stash.Keys.OrderByDescending(k => k))
        {
            var blocks = MermaidMarkdownSyncService.ExtractMermaidBlocks(result);
            if (index < 0 || index >= blocks.Count)
                continue;

            var block = blocks[index];
            if (block.Code.Split('\n').Any(l => IsSpatialMetadataLine(l.TrimEnd('\r'))))
                continue; // fence already carries positions — don't duplicate

            string merged = string.Join("\n", stash[index]) + "\n" + block.Code.Trim('\n');
            result = MermaidMarkdownSyncService.ReplaceMermaidBlock(result, index, merged);
        }

        return result;
    }
}
