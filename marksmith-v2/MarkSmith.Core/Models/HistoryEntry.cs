using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Models;

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SourceLabel { get; set; } = "";   // input file name or "pasted"
    public string Detected { get; set; } = "";      // ChatGPT / Gemini / Claude / Markdown
    public string Theme { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Kind { get; set; } = "";          // PDF / DOCX
    public string DocumentTitle { get; set; } = ""; // the document's own title (first heading)

    // Export telemetry (Task 15): how long the conversion took and how big the output file is.
    // Both are optional — DurationMs is 0 when the caller didn't time the export, OutputSizeBytes
    // is 0 when the output file couldn't be measured — and are simply omitted from the subtitle.
    public long DurationMs { get; set; }
    public long OutputSizeBytes { get; set; }

    // The history line reads as the exported document's title; format details move to the subtitle.
    public string Title => string.IsNullOrWhiteSpace(DocumentTitle)
        ? (string.IsNullOrWhiteSpace(SourceLabel) ? "Untitled document" : SourceLabel)
        : DocumentTitle;

    // Human-readable output size ("512 B", "18.4 KB", "1.2 MB"); empty when unknown.
    public string OutputSizeText => OutputSizeBytes switch
    {
        <= 0 => "",
        < 1024 => $"{OutputSizeBytes} B",
        < 1024 * 1024 => $"{OutputSizeBytes / 1024.0:0.#} KB",
        _ => $"{OutputSizeBytes / (1024.0 * 1024.0):0.#} MB",
    };

    // Human-readable export duration ("320 ms", "2.5 s"); empty when the caller didn't time it.
    public string DurationText => DurationMs switch
    {
        <= 0 => "",
        < 1000 => $"{DurationMs} ms",
        _ => $"{DurationMs / 1000.0:0.#} s",
    };

    public string Subtitle
    {
        get
        {
            var core = $"{Kind} · {Detected} · {Theme} · {Timestamp:g}";
            var extra = string.Join(" · ", new[] { DurationText, OutputSizeText }.Where(s => s.Length > 0));
            return extra.Length == 0 ? core : $"{core} · {extra}";
        }
    }

    // Cached compiled regexes — these used to be re-created via new Regex(...) on every
    // history-list render (ExtractTitle runs once per history row).
    private static readonly Regex FrontMatterTitleRegex = new(@"^title:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex EmphasisRegex = new(@"[*_`~]", RegexOptions.Compiled);

    // Pull a human title from the Markdown: YAML front-matter title, else the first heading,
    // else the first meaningful line. Cleaned of Markdown syntax and length-capped.
    public static string ExtractTitle(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r", "").Split('\n');

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (var i = 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == "---") break;
                var m = FrontMatterTitleRegex.Match(trimmed);
                if (m.Success) return Clean(m.Groups[1].Value);
            }
        }

        foreach (var raw in lines)
        {
            var m = HeadingRegex.Match(raw);
            if (m.Success) return Clean(m.Groups[1].Value);
        }

        var inFence = false;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith("```") || l.StartsWith("~~~")) { inFence = !inFence; continue; }
            if (inFence || l.Length == 0 || l.StartsWith("---") || l.StartsWith(">")) continue;
            return Clean(l);
        }
        return "";
    }

    private static string Clean(string s)
    {
        s = LinkRegex.Replace(s, "$1");   // links -> text
        s = EmphasisRegex.Replace(s, ""); // emphasis / code / strikethrough
        s = s.Trim().Trim('"');
        return s.Length > 80 ? s[..79].TrimEnd() + "…" : s;
    }
}
