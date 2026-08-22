using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services
{
    public sealed class DocumentOutlineHeatmapService
    {
        // Compiled once — Analyze used to rebuild this interpreted regex on every call.
        private static readonly Regex HeadingRe = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex DiagramRe = new(@"(:::smartart|```mermaid|:::workflow|:::timeline|:::canvas)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MathRe = new(@"(\$\$|\$[^\$]+\$|\\\[|\\\()", RegexOptions.Compiled);
        private static readonly Regex TableRe = new(@"^\|.+\|$", RegexOptions.Multiline | RegexOptions.Compiled);

        public sealed record SectionHeatmapEntry(
            int Level,
            string HeadingText,
            int WordCount,
            TimeSpan EstimatedReadingTime,
            int DiagramCount,
            int MathBlockCount,
            int TableCount,
            double DensityScore,
            string HeatmapColorHex);

        public sealed record DocumentHeatmapSummary(
            int TotalWords,
            TimeSpan TotalReadingTime,
            int TotalDiagrams,
            int TotalEquations,
            int TotalTables,
            List<SectionHeatmapEntry> Sections);

        public DocumentHeatmapSummary Analyze(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return new DocumentHeatmapSummary(0, TimeSpan.Zero, 0, 0, 0, new List<SectionHeatmapEntry>());
            }

            var matches = HeadingRe.Matches(markdown);
            var sections = new List<SectionHeatmapEntry>();

            int totalWords = 0;
            int totalDiagrams = 0;
            int totalEquations = 0;
            int totalTables = 0;

            if (matches.Count == 0)
            {
                var entry = AnalyzeSection(1, "Document Overview", markdown);
                sections.Add(entry);
                return new DocumentHeatmapSummary(
                    entry.WordCount,
                    entry.EstimatedReadingTime,
                    entry.DiagramCount,
                    entry.MathBlockCount,
                    entry.TableCount,
                    sections);
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int level = m.Groups[1].Length;
                string heading = m.Groups[2].Value.Trim();

                int start = m.Index + m.Length;
                int end = (i < matches.Count - 1) ? matches[i + 1].Index : markdown.Length;
                string body = (end > start) ? markdown[start..end] : "";

                var entry = AnalyzeSection(level, heading, body);
                sections.Add(entry);

                totalWords += entry.WordCount;
                totalDiagrams += entry.DiagramCount;
                totalEquations += entry.MathBlockCount;
                totalTables += entry.TableCount;
            }

            // Normalize density scores (0.0 to 1.0) and assign colors
            int maxWords = Math.Max(1, sections.Max(s => s.WordCount));
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                double density = Math.Clamp((double)s.WordCount / maxWords, 0.05, 1.0);
                string color = GetHeatmapColor(density);
                sections[i] = s with { DensityScore = density, HeatmapColorHex = color };
            }

            var totalReadingTime = TimeSpan.FromMinutes(totalWords / 220.0);

            return new DocumentHeatmapSummary(
                totalWords,
                totalReadingTime,
                totalDiagrams,
                totalEquations,
                totalTables,
                sections);
        }

        private static SectionHeatmapEntry AnalyzeSection(int level, string heading, string body)
        {
            int words = CountWords(body);
            var readingTime = TimeSpan.FromMinutes(words / 220.0);

            int diagrams = DiagramRe.Matches(body).Count;
            int math = MathRe.Matches(body).Count;
            int tables = TableRe.Matches(body).Count > 1 ? 1 : 0;

            return new SectionHeatmapEntry(
                level,
                heading,
                words,
                readingTime,
                diagrams,
                math,
                tables,
                0.0,
                "#38BDF8");
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string GetHeatmapColor(double density)
        {
            if (density > 0.8) return "#EF4444"; // Red (dense)
            if (density > 0.6) return "#F59E0B"; // Amber
            if (density > 0.4) return "#10B981"; // Green
            if (density > 0.2) return "#3B82F6"; // Blue
            return "#6366F1";                    // Indigo / Violet (light)
        }
    }
}
