using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record NumberedHeading(
    int Level,
    string RawText,
    string CleanText,
    string NumberPrefix,
    int LineNumber,
    bool IsSkipped);

/// <summary>
/// Service that generates semantic hierarchical section numbers (1., 1.1, 1.1.1) for Markdown headings.
/// </summary>
public static class HeaderAutoNumberingService
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex NoNumberRegex = new(@"<!--\s*(?:nonumber|unnumbered)\s*-->", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExistingNumberRegex = new(@"^\d+(?:\.\d+)*\.?\s+", RegexOptions.Compiled);

    /// <summary>
    /// Traverses Markdown headings and computes hierarchical section numbers.
    /// </summary>
    public static List<NumberedHeading> ComputeHeadingNumbers(string markdown)
    {
        var list = new List<NumberedHeading>();
        if (string.IsNullOrWhiteSpace(markdown))
            return list;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int[] counters = new int[7]; // levels 1 to 6

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            var match = HeadingRegex.Match(line);
            if (!match.Success) continue;

            int level = match.Groups[1].Value.Length;
            string raw = match.Groups[2].Value.Trim();

            bool isSkipped = NoNumberRegex.IsMatch(raw);
            string clean = ExistingNumberRegex.Replace(NoNumberRegex.Replace(raw, "").Trim(), "").Trim();

            string numberPrefix = "";
            if (!isSkipped)
            {
                counters[level]++;
                // Reset lower levels
                for (int l = level + 1; l <= 6; l++) counters[l] = 0;

                var parts = new List<int>();
                for (int l = 1; l <= level; l++)
                {
                    if (counters[l] > 0) parts.Add(counters[l]);
                }
                numberPrefix = string.Join(".", parts) + ".";
            }

            list.Add(new NumberedHeading(level, raw, clean, numberPrefix, lineNum, isSkipped));
        }

        return list;
    }

    /// <summary>
    /// Applies auto-numbering prefixes to all headings in a Markdown document.
    /// </summary>
    public static string ApplyNumberingToMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        var headings = ComputeHeadingNumbers(markdown);
        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        foreach (var h in headings)
        {
            if (h.IsSkipped || string.IsNullOrEmpty(h.NumberPrefix)) continue;
            int idx = h.LineNumber - 1;
            if (idx >= 0 && idx < lines.Length)
            {
                string hashes = new string('#', h.Level);
                lines[idx] = $"{hashes} {h.NumberPrefix} {h.CleanText}";
            }
        }

        return string.Join("\n", lines);
    }
}
