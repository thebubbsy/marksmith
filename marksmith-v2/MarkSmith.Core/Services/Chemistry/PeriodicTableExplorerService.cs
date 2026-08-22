using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Chemistry;

public record PeriodicElement(int Number, string Symbol, string Name, string Category, int Period, int Group);

public class PeriodicTableModel
{
    public string Title { get; set; } = "Periodic Table";
    public string? HighlightCategory { get; set; }
    public string? FocusSymbol { get; set; }
    public List<PeriodicElement> Elements { get; } = new();
}

/// <summary>
/// Service for parsing periodic table spotlight directives and rendering interactive SVG elemental matrix grids.
/// </summary>
public static class PeriodicTableExplorerService
{
    private static readonly Regex TableFenceRegex = new(
        @":::periodic-table(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex HighlightRegex = new(
        @"highlight\s*:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FocusRegex = new(
        @"focus\s*:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly List<PeriodicElement> CoreElements = new()
    {
        new(1, "H", "Hydrogen", "nonmetal", 1, 1),
        new(2, "He", "Helium", "noble-gas", 1, 18),
        new(3, "Li", "Lithium", "alkali", 2, 1),
        new(4, "Be", "Beryllium", "alkaline", 2, 2),
        new(5, "B", "Boron", "metalloid", 2, 13),
        new(6, "C", "Carbon", "nonmetal", 2, 14),
        new(7, "N", "Nitrogen", "nonmetal", 2, 15),
        new(8, "O", "Oxygen", "nonmetal", 2, 16),
        new(9, "F", "Fluorine", "halogen", 2, 17),
        new(10, "Ne", "Neon", "noble-gas", 2, 18),
        new(11, "Na", "Sodium", "alkali", 3, 1),
        new(12, "Mg", "Magnesium", "alkaline", 3, 2),
        new(13, "Al", "Aluminium", "metal", 3, 13),
        new(14, "Si", "Silicon", "metalloid", 3, 14),
        new(15, "P", "Phosphorus", "nonmetal", 3, 15),
        new(16, "S", "Sulfur", "nonmetal", 3, 16),
        new(17, "Cl", "Chlorine", "halogen", 3, 17),
        new(18, "Ar", "Argon", "noble-gas", 3, 18),
        new(79, "Au", "Gold", "transition", 6, 11),
        new(47, "Ag", "Silver", "transition", 5, 11),
        new(26, "Fe", "Iron", "transition", 4, 8),
        new(29, "Cu", "Copper", "transition", 4, 11)
    };

    /// <summary>
    /// Parses a periodic-table block into a model.
    /// </summary>
    public static PeriodicTableModel ParsePeriodicTable(string blockText, string defaultTitle = "Periodic Table")
    {
        var model = new PeriodicTableModel { Title = defaultTitle };
        model.Elements.AddRange(CoreElements);

        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = TableFenceRegex.Match(blockText);
        string text = fence.Success ? (fence.Groups[2].Value + " " + fence.Groups[3].Value) : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var hm = HighlightRegex.Match(text);
        if (hm.Success) model.HighlightCategory = hm.Groups[1].Value.ToLowerInvariant();

        var fm = FocusRegex.Match(text);
        if (fm.Success) model.FocusSymbol = fm.Groups[1].Value.ToUpperInvariant();

        return model;
    }

    /// <summary>
    /// Renders an SVG periodic table matrix grid.
    /// </summary>
    public static string RenderPeriodicTableSvg(PeriodicTableModel model)
    {
        double width = 540;
        double height = 240;
        double cellW = 26;
        double cellH = 26;
        double ox = 25;
        double oy = 55;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-ptable-svg\">");
        sb.AppendLine("""
            <style>
              .pt-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .pt-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .elem-box { rx: 3; stroke-width: 1; }
              .elem-num { font-family: monospace; font-size: 6px; fill: #94a3b8; }
              .elem-sym { font-family: Segoe UI, sans-serif; font-size: 9px; font-weight: 700; fill: #ffffff; text-anchor: middle; }
              .cat-nonmetal { fill: #047857; stroke: #10b981; }
              .cat-noble-gas { fill: #6d28d9; stroke: #8b5cf6; }
              .cat-alkali { fill: #b91c1c; stroke: #ef4444; }
              .cat-alkaline { fill: #c2410c; stroke: #f97316; }
              .cat-metalloid { fill: #b45309; stroke: #f59e0b; }
              .cat-halogen { fill: #0369a1; stroke: #0ea5e9; }
              .cat-transition { fill: #334155; stroke: #64748b; }
              .elem-focus { stroke: #eab308 !important; stroke-width: 2.5 !important; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pt-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pt-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        foreach (var elem in model.Elements)
        {
            double ex = ox + (elem.Group - 1) * (cellW + 2);
            double ey = oy + (elem.Period - 1) * (cellH + 2);

            string catClass = "cat-" + elem.Category;
            bool isFocus = string.Equals(elem.Symbol, model.FocusSymbol, StringComparison.OrdinalIgnoreCase);
            string focusClass = isFocus ? "elem-focus" : "";

            sb.AppendLine($"  <g transform=\"translate({ex}, {ey})\">");
            sb.AppendLine($"    <rect width=\"{cellW}\" height=\"{cellH}\" class=\"elem-box {catClass} {focusClass}\" />");
            sb.AppendLine($"    <text x=\"2\" y=\"7\" class=\"elem-num\">{elem.Number}</text>");
            sb.AppendLine($"    <text x=\"{cellW / 2}\" y=\"18\" class=\"elem-sym\">{elem.Symbol}</text>");
            sb.AppendLine("  </g>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
