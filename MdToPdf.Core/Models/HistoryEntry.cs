using System.Text.RegularExpressions;

namespace MdToPdf.Models;

public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string SourceLabel { get; set; } = "";   // input file name or "pasted"
    public string Detected { get; set; } = "";      // ChatGPT / Gemini / Claude / Markdown
    public string Theme { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Kind { get; set; } = "";          // PDF / DOCX
    public string DocumentTitle { get; set; } = ""; // the document's own title (first heading)

    // The history line reads as the exported document's title; format details move to the subtitle.
    public string Title => string.IsNullOrWhiteSpace(DocumentTitle)
        ? (string.IsNullOrWhiteSpace(SourceLabel) ? "Untitled document" : SourceLabel)
        : DocumentTitle;

    public string Subtitle => $"{Kind} · {Detected} · {Theme} · {Timestamp:g}";

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
                if (lines[i].Trim() == "---") break;
                var m = Regex.Match(lines[i].Trim(), @"^title:\s*(.+)$", RegexOptions.IgnoreCase);
                if (m.Success) return Clean(m.Groups[1].Value);
            }
        }

        foreach (var raw in lines)
        {
            var m = Regex.Match(raw, @"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$");
            if (m.Success) return Clean(m.Groups[1].Value);
        }

        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("```") || l.StartsWith("~~~") || l.StartsWith("---") || l.StartsWith(">")) continue;
            return Clean(l);
        }
        return "";
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1"); // links -> text
        s = Regex.Replace(s, @"[*_`~]", "");                  // emphasis / code / strikethrough
        s = s.Trim().Trim('"');
        return s.Length > 80 ? s[..79].TrimEnd() + "…" : s;
    }
}
