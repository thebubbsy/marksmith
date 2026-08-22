using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class KarnaughMapModel
{
    public string Title { get; set; } = "K-Map";
    public int Variables { get; set; } = 2; // 2 or 4
    public int[,] Matrix { get; set; } = new int[2, 2];
}

/// <summary>
/// Service for parsing digital logic truth tables and rendering 2-variable / 4-variable Gray code Karnaugh Maps in SVG.
/// </summary>
public static class KarnaughMapRendererService
{
    private static readonly Regex KmapFenceRegex = new(
        @":::kmap([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex VarsRegex = new(
        @"vars\s*=\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ValuesRegex = new(
        @"values:\s*([0-1\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static KarnaughMapModel ParseKmap(string blockText, string defaultTitle = "Karnaugh Map")
    {
        var model = new KarnaughMapModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = KmapFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var vm = VarsRegex.Match(text);
        if (vm.Success && int.TryParse(vm.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v))
        {
            model.Variables = (v >= 4) ? 4 : 2;
        }

        int rows = model.Variables == 4 ? 4 : 2;
        int cols = model.Variables == 4 ? 4 : 2;
        model.Matrix = new int[rows, cols];

        var valM = ValuesRegex.Match(text);
        if (valM.Success)
        {
            var tokens = valM.Groups[1].Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (idx < tokens.Length && int.TryParse(tokens[idx++], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int bit))
                    {
                        model.Matrix[r, c] = bit == 1 ? 1 : 0;
                    }
                }
            }
        }

        return model;
    }

    public static string RenderKmapSvg(KarnaughMapModel model)
    {
        int rows = model.Variables == 4 ? 4 : 2;
        int cols = model.Variables == 4 ? 4 : 2;
        double cellSize = 38;
        double width = cols * cellSize + 90;
        double height = rows * cellSize + 90;
        double ox = 60;
        double oy = 55;

        string[] rowHeaders = model.Variables == 4 ? new[] { "00", "01", "11", "10" } : new[] { "0", "1" };
        string[] colHeaders = model.Variables == 4 ? new[] { "00", "01", "11", "10" } : new[] { "0", "1" };

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-kmap-svg\">");
        sb.AppendLine("""
            <style>
              .km-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .km-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .km-header { font-family: monospace; font-size: 11px; font-weight: 700; fill: #94a3b8; text-anchor: middle; }
              .km-cell { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .km-val-1 { font-family: monospace; font-size: 14px; font-weight: 700; fill: #38bdf8; text-anchor: middle; }
              .km-val-0 { font-family: monospace; font-size: 14px; font-weight: 400; fill: #64748b; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"km-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"km-title\">{System.Net.WebUtility.HtmlEncode(model.Title)} ({model.Variables}-Var)</text>");

        // Column Headers
        for (int c = 0; c < cols; c++)
        {
            double cx = ox + c * cellSize + cellSize / 2;
            sb.AppendLine($"  <text x=\"{cx}\" y=\"{oy - 8}\" class=\"km-header\">{colHeaders[c]}</text>");
        }

        // Row Headers & Cells
        for (int r = 0; r < rows; r++)
        {
            double ry = oy + r * cellSize + cellSize / 2 + 4;
            sb.AppendLine($"  <text x=\"{ox - 12}\" y=\"{ry}\" class=\"km-header\">{rowHeaders[r]}</text>");

            for (int c = 0; c < cols; c++)
            {
                double cx = ox + c * cellSize;
                double cy = oy + r * cellSize;
                int val = model.Matrix[r, c];
                string valClass = val == 1 ? "km-val-1" : "km-val-0";

                sb.AppendLine($"  <rect x=\"{cx}\" y=\"{cy}\" width=\"{cellSize}\" height=\"{cellSize}\" class=\"km-cell\" />");
                sb.AppendLine($"  <text x=\"{cx + cellSize / 2}\" y=\"{cy + cellSize / 2 + 5}\" class=\"{valClass}\">{val}</text>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
