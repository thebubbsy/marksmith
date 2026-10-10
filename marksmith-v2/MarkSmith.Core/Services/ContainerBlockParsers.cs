using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Parsers for the inner content of the Insert-menu <c>:::</c> containers (tabs, datagrid,
/// references, embed, ai-context). The live preview (<see cref="MarkdownHtmlService"/>) reads
/// them through here; the rules mirror what the DOCX exporter accepts so a block that exports
/// also previews, and the other way round.
/// </summary>
public static class ContainerBlockParsers
{
    private static readonly Regex TabLineRegex = new(@"^:::tab(?:\s+title=(?:""(?<t1>.*)""|(?<t2>\S+))|\s+(?<t3>[^\n]+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TabHeaderRegex = new(@"^={2,3}\s+(?:""(?<t1>.*)""|(?<t3>[^\n]+))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttrRegex = new(@"(?<k>[A-Za-z][\w-]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""']+))", RegexOptions.Compiled);
    private static readonly Regex SeparatorRowRegex = new(@"^\|?[\s\-:|]+\|?$", RegexOptions.Compiled);

    /// <summary>Tabs inside a <c>:::tabs</c> block: <c>=== Title</c> / <c>== Title</c> headers or
    /// nested <c>:::tab title="…"</c> containers. Header-like lines inside code fences are body.</summary>
    public static List<(string Title, string Content)> ParseTabs(string innerContent)
    {
        var result = new List<(string Title, string Content)>();
        if (string.IsNullOrWhiteSpace(innerContent)) return result;

        string? currentTitle = null;
        var body = new List<string>();
        string? fence = null;

        foreach (var raw in innerContent.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (fence is null && (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")))
            {
                fence = trimmed[..3];
                if (currentTitle != null) body.Add(line);
                continue;
            }
            if (fence is not null)
            {
                if (currentTitle != null) body.Add(line);
                if (trimmed.StartsWith(fence)) fence = null;
                continue;
            }

            var m = TabLineRegex.Match(trimmed);
            if (!m.Success) m = TabHeaderRegex.Match(trimmed);
            if (m.Success)
            {
                if (currentTitle != null) result.Add((currentTitle, string.Join("\n", body).Trim()));
                body.Clear();
                var t = new[] { "t1", "t2", "t3" }.Select(g => m.Groups[g].Value.Trim()).FirstOrDefault(v => v.Length > 0);
                currentTitle = t ?? $"Tab {result.Count + 1}";
                continue;
            }

            if (trimmed == ":::") continue;   // closer of a nested :::tab
            if (currentTitle != null) body.Add(line);
        }

        if (currentTitle != null) result.Add((currentTitle, string.Join("\n", body).Trim()));
        return result;
    }

    /// <summary>Rows of a <c>:::datagrid</c>: comma- or tab-separated lines (what the DOCX
    /// exporter reads), and pipe-table rows too since the validator accepts them. The first row is
    /// the header. Separator rows (<c>|---|</c>) are skipped; short rows are padded.</summary>
    public static List<string[]> ParseDatagrid(string innerContent)
    {
        var rows = new List<string[]>();
        foreach (var raw in (innerContent ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == ":::") continue;
            string[] cells;
            if (line.Contains('|'))
            {
                if (SeparatorRowRegex.IsMatch(line)) continue;
                cells = line.Trim('|').Split('|');
            }
            else
            {
                cells = SplitDelimited(line, line.Contains('\t') ? '\t' : ',');
            }
            rows.Add(cells.Select(c => c.Trim()).ToArray());
        }

        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Length < width) rows[i] = rows[i].Concat(Enumerable.Repeat("", width - rows[i].Length)).ToArray();
        return rows;
    }

    /// <summary>True for a cell that reads as a number: "1,240", "$980.50", "-3%".</summary>
    public static bool LooksNumeric(string s)
    {
        var t = s.Trim().TrimStart('$', '€', '£', '¥').TrimEnd('%').Replace(",", "");
        return t.Length > 0 && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    /// <summary>One CSV/TSV row. A <c>"quoted"</c> cell keeps its delimiters (<c>"$1,240,000"</c>
    /// is one cell, not three) and <c>""</c> inside it is a literal quote, as in a spreadsheet's
    /// CSV export.</summary>
    public static string[] SplitDelimited(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch != '"') cell.Append(ch);
                else if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (ch == '"' && string.IsNullOrWhiteSpace(cell.ToString())) { cell.Clear(); quoted = true; }
            else if (ch == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString());
        return cells.ToArray();
    }

    /// <summary>One bibliography entry from a <c>:::references</c> block.</summary>
    public sealed record Reference(string Id, string Author, string Title, string Year, string Journal, string Url);

    /// <summary>Entries start at an <c>@id</c> line; <c>key: value</c> lines (author, title, year,
    /// journal, url) belong to the entry above them.</summary>
    public static List<Reference> ParseReferences(string innerContent)
    {
        var result = new List<Reference>();
        string? id = null;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (id is null) return;
            string F(string k) => fields.TryGetValue(k, out var v) ? v : "";
            result.Add(new Reference(id, F("author"), F("title"), F("year"), F("journal"), F("url")));
            fields.Clear();
        }

        foreach (var raw in (innerContent ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('@'))
            {
                Flush();
                id = line.TrimStart('@').Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            id ??= "";   // fields before any @id still make an (unnamed) entry
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        Flush();
        return result;
    }

    /// <summary><c>key: value</c> pairs in order (ai-context). Later duplicates win, like DOCX.</summary>
    public static List<KeyValuePair<string, string>> ParseKeyValues(string innerContent)
    {
        var map = new List<KeyValuePair<string, string>>();
        foreach (var raw in (innerContent ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();
            var at = map.FindIndex(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) map[at] = new(key, val); else map.Add(new(key, val));
        }
        return map;
    }

    /// <summary>Attributes on a container's marker line: <c>provider="youtube" src=…</c>.</summary>
    public static Dictionary<string, string> ParseAttributes(string markerLine)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex.Matches(markerLine ?? ""))
            attrs[m.Groups["k"].Value] = m.Groups["v"].Value;
        return attrs;
    }

    /// <summary>Display name for an embed provider id ("youtube" → "YouTube").</summary>
    public static string ProviderDisplayName(string provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        "youtube" => "YouTube",
        "vimeo" => "Vimeo",
        "loom" => "Loom",
        "codepen" => "CodePen",
        "bilibili" => "Bilibili",
        "figma" => "Figma",
        "" => "Embed",
        var p => char.ToUpperInvariant(p[0]) + p[1..],
    };

    /// <summary>The provider a URL belongs to, or null when it isn't one we know.</summary>
    public static string? DetectProvider(string url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        return host switch
        {
            "youtube.com" or "m.youtube.com" or "youtu.be" or "youtube-nocookie.com" => "youtube",
            "vimeo.com" or "player.vimeo.com" => "vimeo",
            "loom.com" => "loom",
            "codepen.io" => "codepen",
            "bilibili.com" or "b23.tv" => "bilibili",
            "figma.com" => "figma",
            _ => null,
        };
    }
}
