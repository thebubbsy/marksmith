using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkSmith.Core.Services
{
    public enum LinkIssueType
    {
        MissingAnchor,
        MissingLocalFile,
        EmptyUrl,
        InvalidSyntax
    }

    public class LinkIssue
    {
        public string Url { get; set; } = string.Empty;
        public LinkIssueType IssueType { get; set; }
        public string Message { get; set; } = string.Empty;
        public int LineNumber { get; set; }
    }

    public class LinkIntegrityReport
    {
        public List<LinkIssue> Issues { get; set; } = new List<LinkIssue>();
        public int TotalLinksChecked { get; set; }
        public bool IsValid => Issues.Count == 0;
    }

    public class LinkIntegrityAnalyzerService
    {
        public LinkIntegrityReport Analyze(string markdown, string? documentDirectory = null)
        {
            var report = new LinkIntegrityReport();
            if (string.IsNullOrWhiteSpace(markdown)) return report;

            var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
            var doc = Markdown.Parse(markdown, pipeline);

            // 1. Collect all header anchors
            var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var heading in doc.Descendants<HeadingBlock>())
            {
                var text = heading.Inline?.ToPlainText() ?? string.Empty;
                var slug = NormalizeAnchorSlug(text);
                if (!string.IsNullOrEmpty(slug))
                {
                    anchors.Add(slug);
                }
            }

            // 2. Scan all link inlines
            foreach (var link in doc.Descendants<LinkInline>())
            {
                report.TotalLinksChecked++;
                var url = link.Url ?? string.Empty;

                if (string.IsNullOrWhiteSpace(url))
                {
                    report.Issues.Add(new LinkIssue
                    {
                        Url = url,
                        IssueType = LinkIssueType.EmptyUrl,
                        Message = "Link destination URL is empty or null.",
                        LineNumber = link.Line
                    });
                    continue;
                }

                // Check internal anchor reference (#section-slug)
                if (url.StartsWith("#"))
                {
                    var anchorName = url.Substring(1);
                    var slug = NormalizeAnchorSlug(anchorName);
                    if (!anchors.Contains(slug))
                    {
                        report.Issues.Add(new LinkIssue
                        {
                            Url = url,
                            IssueType = LinkIssueType.MissingAnchor,
                            Message = $"Anchor '{url}' does not match any header in the document.",
                            LineNumber = link.Line
                        });
                    }
                    continue;
                }

                // Check local file relative path if documentDirectory is supplied
                if (!string.IsNullOrEmpty(documentDirectory) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    var fullPath = Path.Combine(documentDirectory, url);
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        report.Issues.Add(new LinkIssue
                        {
                            Url = url,
                            IssueType = LinkIssueType.MissingLocalFile,
                            Message = $"Referenced local file/directory '{url}' was not found.",
                            LineNumber = link.Line
                        });
                    }
                }
            }

            return report;
        }

        private static string NormalizeAnchorSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string slug = text.Trim().ToLowerInvariant();
            slug = Regex.Replace(slug, @"[^\w\s-]", "");
            slug = Regex.Replace(slug, @"\s+", "-");
            return slug;
        }
    }

    internal static class InlineExtensions
    {
        public static string ToPlainText(this ContainerInline inline)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var child in inline)
            {
                if (child is LiteralInline literal)
                {
                    sb.Append(literal.Content);
                }
                else if (child is ContainerInline container)
                {
                    sb.Append(container.ToPlainText());
                }
            }
            return sb.ToString();
        }
    }
}
