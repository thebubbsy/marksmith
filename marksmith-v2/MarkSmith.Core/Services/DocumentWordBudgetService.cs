using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record SectionWordMetric(
    string HeadingTitle,
    int HeadingLevel,
    int ActualWords,
    int? BudgetWords,
    double ProgressPercentage,
    int RemainingWords,
    bool IsOverBudget);

public class DocumentWordBudgetReport
{
    public int TotalActualWords { get; set; }
    public int? OverallBudgetWords { get; set; }
    public double OverallProgressPercentage { get; set; }
    public int OverallRemainingWords { get; set; }
    public bool IsOverallOverBudget => OverallBudgetWords.HasValue && TotalActualWords > OverallBudgetWords.Value;
    public List<SectionWordMetric> Sections { get; } = new();
}

/// <summary>
/// Service for calculating section-level and document-level word budgets, progress gauges, and length metrics in Markdown.
/// </summary>
public static class DocumentWordBudgetService
{
    private static readonly Regex DocBudgetRegex = new(@"<!--\s*(?:doc-)?budget:\s*(\d+)\s*(?:words)?\s*-->", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SectionBudgetRegex = new(@"<!--\s*section-budget:\s*(\d+)\s*(?:words)?\s*-->", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\b\w+\b", RegexOptions.Compiled);

    /// <summary>
    /// Analyzes word counts per section and evaluates them against defined budgets.
    /// </summary>
    public static DocumentWordBudgetReport Analyze(string markdown)
    {
        var report = new DocumentWordBudgetReport();
        if (string.IsNullOrWhiteSpace(markdown))
            return report;

        // Check for document-level budget
        var docMatch = DocBudgetRegex.Match(markdown);
        if (docMatch.Success && int.TryParse(docMatch.Groups[1].Value, out int docBudget))
        {
            report.OverallBudgetWords = docBudget;
        }

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentHeading = "Preamble";
        int currentLevel = 1;
        int? currentSectionBudget = null;
        var currentSectionLines = new List<string>();

        void FlushSection()
        {
            string cleanLines = Regex.Replace(string.Join(" ", currentSectionLines), @"<!--[\s\S]*?-->", "").Trim();
            int words = WordRegex.Matches(cleanLines).Count;

            if (words == 0 && currentHeading == "Preamble")
            {
                currentSectionLines.Clear();
                currentSectionBudget = null;
                return;
            }

            double pct = currentSectionBudget.HasValue && currentSectionBudget.Value > 0
                ? Math.Round(((double)words / currentSectionBudget.Value) * 100.0, 1)
                : 100.0;

            int remaining = currentSectionBudget.HasValue
                ? Math.Max(0, currentSectionBudget.Value - words)
                : 0;

            bool over = currentSectionBudget.HasValue && words > currentSectionBudget.Value;

            report.Sections.Add(new SectionWordMetric(
                currentHeading,
                currentLevel,
                words,
                currentSectionBudget,
                pct,
                remaining,
                over));

            currentSectionLines.Clear();
            currentSectionBudget = null;
        }

        foreach (var line in lines)
        {
            var hMatch = HeadingRegex.Match(line);
            if (hMatch.Success)
            {
                FlushSection();
                currentLevel = hMatch.Groups[1].Value.Length;
                currentHeading = hMatch.Groups[2].Value.Trim();

                var sMatch = SectionBudgetRegex.Match(currentHeading);
                if (sMatch.Success && int.TryParse(sMatch.Groups[1].Value, out int sBudget))
                {
                    currentSectionBudget = sBudget;
                    currentHeading = SectionBudgetRegex.Replace(currentHeading, "").Trim();
                }
                continue;
            }

            var inlineSecMatch = SectionBudgetRegex.Match(line);
            if (inlineSecMatch.Success && int.TryParse(inlineSecMatch.Groups[1].Value, out int sb))
            {
                currentSectionBudget = sb;
            }

            currentSectionLines.Add(line);
        }

        FlushSection();

        report.TotalActualWords = report.Sections.Sum(s => s.ActualWords);
        if (report.OverallBudgetWords.HasValue && report.OverallBudgetWords.Value > 0)
        {
            report.OverallProgressPercentage = Math.Round(((double)report.TotalActualWords / report.OverallBudgetWords.Value) * 100.0, 1);
            report.OverallRemainingWords = Math.Max(0, report.OverallBudgetWords.Value - report.TotalActualWords);
        }
        else
        {
            report.OverallProgressPercentage = 100.0;
            report.OverallRemainingWords = 0;
        }

        return report;
    }
}
