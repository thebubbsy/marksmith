using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    public class TocItem
    {
        public int Level { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
    }

    public class TocInjectorResult
    {
        public string ProcessedMarkdown { get; set; } = string.Empty;
        public List<TocItem> Items { get; set; } = new List<TocItem>();
        public bool TocInjected { get; set; }
    }

    public class TocAnchorInjectorService
    {
        public TocInjectorResult Process(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return new TocInjectorResult { ProcessedMarkdown = markdown ?? string.Empty };
            }

            var items = new List<TocItem>();
            var slugCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string[] lines = markdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool inCodeBlock = false;
            var sbMarkdown = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
                {
                    inCodeBlock = !inCodeBlock;
                    sbMarkdown.AppendLine(line);
                    continue;
                }

                if (!inCodeBlock)
                {
                    var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                    if (match.Success)
                    {
                        int level = match.Groups[1].Value.Length;
                        string title = match.Groups[2].Value.Trim();

                        string baseSlug = GenerateSlug(title);
                        string finalSlug = baseSlug;

                        if (slugCounts.ContainsKey(baseSlug))
                        {
                            slugCounts[baseSlug]++;
                            finalSlug = $"{baseSlug}-{slugCounts[baseSlug]}";
                        }
                        else
                        {
                            slugCounts[baseSlug] = 0;
                        }

                        items.Add(new TocItem
                        {
                            Level = level,
                            Title = title,
                            Slug = finalSlug
                        });

                        // Inject anchor tag before heading or append id
                        sbMarkdown.AppendLine($"<a id=\"{finalSlug}\"></a>");
                        sbMarkdown.AppendLine(line);
                        continue;
                    }
                }

                sbMarkdown.AppendLine(line);
            }

            string processed = sbMarkdown.ToString().TrimEnd();

            // Inject [TOC] if present
            bool tocInjected = false;
            if (processed.Contains("[TOC]") && items.Count > 0)
            {
                var sbToc = new StringBuilder();
                sbToc.AppendLine("### Table of Contents");
                sbToc.AppendLine();

                int minLevel = int.MaxValue;
                foreach (var item in items)
                {
                    if (item.Level < minLevel) minLevel = item.Level;
                }

                foreach (var item in items)
                {
                    int indent = Math.Max(0, item.Level - minLevel);
                    string indentStr = new string(' ', indent * 2);
                    sbToc.AppendLine($"{indentStr}- [{item.Title}](#{item.Slug})");
                }

                processed = processed.Replace("[TOC]", sbToc.ToString().TrimEnd());
                tocInjected = true;
            }

            return new TocInjectorResult
            {
                ProcessedMarkdown = processed,
                Items = items,
                TocInjected = tocInjected
            };
        }

        private static string GenerateSlug(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "heading";

            // Strip markdown bold/italic/code formatting
            string clean = Regex.Replace(text, @"[*_`~]", "");
            clean = Regex.Replace(clean, @"\[([^\]]+)\]\([^\)]+\)", "$1"); // links

            string slug = clean.ToLowerInvariant();
            slug = Regex.Replace(slug, @"[^a-z0-9\s-]", "");
            slug = Regex.Replace(slug, @"\s+", "-").Trim('-');

            return string.IsNullOrEmpty(slug) ? "heading" : slug;
        }
    }
}
