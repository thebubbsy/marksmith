using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Typography;

public class BrailleCell
{
    public char Character { get; set; }
    public bool[] Dots { get; } = new bool[6]; // dot 1 to 6
}

public class BrailleModel
{
    public string Title { get; set; } = "Braille Text";
    public string Text { get; set; } = "HELLO";
    public List<BrailleCell> Cells { get; } = new();
}

/// <summary>
/// Service for translating alphanumeric text to standard 6-dot Braille tactile cell matrices and rendering SVG cards.
/// </summary>
public static class BrailleMatrixRendererService
{
    private static readonly Regex BrailleFenceRegex = new(
        @":::braille([^\r\n]*)\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    // Standard 6-dot English Braille Grade 1 mappings (dot indices 0..5 -> dots 1..6)
    private static readonly Dictionary<char, byte> BraillePatterns = new()
    {
        { 'A', 0b000001 }, { 'B', 0b000011 }, { 'C', 0b001001 }, { 'D', 0b011001 },
        { 'E', 0b010001 }, { 'F', 0b001011 }, { 'G', 0b011011 }, { 'H', 0b010011 },
        { 'I', 0b001010 }, { 'J', 0b011010 }, { 'K', 0b000101 }, { 'L', 0b000111 },
        { 'M', 0b001101 }, { 'N', 0b011101 }, { 'O', 0b010101 }, { 'P', 0b001111 },
        { 'Q', 0b011111 }, { 'R', 0b010111 }, { 'S', 0b001110 }, { 'T', 0b011110 },
        { 'U', 0b100101 }, { 'V', 0b100111 }, { 'W', 0b111010 }, { 'X', 0b101101 },
        { 'Y', 0b111101 }, { 'Z', 0b110101 }, { ' ', 0b000000 }
    };

    public static BrailleModel ParseBraille(string blockText, string defaultText = "HELLO")
    {
        var model = new BrailleModel { Title = "Braille Matrix", Text = defaultText };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            PopulateCells(model);
            return model;
        }

        var fence = BrailleFenceRegex.Match(blockText);
        string raw = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Text = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Text = header;

            string body = fence.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(body)) model.Text = body;
        }

        PopulateCells(model);
        return model;
    }

    private static void PopulateCells(BrailleModel model)
    {
        foreach (char rawChar in model.Text.ToUpperInvariant())
        {
            var cell = new BrailleCell { Character = rawChar };
            if (BraillePatterns.TryGetValue(rawChar, out byte pattern))
            {
                for (int i = 0; i < 6; i++)
                {
                    cell.Dots[i] = (pattern & (1 << i)) != 0;
                }
            }
            model.Cells.Add(cell);
        }
    }

    public static string RenderBrailleSvg(BrailleModel model)
    {
        double cellW = 36;
        double width = Math.Max(320, model.Cells.Count * cellW + 60);
        double height = 160;
        double ox = 30;
        double oy = 55;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-braille-svg\">");
        sb.AppendLine("""
            <style>
              .br-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .br-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .br-dot-active { fill: #38bdf8; }
              .br-dot-inactive { fill: #334155; }
              .br-char { font-family: monospace; font-size: 12px; font-weight: 700; fill: #94a3b8; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"br-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"br-title\">Braille: {System.Net.WebUtility.HtmlEncode(model.Text)}</text>");

        // Dot offsets for 6-dot matrix (dots 1..3 on left column, dots 4..6 on right column)
        (double dx, double dy)[] dotCoords =
        {
            (6, 6),   // Dot 1
            (6, 20),  // Dot 2
            (6, 34),  // Dot 3
            (20, 6),  // Dot 4
            (20, 20), // Dot 5
            (20, 34)  // Dot 6
        };

        for (int c = 0; c < model.Cells.Count; c++)
        {
            var cell = model.Cells[c];
            double cx = ox + c * cellW;

            for (int d = 0; d < 6; d++)
            {
                bool active = cell.Dots[d];
                string dotClass = active ? "br-dot-active" : "br-dot-inactive";
                double r = active ? 4 : 2.5;
                sb.AppendLine($"  <circle cx=\"{cx + dotCoords[d].dx}\" cy=\"{oy + dotCoords[d].dy}\" r=\"{r}\" class=\"{dotClass}\" />");
            }

            sb.AppendLine($"  <text x=\"{cx + 13}\" y=\"{oy + 58}\" class=\"br-char\">{cell.Character}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
