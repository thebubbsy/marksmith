using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Games;

public class SudokuModel
{
    public string Title { get; set; } = "Sudoku Grid";
    public int[,] Grid { get; } = new int[9, 9];
    public bool[,] IsGiven { get; } = new bool[9, 9];
}

/// <summary>
/// Service for parsing 9x9 Sudoku puzzle matrix blocks and rendering SVG puzzle boards.
/// </summary>
public static class SudokuGridRendererService
{
    private static readonly Regex SudokuFenceRegex = new(
        @":::sudoku([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static SudokuModel ParseSudoku(string blockText, string defaultTitle = "Sudoku")
    {
        var model = new SudokuModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = SudokuFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        int r = 0;

        foreach (var raw in lines)
        {
            if (r >= 9) break;
            string l = raw.Trim();
            var tokens = l.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int c = 0;

            foreach (var tok in tokens)
            {
                if (c >= 9) break;
                if (int.TryParse(tok, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int num) && num >= 1 && num <= 9)
                {
                    model.Grid[r, c] = num;
                    model.IsGiven[r, c] = true;
                }
                c++;
            }
            if (c > 0) r++;
        }

        return model;
    }

    public static string RenderSudokuSvg(SudokuModel model)
    {
        double cellSize = 30;
        double gridW = cellSize * 9;
        double width = gridW + 60;
        double height = gridW + 80;
        double ox = 30;
        double oy = 55;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-sudoku-svg\">");
        sb.AppendLine("""
            <style>
              .su-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .su-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .su-cell { fill: #1e293b; stroke: #334155; stroke-width: 0.8; }
              .su-num-given { font-family: Segoe UI, monospace; font-size: 14px; font-weight: 700; fill: #38bdf8; text-anchor: middle; }
              .su-box-line { stroke: #94a3b8; stroke-width: 2; fill: none; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"su-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"su-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Cells
        for (int r = 0; r < 9; r++)
        {
            for (int c = 0; c < 9; c++)
            {
                double cx = ox + c * cellSize;
                double cy = oy + r * cellSize;
                sb.AppendLine($"  <rect x=\"{cx}\" y=\"{cy}\" width=\"{cellSize}\" height=\"{cellSize}\" class=\"su-cell\" />");
                if (model.IsGiven[r, c])
                {
                    sb.AppendLine($"  <text x=\"{cx + cellSize / 2}\" y=\"{cy + cellSize / 2 + 5}\" class=\"su-num-given\">{model.Grid[r, c]}</text>");
                }
            }
        }

        // 3x3 Box Dividers
        for (int i = 0; i <= 3; i++)
        {
            double pos = i * cellSize * 3;
            sb.AppendLine($"  <line x1=\"{ox + pos}\" y1=\"{oy}\" x2=\"{ox + pos}\" y2=\"{oy + gridW}\" class=\"su-box-line\" />");
            sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy + pos}\" x2=\"{ox + gridW}\" y2=\"{oy + pos}\" class=\"su-box-line\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
