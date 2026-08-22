using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record BibTeXEntry(
    string Key,
    string EntryType = "article",
    string Title = "",
    string Author = "",
    string Year = "",
    string Journal = "",
    string Doi = "",
    string Url = "");

/// <summary>
/// Service that scans Markdown documents for citation keys and serializes standard BibTeX and CSL-JSON bibliography records.
/// </summary>
public static class BibTeXCitationExporter
{
    private static readonly Regex CitationKeyRegex = new(@"\[@([a-zA-Z0-9_\-]+)\]", RegexOptions.Compiled);
    private static readonly Regex CitationDefRegex = new(
        @"^\[@([a-zA-Z0-9_\-]+)\]:\s*(?:author=""([^""]*)"")?\s*(?:title=""([^""]*)"")?\s*(?:year=""([^""]*)"")?\s*(?:journal=""([^""]*)"")?\s*(?:doi=""([^""]*)"")?\s*(?:url=""([^""]*)"")?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts all citation entries defined or referenced in the Markdown document.
    /// </summary>
    public static List<BibTeXEntry> ExtractCitations(string markdown)
    {
        var entries = new Dictionary<string, BibTeXEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(markdown))
            return entries.Values.ToList();

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            var defMatch = CitationDefRegex.Match(line);
            if (defMatch.Success)
            {
                string key = defMatch.Groups[1].Value.Trim();
                string author = defMatch.Groups[2].Value.Trim();
                string title = defMatch.Groups[3].Value.Trim();
                string year = defMatch.Groups[4].Value.Trim();
                string journal = defMatch.Groups[5].Value.Trim();
                string doi = defMatch.Groups[6].Value.Trim();
                string url = defMatch.Groups[7].Value.Trim();

                entries[key] = new BibTeXEntry(key, "article", title, author, year, journal, doi, url);
                continue;
            }

            foreach (Match m in CitationKeyRegex.Matches(line))
            {
                string key = m.Groups[1].Value.Trim();
                if (!entries.ContainsKey(key))
                {
                    entries[key] = new BibTeXEntry(key, "misc", Title: key);
                }
            }
        }

        return entries.Values.ToList();
    }

    /// <summary>
    /// Serializes extracted citation entries into standardized BibTeX format (.bib).
    /// </summary>
    public static string ToBibTeX(IEnumerable<BibTeXEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.AppendLine($"@{e.EntryType}{{{e.Key},");
            if (!string.IsNullOrEmpty(e.Title)) sb.AppendLine($"    title = {{{e.Title}}},");
            if (!string.IsNullOrEmpty(e.Author)) sb.AppendLine($"    author = {{{e.Author}}},");
            if (!string.IsNullOrEmpty(e.Year)) sb.AppendLine($"    year = {{{e.Year}}},");
            if (!string.IsNullOrEmpty(e.Journal)) sb.AppendLine($"    journal = {{{e.Journal}}},");
            if (!string.IsNullOrEmpty(e.Doi)) sb.AppendLine($"    doi = {{{e.Doi}}},");
            if (!string.IsNullOrEmpty(e.Url)) sb.AppendLine($"    url = {{{e.Url}}},");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Serializes extracted citation entries into standard CSL-JSON (Citation Style Language) format.
    /// </summary>
    public static string ToCslJson(IEnumerable<BibTeXEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        var list = entries.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            sb.AppendLine("  {");
            sb.AppendLine($"    \"id\": \"{e.Key}\",");
            sb.AppendLine($"    \"type\": \"{e.EntryType}\",");
            sb.AppendLine($"    \"title\": \"{e.Title}\",");
            sb.AppendLine($"    \"author\": [{{ \"literal\": \"{e.Author}\" }}],");
            sb.AppendLine($"    \"issued\": {{ \"date-parts\": [[ { (int.TryParse(e.Year, out int y) ? y : 2024) } ]] }},");
            if (!string.IsNullOrEmpty(e.Journal)) sb.AppendLine($"    \"container-title\": \"{e.Journal}\",");
            if (!string.IsNullOrEmpty(e.Doi)) sb.AppendLine($"    \"DOI\": \"{e.Doi}\",");
            if (!string.IsNullOrEmpty(e.Url)) sb.AppendLine($"    \"URL\": \"{e.Url}\"");
            sb.Append("  }");
            if (i < list.Count - 1) sb.Append(",");
            sb.AppendLine();
        }
        sb.AppendLine("]");
        return sb.ToString();
    }
}
