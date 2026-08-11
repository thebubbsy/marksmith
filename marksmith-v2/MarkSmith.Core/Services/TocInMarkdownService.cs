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
            // Escape link-label metacharacters so headings like "C# (CSharp)" or "Bug [1]" stay
            // valid markdown links.
            var label = text.Replace("[", "\\[").Replace("]", "\\]").Replace("(", "\\(").Replace(")", "\\)");
            sb.AppendLine($"{indent}- [{label}](#{slug})");
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
            // Marker lines only count OUTSIDE fences — a code block containing the marker strings
            // must not flip the region state (and swallow every following heading).
            if (!inFence)
            {
                if (t.Contains(StartMarker, StringComparison.Ordinal)) { inTocRegion = true; continue; }
                if (t.Contains(EndMarker, StringComparison.Ordinal)) { inTocRegion = false; continue; }
            }
            if (inTocRegion) continue;

            var fence = FenceRe().Match(t);
            if (fence.Success) { inFence = !inFence; continue; }
            if (inFence) continue;

            var m = HeadingRe().Match(t);
            if (!m.Success) continue;
            var text = InlineMarkRe().Replace(m.Groups[2].Value, "").Trim();
            if (text.Length == 0) continue;

            var slug = Slugify(text);
            if (slug.Length == 0) continue; // un-anchorable heading (e.g. "# ---") — omit from the TOC
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

    /// <summary>The contiguous maintained-block range (start of StartMarker .. end of EndMarker),
    /// or (-1, -1) when there is no valid pair. A stray unpaired marker is NOT a block, and a pair
    /// living inside a fenced code block is not the maintained TOC either — documents that merely
    /// contain the strings are left untouched. Note: the block is expected at the top of the
    /// document; a pair deeper in the doc is still honored (content between it and the top is
    /// preserved on replace/remove).</summary>
    private static (int Start, int End) FindBlockRange(string markdown)
    {
        var searchFrom = 0;
        while (true)
        {
            var start = markdown.IndexOf(StartMarker, searchFrom, StringComparison.Ordinal);
            if (start < 0) return (-1, -1);
            var end = markdown.IndexOf(EndMarker, start + StartMarker.Length, StringComparison.Ordinal);
            if (end < 0) return (-1, -1);

            // A pair inside a fenced code block is a code sample, not the maintained TOC — skip it
            // and keep looking (also keeps the fence from being unbalanced by a mid-fence replace).
            var inFence = false;
            foreach (var line in markdown[..start].Split('\n'))
            {
                if (FenceRe().IsMatch(line.TrimEnd('\r'))) inFence = !inFence;
            }
            if (!inFence) return (start, end + EndMarker.Length);

            searchFrom = end + EndMarker.Length;
        }
    }

    /// <summary>True when the document carries the maintained TOC block (a contiguous marker pair
    /// outside any fenced code block). Used by the preview pipeline to skip its own injected TOC —
    /// a code sample that merely mentions the markers doesn't suppress the injection.</summary>
    public static bool HasMaintainedToc(string markdown) =>
        !string.IsNullOrEmpty(markdown) && FindBlockRange(markdown).Start >= 0;

    /// <summary>Inserts or replaces the maintained TOC block at the very top of the markdown.
    /// Idempotent: re-running on a document that already carries the block yields the same text.</summary>
    public static string InsertOrReplace(string markdown, string block)
    {
        var (start, end) = FindBlockRange(markdown);
        if (start >= 0)
        {
            var before = markdown[..start];
            var after = markdown[end..].TrimStart('\r', '\n');
            return (before.TrimEnd('\r', '\n') + "\n\n" + block + "\n\n" + after).TrimStart('\r', '\n');
        }
        return block + "\n\n" + markdown.TrimStart('\r', '\n');
    }

    /// <summary>Removes the maintained TOC block (markers + content) from the markdown. Only a
    /// contiguous marker PAIR is removed — a document that merely contains the marker strings is
    /// returned unchanged.</summary>
    public static string Remove(string markdown)
    {
        var (start, end) = FindBlockRange(markdown);
        if (start < 0) return markdown;
        var before = markdown[..start];
        var after = markdown[end..];
        return (before.TrimEnd('\r', '\n') + "\n" + after.TrimStart('\r', '\n')).TrimStart('\r', '\n');
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"^\s*(```|~~~)", RegexOptions.Multiline)]
    private static partial Regex FenceRe();

    [GeneratedRegex(@"[*`~]")]
    private static partial Regex InlineMarkRe();
}
