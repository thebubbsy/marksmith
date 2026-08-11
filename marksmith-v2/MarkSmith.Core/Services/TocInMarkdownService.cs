using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>Maintains a REAL table of contents inside the user's raw markdown (when the app's
/// IncludeToc option is on), instead of only injecting one into the preview/export. The block is
/// delimited by HTML-comment markers so it can be found, replaced and stripped reliably, and the
/// preview pipeline skips its own injected TOC when the markers are present (no double TOC).</summary>
public static partial class TocInMarkdownService
{
    public const string StartMarker = "<!-- MARKSMITH-TOC:START -->";
    public const string EndMarker = "<!-- MARKSMITH-TOC:END -->";

    // Mirrors the Markdig AutoIdentifiers default slugify (the ids the rendered HTML gives the
    // headings): lowercase; keep [a-z0-9 _-]; every run of other characters AND spaces collapses
    // to a single '-'; duplicate headings get -1/-2... (verified against the app's real Markdig
    // pipeline in TocInMarkdownTests).
    private static string Slugify(string heading)
    {
        var sb = new StringBuilder(heading.Length);
        var lastWasDash = false;
        foreach (var c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else
            {
                // Any other character (space, &, %, punctuation) collapses into a single dash.
                if (!lastWasDash)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }
        }
        return Regex.Replace(sb.ToString().Trim('-'), @"^\d+-", ""); // GitHub-style: leading numbers stripped
    }

    /// <summary>Builds the markdown block for the given headings (already slugged with duplicate
    /// suffixes applied). Empty when there are fewer than two headings worth listing.</summary>
    public static string BuildBlock(IReadOnlyList<(int Level, string Text, string Slug)> headings)
    {
        if (headings.Count < 2) return "";
        var sb = new StringBuilder();
        sb.AppendLine(StartMarker);
        sb.AppendLine("## Table of Contents");
        sb.AppendLine();
        foreach (var (level, text, slug) in headings)
        {
            var indent = new string(' ', (level - 1) * 2);
            sb.AppendLine($"{indent}- [{text}](#{slug})");
        }
        sb.Append(EndMarker);
        return sb.ToString();
    }

    /// <summary>Headings from the markdown, skipping fenced code blocks and the maintained TOC
    /// region itself. Level is the 1-6 heading depth; slug is Markdig-compatible with per-block
    /// duplicate suffixing (-1, -2, …).</summary>
    public static List<(int Level, string Text, string Slug)> ExtractHeadings(string markdown)
    {
        var result = new List<(int, string, string)>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var inFence = false;
        var inTocRegion = false;
        foreach (var line in markdown.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Contains(StartMarker, StringComparison.Ordinal)) { inTocRegion = true; continue; }
            if (t.Contains(EndMarker, StringComparison.Ordinal)) { inTocRegion = false; continue; }
            if (inTocRegion) continue;

            var fence = FenceRe().Match(t);
            if (fence.Success) { inFence = !inFence; continue; }
            if (inFence) continue;

            var m = HeadingRe().Match(t);
            if (!m.Success) continue;
            var text = InlineMarkRe().Replace(m.Groups[2].Value, "").Trim();
            if (text.Length == 0) continue;

            var slug = Slugify(text);
            if (seen.TryGetValue(slug, out var count))
            {
                seen[slug] = count + 1;
                slug = $"{slug}-{count}";
            }
            else
            {
                seen[slug] = 1;
            }
            result.Add((m.Groups[1].Value.Length, text, slug));
        }
        return result;
    }

    /// <summary>Inserts or replaces the maintained TOC block at the very top of the markdown.</summary>
    public static string InsertOrReplace(string markdown, string block)
    {
        var start = markdown.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = markdown.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            var before = markdown[..start];
            var after = markdown[(end + EndMarker.Length)..].TrimStart('\r', '\n');
            return before.TrimEnd('\r', '\n') + "\n\n" + block + "\n\n" + after;
        }
        return block + "\n\n" + markdown.TrimStart('\r', '\n');
    }

    /// <summary>Removes the maintained TOC block (markers + content) from the markdown.</summary>
    public static string Remove(string markdown)
    {
        var start = markdown.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = markdown.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start) return markdown;
        var before = markdown[..start];
        var after = markdown[(end + EndMarker.Length)..];
        return (before.TrimEnd('\r', '\n') + "\n" + after.TrimStart('\r', '\n')).TrimStart('\r', '\n');
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"^\s*(```|~~~)", RegexOptions.Multiline)]
    private static partial Regex FenceRe();

    [GeneratedRegex(@"[*`~]")]
    private static partial Regex InlineMarkRe();
}
