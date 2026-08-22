using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.MathDiagrams;

public class SorobanModel
{
    public string Title { get; set; } = "Soroban Abacus";
    public long Value { get; set; } = 1492;
    public int Columns { get; set; } = 5;
}

/// <summary>
/// Service for converting decimal integers to 4:1 bi-quinary bead states and rendering SVG Soroban abacus frames.
/// </summary>
public static class SorobanAbacusRendererService
{
    private static readonly Regex AbacusFenceRegex = new(
        @":::abacus(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex ValueRegex = new(
        @"value\s*=\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SorobanModel ParseAbacus(string blockText, string defaultTitle = "Soroban Abacus")
    {
        var model = new SorobanModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = AbacusFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Success ? fence.Groups[1].Value : fence.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(header)) model.Title = header.Trim();
            text = (fence.Groups[2].Value + " " + fence.Groups[3].Value);
        }

        var vm = ValueRegex.Match(text);
        if (vm.Success && long.TryParse(vm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long val))
        {
            model.Value = val;
        }

        string valStr = model.Value.ToString();
        model.Columns = Math.Max(4, Math.Min(10, valStr.Length + 1));
        return model;
    }

    public static string RenderAbacusSvg(SorobanModel model)
    {
        double colW = 34;
        double width = model.Columns * colW + 60;
        double height = 220;
        double ox = 30;
        double frameTop = 50;
        double frameH = 140;
        double beamY = frameTop + 45;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-abacus-svg\">");
        sb.AppendLine("""
            <style>
              .ab-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .ab-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ab-frame { fill: #78350f; stroke: #451a03; stroke-width: 4; rx: 4; }
              .ab-inner { fill: #fef3c7; }
              .ab-rod { stroke: #94a3b8; stroke-width: 2; }
              .ab-beam { fill: #451a03; }
              .ab-bead { fill: #b45309; stroke: #78350f; stroke-width: 1; rx: 3; }
              .ab-val { font-family: monospace; font-size: 11px; font-weight: 700; fill: #38bdf8; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ab-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ab-title\">{System.Net.WebUtility.HtmlEncode(model.Title)} ({model.Value})</text>");

        // Outer Frame & Inner Board
        double frameW = model.Columns * colW;
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{frameTop}\" width=\"{frameW}\" height=\"{frameH}\" class=\"ab-frame\" />");
        sb.AppendLine($"  <rect x=\"{ox + 4}\" y=\"{frameTop + 4}\" width=\"{frameW - 8}\" height=\"{frameH - 8}\" class=\"ab-inner\" />");
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{beamY}\" width=\"{frameW}\" height=\"8\" class=\"ab-beam\" />");

        string valStr = model.Value.ToString().PadLeft(model.Columns, '0');

        for (int c = 0; c < model.Columns; c++)
        {
            double rodX = ox + c * colW + colW / 2;
            sb.AppendLine($"  <line x1=\"{rodX}\" y1=\"{frameTop + 4}\" x2=\"{rodX}\" y2=\"{frameTop + frameH - 4}\" class=\"ab-rod\" />");

            int digit = valStr[c] - '0';
            bool upperActive = digit >= 5;
            int lowerCount = digit % 5;

            // Upper Deck Bead (Heaven bead, value 5)
            double upperY = upperActive ? (beamY - 14) : (frameTop + 8);
            sb.AppendLine($"  <rect x=\"{rodX - 11}\" y=\"{upperY}\" width=\"22\" height=\"12\" class=\"ab-bead\" />");

            // Lower Deck Beads (Earth beads, 4 beads)
            for (int b = 0; b < 4; b++)
            {
                bool beadActive = b < lowerCount;
                double lowerY = beadActive ? (beamY + 12 + b * 13) : (frameTop + frameH - 16 - (3 - b) * 13);
                sb.AppendLine($"  <rect x=\"{rodX - 11}\" y=\"{lowerY}\" width=\"22\" height=\"12\" class=\"ab-bead\" />");
            }

            // Digit label
            sb.AppendLine($"  <text x=\"{rodX}\" y=\"{frameTop + frameH + 18}\" class=\"ab-val\">{digit}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
