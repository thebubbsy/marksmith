using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record DocumentFigureItem(
    int Number,
    string Caption,
    string ImageUri,
    int LineNumber,
    string AnchorId);

public record DocumentTableCaptionItem(
    int Number,
    string Caption,
    int LineNumber,
    string AnchorId);

public class FiguresAndTablesManifest
{
    public List<DocumentFigureItem> Figures { get; } = new();
    public List<DocumentTableCaptionItem> Tables { get; } = new();
}

/// <summary>
/// Service that scans Markdown documents for captioned figures and tables and generates preliminary matter indexes.
/// </summary>
public static class TableOfFiguresService
{
    private static readonly Regex FigureImageRegex = new(
        @"!\[(?:Figure\s+(\d+)[:.]\s*)?([^\]]*)\]\(([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FigureStandaloneCaptionRegex = new(
        @"^\*(?:Figure\s+(\d+)[:.]\s*)([^*]+)\*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TableCaptionRegex = new(
        @"^(?:Table\s+(\d+)[:.]\s*|\*Table\s+(\d+)[:.]\s*)([^*\n\r|]+)(?:\*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scans Markdown document and extracts all numbered figure and table items.
    /// </summary>
    public static FiguresAndTablesManifest ExtractManifest(string markdown)
    {
        var manifest = new FiguresAndTablesManifest();
        if (string.IsNullOrWhiteSpace(markdown))
            return manifest;

        if (!markdown.Contains("Figure", StringComparison.OrdinalIgnoreCase) &&
            !markdown.Contains("Table", StringComparison.OrdinalIgnoreCase) &&
            !markdown.Contains("![", StringComparison.Ordinal))
        {
            return manifest;
        }

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int autoFig = 1, autoTbl = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i].Trim();

            // 1. Figures via Image tags: ![Figure 1: Architecture](path)
            var figMatch = FigureImageRegex.Match(line);
            if (figMatch.Success)
            {
                int num = figMatch.Groups[1].Success && int.TryParse(figMatch.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : autoFig++;
                string caption = figMatch.Groups[2].Value.Trim();
                string uri = figMatch.Groups[3].Value.Trim();
                string anchor = $"fig-{num}";
                manifest.Figures.Add(new DocumentFigureItem(num, caption, uri, lineNum, anchor));
                continue;
            }

            // 2. Standalone Figure captions: *Figure 1: Architecture*
            var standFigMatch = FigureStandaloneCaptionRegex.Match(line);
            if (standFigMatch.Success)
            {
                int num = standFigMatch.Groups[1].Success && int.TryParse(standFigMatch.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : autoFig++;
                string caption = standFigMatch.Groups[2].Value.Trim();
                string anchor = $"fig-{num}";
                manifest.Figures.Add(new DocumentFigureItem(num, caption, "", lineNum, anchor));
                continue;
            }

            // 3. Table captions: Table 1: Performance Summary
            var tblMatch = TableCaptionRegex.Match(line);
            if (tblMatch.Success)
            {
                string numStr = tblMatch.Groups[1].Success ? tblMatch.Groups[1].Value : tblMatch.Groups[2].Value;
                int num = int.TryParse(numStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : autoTbl++;
                string caption = tblMatch.Groups[3].Value.Trim();
                string anchor = $"tbl-{num}";
                manifest.Tables.Add(new DocumentTableCaptionItem(num, caption, lineNum, anchor));
            }
        }

        return manifest;
    }

    /// <summary>
    /// Generates Markdown List of Figures preliminary page.
    /// </summary>
    public static string GenerateListOfFiguresMarkdown(IEnumerable<DocumentFigureItem> figures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## List of Figures");
        sb.AppendLine();
        foreach (var fig in figures)
        {
            sb.AppendLine($"- **Figure {fig.Number}**: [{fig.Caption}](#{fig.AnchorId})");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generates Markdown List of Tables preliminary page.
    /// </summary>
    public static string GenerateListOfTablesMarkdown(IEnumerable<DocumentTableCaptionItem> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## List of Tables");
        sb.AppendLine();
        foreach (var tbl in tables)
        {
            sb.AppendLine($"- **Table {tbl.Number}**: [{tbl.Caption}](#{tbl.AnchorId})");
        }
        return sb.ToString().TrimEnd();
    }
}
