using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Citations;

public record CitationOccurrence(string Key, string SectionTitle, string SectionSlug, int LineNumber);
public record CitationBacklinkReport(string Key, List<CitationOccurrence> Occurrences);

/// <summary>
/// Service that tracks in-text citations back to bibliography records and generates reverse hyperlink backlinks.
/// </summary>
public static class CitationBacklinkService
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex CitationKeyRegex = new(@"\[@([a-zA-Z0-9_\-]+)\]", RegexOptions.Compiled);
    private static readonly Regex SlugSanitizeRegex = new(@"[^a-z0-9\s-]", RegexOptions.Compiled);

    /// <summary>
    /// Scans Markdown document and maps each citation key to all its section and line occurrences.
    /// </summary>
    public static Dictionary<string, List<CitationOccurrence>> ExtractCitationBacklinks(string markdown)
    {
        var map = new Dictionary<string, List<CitationOccurrence>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(markdown))
            return map;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentHeading = "Introduction";
        string currentSlug = "introduction";

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            var hMatch = HeadingRegex.Match(line);
            if (hMatch.Success)
            {
                currentHeading = hMatch.Groups[2].Value.Trim();
                currentSlug = SlugSanitizeRegex.Replace(currentHeading.ToLowerInvariant(), "").Trim().Replace(" ", "-");
                if (string.IsNullOrEmpty(currentSlug)) currentSlug = $"section-{i + 1}";
                continue;
            }

            foreach (Match m in CitationKeyRegex.Matches(line))
            {
                string key = m.Groups[1].Value;
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<CitationOccurrence>();
                    map[key] = list;
                }
                list.Add(new CitationOccurrence(key, currentHeading, currentSlug, lineNum));
            }
        }

        return map;
    }

    /// <summary>
    /// Formats a backlink string for a specific citation key (e.g. "↩ cited in §Introduction, §Methods").
    /// </summary>
    public static string FormatBacklinkHtml(string citationKey, Dictionary<string, List<CitationOccurrence>> backlinkMap)
    {
        if (!backlinkMap.TryGetValue(citationKey, out var occurrences) || occurrences.Count == 0)
            return string.Empty;

        var distinctSections = occurrences.GroupBy(o => o.SectionSlug).Select(g => g.First()).ToList();
        var links = distinctSections.Select(s => $"<a href=\"#{s.SectionSlug}\" class=\"ms-cite-backlink\">§{System.Net.WebUtility.HtmlEncode(s.SectionTitle)}</a>");

        return $"<span class=\"ms-citation-backlinks\">&#8617; cited in {string.Join(", ", links)}</span>";
    }
}
