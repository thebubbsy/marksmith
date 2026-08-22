using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record GlossaryEntry(
    string Term,
    string Definition,
    int FirstOccurrenceLine = 1,
    int OccurrencesCount = 1);

/// <summary>
/// Service for scanning, extracting, and rendering abbreviation definitions and glossary term tooltips in Markdown.
/// </summary>
public static class GlossaryLexiconService
{
    private static readonly Regex AbbrDefRegex = new(@"^\*\[([^\]]+)\]:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DefListRegex = new(@"^([^\n\r:]+)\r?\n:\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Scans Markdown content and extracts all defined glossary terms and abbreviations.
    /// </summary>
    public static List<GlossaryEntry> ExtractGlossary(string markdown)
    {
        var dict = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(markdown))
            return dict.Values.ToList();

        // 1. Markdown abbreviation definitions: *[HTML]: HyperText Markup Language
        foreach (Match m in AbbrDefRegex.Matches(markdown))
        {
            string term = m.Groups[1].Value.Trim();
            string def = m.Groups[2].Value.Trim();
            dict[term] = new GlossaryEntry(term, def);
        }

        // 2. Definition lists: Term\n: Definition
        foreach (Match m in DefListRegex.Matches(markdown))
        {
            string term = m.Groups[1].Value.Trim();
            string def = m.Groups[2].Value.Trim();
            if (!dict.ContainsKey(term))
            {
                dict[term] = new GlossaryEntry(term, def);
            }
        }

        return dict.Values.OrderBy(e => e.Term, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Injects hoverable <abbr title="..."> spans into HTML markup for defined terms.
    /// </summary>
    public static string InjectAbbrTooltips(string html, IEnumerable<GlossaryEntry> glossary)
    {
        if (string.IsNullOrWhiteSpace(html))
            return html;

        string result = html;
        foreach (var entry in glossary)
        {
            // Match whole word only, not inside existing tags
            string pattern = $@"\b({Regex.Escape(entry.Term)})\b(?![^<]*>)";
            result = Regex.Replace(result, pattern, $"<abbr title=\"{System.Net.WebUtility.HtmlEncode(entry.Definition)}\" class=\"ms-glossary-term\">$1</abbr>", RegexOptions.IgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Generates an alphabetical Markdown Glossary Appendix table.
    /// </summary>
    public static string GenerateGlossaryAppendixMarkdown(IEnumerable<GlossaryEntry> glossary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Glossary & Abbreviations");
        sb.AppendLine();
        sb.AppendLine("| Term | Definition |");
        sb.AppendLine("| :--- | :--- |");

        foreach (var e in glossary.OrderBy(g => g.Term, StringComparer.OrdinalIgnoreCase))
        {
            string safeTerm = e.Term.Replace("|", "\\|");
            string safeDef = e.Definition.Replace("|", "\\|");
            sb.AppendLine($"| **{safeTerm}** | {safeDef} |");
        }

        return sb.ToString().TrimEnd();
    }
}
