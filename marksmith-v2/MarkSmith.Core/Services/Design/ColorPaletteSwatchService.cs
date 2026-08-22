using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Design;

public record SwatchColor(string Name, string HexCode, double Luminance, bool IsDark);

public class PaletteModel
{
    public string Title { get; set; } = "Color Palette";
    public List<SwatchColor> Colors { get; } = new();
}

/// <summary>
/// Service for parsing design tokens and rendering interactive SVG hexagonal color palette swatches.
/// </summary>
public static class ColorPaletteSwatchService
{
    private static readonly Regex PaletteFenceRegex = new(
        @":::palette(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex ColorTokenRegex = new(
        @"([a-zA-Z0-9_\-\s]+):\s*(#[0-9a-fA-F]{6}|#[0-9a-fA-F]{3})",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a palette block and extracts color tokens.
    /// </summary>
    public static PaletteModel ParsePalette(string blockText, string defaultTitle = "Color Palette")
    {
        var model = new PaletteModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = PaletteFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        foreach (Match m in ColorTokenRegex.Matches(text))
        {
            string name = m.Groups[1].Value.Trim();
            string hex = NormalizeHex(m.Groups[2].Value.Trim());
            double lum = CalculateLuminance(hex);
            bool isDark = lum < 0.5;

            model.Colors.Add(new SwatchColor(name, hex, Math.Round(lum, 3), isDark));
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG swatch matrix of hexagonal color chips.
    /// </summary>
    public static string RenderSwatchesSvg(PaletteModel model)
    {
        if (model.Colors.Count == 0)
            return "<svg width=\"300\" height=\"80\"></svg>";

        double chipWidth = 110;
        double chipHeight = 90;
        double cols = Math.Min(5, model.Colors.Count);
        double rows = Math.Ceiling(model.Colors.Count / cols);
        double width = cols * chipWidth + 40;
        double height = rows * chipHeight + 50;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-palette-svg\">");
        sb.AppendLine("""
            <style>
              .p-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .p-name { font-family: Segoe UI, sans-serif; font-size: 10px; font-weight: 600; text-anchor: middle; }
              .p-hex { font-family: Segoe UI, sans-serif; font-size: 9px; fill: #8b949e; text-anchor: middle; font-family: monospace; }
            </style>
            """);

        sb.AppendLine($"  <text x=\"20\" y=\"22\" class=\"p-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        for (int i = 0; i < model.Colors.Count; i++)
        {
            var c = model.Colors[i];
            int r = i / 5;
            int col = i % 5;

            double cx = 20 + col * chipWidth + 45;
            double cy = 40 + r * chipHeight + 35;

            // Hexagonal swatch path or rounded rect
            sb.AppendLine($"  <g transform=\"translate({cx}, {cy})\">");
            sb.AppendLine($"    <rect x=\"-36\" y=\"-22\" width=\"72\" height=\"44\" rx=\"8\" fill=\"{c.HexCode}\" stroke=\"#30363d\" stroke-width=\"1.5\" />");
            string textFill = c.IsDark ? "#ffffff" : "#000000";
            sb.AppendLine($"    <text y=\"4\" fill=\"{textFill}\" class=\"p-name\">{System.Net.WebUtility.HtmlEncode(c.Name)}</text>");
            sb.AppendLine($"    <text y=\"34\" class=\"p-hex\">{c.HexCode.ToUpperInvariant()}</text>");
            sb.AppendLine("  </g>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string NormalizeHex(string hex)
    {
        if (hex.Length == 4) // #RGB
        {
            return $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}";
        }
        return hex;
    }

    private static double CalculateLuminance(string hex)
    {
        if (hex.Length >= 7 &&
            int.TryParse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) &&
            int.TryParse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) &&
            int.TryParse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
        {
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        }
        return 0.5;
    }
}
