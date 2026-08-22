using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public enum AnchorKind
{
    Heading,
    Equation,
    Figure,
    Table,
    Custom
}

public record DocumentAnchor(
    string Slug,
    string DisplayTitle,
    AnchorKind Kind,
    int LineNumber);

public record CrossReferenceValidation(
    string LinkText,
    string TargetSlug,
    int LineNumber,
    bool IsResolved,
    DocumentAnchor? MatchedAnchor = null);

public class AnchorIndexReport
{
    public List<DocumentAnchor> Anchors { get; } = new();
    public List<CrossReferenceValidation> References { get; } = new();
    public int ResolvedReferencesCount => References.Count(r => r.IsResolved);
    public int BrokenReferencesCount => References.Count(r => !r.IsResolved);
}

/// <summary>
/// Service that builds an anchor index for headings, figures, tables, and equations and validates internal cross-references.
/// </summary>
public static class DocumentAnchorIndexerService
{
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex CustomAnchorRegex = new(@"{#([a-zA-Z0-9_\-:]+)}", RegexOptions.Compiled);
    private static readonly Regex InternalLinkRegex = new(@"(?<!!)\[([^\]]+)\]\(#([a-zA-Z0-9_\-:]+)\)", RegexOptions.Compiled);
    private static readonly Regex FigTableAnchorRegex = new(@"\*(?:Figure|Table)\s+(\d+)[:.]\s*([^*]+)\*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds an anchor index and validates all internal links in the Markdown document.
    /// </summary>
    public static AnchorIndexReport IndexAndValidate(string markdown)
    {
        var report = new AnchorIndexReport();
        if (string.IsNullOrWhiteSpace(markdown))
            return report;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var anchorDict = new Dictionary<string, DocumentAnchor>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: Discover Anchors
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i].Trim();

            // Headings
            var hMatch = HeadingRegex.Match(line);
            if (hMatch.Success)
            {
                string title = hMatch.Groups[2].Value.Trim();
                var customAnchor = CustomAnchorRegex.Match(title);
                string slug;
                if (customAnchor.Success)
                {
                    slug = customAnchor.Groups[1].Value;
                    title = CustomAnchorRegex.Replace(title, "").Trim();
                }
                else
                {
                    slug = GenerateSlug(title);
                }

                var anchor = new DocumentAnchor(slug, title, AnchorKind.Heading, lineNum);
                anchorDict[slug] = anchor;
                report.Anchors.Add(anchor);
                continue;
            }

            // Figures / Tables
            var ftMatch = FigTableAnchorRegex.Match(line);
            if (ftMatch.Success)
            {
                string num = ftMatch.Groups[1].Value;
                string caption = ftMatch.Groups[2].Value.Trim();
                bool isFig = line.StartsWith("*Figure", StringComparison.OrdinalIgnoreCase);
                string slug = isFig ? $"fig-{num}" : $"tbl-{num}";
                var anchor = new DocumentAnchor(slug, caption, isFig ? AnchorKind.Figure : AnchorKind.Table, lineNum);
                anchorDict[slug] = anchor;
                report.Anchors.Add(anchor);
                continue;
            }

            // Custom inline anchors {#foo}
            foreach (Match m in CustomAnchorRegex.Matches(line))
            {
                string slug = m.Groups[1].Value;
                if (!anchorDict.ContainsKey(slug))
                {
                    var anchor = new DocumentAnchor(slug, slug, AnchorKind.Custom, lineNum);
                    anchorDict[slug] = anchor;
                    report.Anchors.Add(anchor);
                }
            }
        }

        // Pass 2: Validate References
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            foreach (Match m in InternalLinkRegex.Matches(line))
            {
                string text = m.Groups[1].Value;
                string target = m.Groups[2].Value;

                bool resolved = anchorDict.TryGetValue(target, out var targetAnchor);
                report.References.Add(new CrossReferenceValidation(text, target, lineNum, resolved, targetAnchor));
            }
        }

        return report;
    }

    private static string GenerateSlug(string text)
    {
        string slug = text.ToLowerInvariant().Trim();
        slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
        slug = Regex.Replace(slug, @"\s+", "-");
        return slug;
    }
}
