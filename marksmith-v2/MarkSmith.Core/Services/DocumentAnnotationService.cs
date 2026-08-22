using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record DocumentComment(
    string Id,
    string Author,
    DateTime Timestamp,
    string Content,
    int LineNumber,
    bool IsResolved = false);

public class AnnotationReport
{
    public List<DocumentComment> Comments { get; } = new();
    public int OpenCommentsCount => Comments.Count(c => !c.IsResolved);
    public int ResolvedCommentsCount => Comments.Count(c => c.IsResolved);
    public Dictionary<string, int> AuthorContributionCounts =>
        Comments.GroupBy(c => c.Author).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>
/// Service for parsing, querying, and managing inline collaborative annotations in Markdown files.
/// </summary>
public static class DocumentAnnotationService
{
    private static readonly Regex CommentTagRegex = new(
        @"<!--\s*comment:([a-zA-Z0-9_\-]+)\s+author=""([^""]+)""(?:\s+date=""([^""]+)"")?\s+text=""([^""]+)""(?:\s+resolved=""(true|false)"")?\s*-->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts all comment annotations from the Markdown document into an annotation report.
    /// </summary>
    public static AnnotationReport ExtractAnnotations(string markdown)
    {
        var report = new AnnotationReport();
        if (string.IsNullOrWhiteSpace(markdown))
            return report;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            foreach (Match m in CommentTagRegex.Matches(line))
            {
                string id = m.Groups[1].Value;
                string author = m.Groups[2].Value;
                DateTime timestamp = m.Groups[3].Success && DateTime.TryParse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt
                    : DateTime.UtcNow;
                string content = m.Groups[4].Value;
                bool isResolved = m.Groups[5].Success && bool.TryParse(m.Groups[5].Value, out var res) && res;

                report.Comments.Add(new DocumentComment(id, author, timestamp, content, lineNum, isResolved));
            }
        }

        return report;
    }

    /// <summary>
    /// Creates an inline comment tag ready for embedding in a Markdown document.
    /// </summary>
    public static string CreateCommentTag(string id, string author, string text, bool resolved = false)
    {
        string dateStr = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"<!-- comment:{id} author=\"{author}\" date=\"{dateStr}\" text=\"{text}\" resolved=\"{resolved.ToString().ToLowerInvariant()}\" -->";
    }
}
