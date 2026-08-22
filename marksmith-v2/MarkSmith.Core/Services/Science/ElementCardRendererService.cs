using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Science;

public class ChemicalElementModel
{
    public string Symbol { get; set; } = "Au";
    public string Name { get; set; } = "Gold";
    public int AtomicNumber { get; set; } = 79;
    public double AtomicMass { get; set; } = 196.967;
    public string Category { get; set; } = "Transition Metal";
    public string ElectronConfig { get; set; } = "[Xe] 4f¹⁴ 5d¹⁰ 6s¹";
    public int[] Shells { get; set; } = { 2, 8, 18, 32, 18, 1 };
    public string CategoryColorHex => Category.ToLowerInvariant() switch
    {
        var c when c.Contains("alkali") && !c.Contains("earth") => "#ef4444",
        var c when c.Contains("alkaline") || c.Contains("earth") => "#f97316",
        var c when c.Contains("transition") => "#eab308",
        var c when c.Contains("post") || c.Contains("poor") => "#10b981",
        var c when c.Contains("metalloid") => "#06b6d4",
        var c when c.Contains("noble") => "#a855f7",
        var c when c.Contains("halogen") || c.Contains("reactive") => "#38bdf8",
        var c when c.Contains("lanthanide") || c.Contains("actinide") => "#ec4899",
        _ => "#64748b"
    };
}

public static class ElementCardRendererService
{
    private static readonly Regex ElementFenceRegex = new(
        @":::(?:element|element-card|periodic-element)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NameRegex = new(
        @"name\s*[:=]\s*""?([^""\r\n]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AtomicRegex = new(
        @"atomic(?:_number)?\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MassRegex = new(
        @"mass\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CategoryRegex = new(
        @"category\s*[:=]\s*""?([^""\r\n]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static ChemicalElementModel ParseElement(string blockText, string defaultSymbol = "Au")
    {
        var model = new ChemicalElementModel { Symbol = defaultSymbol };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = ElementFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Symbol = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header) && !header.Contains('=')) model.Symbol = header;

            var nm = NameRegex.Match(header);
            if (nm.Success) model.Name = nm.Groups[1].Value;

            var am = AtomicRegex.Match(header);
            if (am.Success && int.TryParse(am.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int at)) model.AtomicNumber = at;

            var mm = MassRegex.Match(header);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double m)) model.AtomicMass = m;

            var cm = CategoryRegex.Match(header);
            if (cm.Success) model.Category = cm.Groups[1].Value;

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var nm = NameRegex.Match(l);
            if (nm.Success) model.Name = nm.Groups[1].Value;

            var am = AtomicRegex.Match(l);
            if (am.Success && int.TryParse(am.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int at)) model.AtomicNumber = at;

            var mm = MassRegex.Match(l);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double m)) model.AtomicMass = m;

            var cm = CategoryRegex.Match(l);
            if (cm.Success) model.Category = cm.Groups[1].Value;
        }

        return model;
    }

    public static string RenderElementSvg(ChemicalElementModel model)
    {
        double width = 420;
        double height = 240;
        double cardW = 160;
        double cardH = 200;
        double cardX = 20;
        double cardY = 20;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-element-card-svg\">");
        sb.AppendLine($$"""
            <style>
              .elem-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .elem-card { fill: #131b2e; stroke: {{model.CategoryColorHex}}; stroke-width: 2; }
              .elem-num { font-family: monospace; font-size: 14px; font-weight: 700; fill: #94a3b8; }
              .elem-sym { font-family: Segoe UI, sans-serif; font-size: 46px; font-weight: 900; fill: {{model.CategoryColorHex}}; text-anchor: middle; }
              .elem-name { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; text-anchor: middle; }
              .elem-mass { font-family: monospace; font-size: 11px; fill: #94a3b8; text-anchor: middle; }
              .elem-title { font-family: Segoe UI, sans-serif; font-size: 14px; font-weight: 700; fill: #f8fafc; }
              .elem-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .elem-orbit { fill: none; stroke: #334155; stroke-width: 1; stroke-dasharray: 2 2; }
              .elem-electron { fill: {{model.CategoryColorHex}}; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"elem-bg\" />");

        // Element Card Tile (Periodic Table Style)
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"{cardW}\" height=\"{cardH}\" rx=\"8\" class=\"elem-card\" />");
        sb.AppendLine($"  <text x=\"{cardX + 14}\" y=\"{cardY + 24}\" class=\"elem-num\">{model.AtomicNumber}</text>");
        sb.AppendLine($"  <text x=\"{cardX + cardW / 2}\" y=\"{cardY + 95}\" class=\"elem-sym\">{System.Net.WebUtility.HtmlEncode(model.Symbol)}</text>");
        sb.AppendLine($"  <text x=\"{cardX + cardW / 2}\" y=\"{cardY + 135}\" class=\"elem-name\">{System.Net.WebUtility.HtmlEncode(model.Name)}</text>");
        sb.AppendLine($"  <text x=\"{cardX + cardW / 2}\" y=\"{cardY + 160}\" class=\"elem-mass\">{model.AtomicMass:F3} u</text>");

        // Category Badge on tile footer
        sb.AppendLine($"  <rect x=\"{cardX + 10}\" y=\"{cardY + 172}\" width=\"{cardW - 20}\" height=\"16\" rx=\"3\" fill=\"{model.CategoryColorHex}\" fill-opacity=\"0.2\" />");
        sb.AppendLine($"  <text x=\"{cardX + cardW / 2}\" y=\"{cardY + 184}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" font-weight=\"700\" fill=\"{model.CategoryColorHex}\" text-anchor=\"middle\">{System.Net.WebUtility.HtmlEncode(model.Category)}</text>");

        // Bohr Shell Model & Metrics on Right
        double bohrX = 305;
        double bohrY = 120;
        sb.AppendLine($"  <text x=\"205\" y=\"35\" class=\"elem-title\">⚛ Element Details</text>");
        sb.AppendLine($"  <text x=\"205\" y=\"55\" class=\"elem-meta\">Electron Config: {model.ElectronConfig}</text>");

        // Concentric Bohr Orbital Rings
        for (int i = 0; i < model.Shells.Length; i++)
        {
            double r = 18 + i * 8.5;
            sb.AppendLine($"  <circle cx=\"{bohrX}\" cy=\"{bohrY + 15}\" r=\"{r:F1}\" class=\"elem-orbit\" />");

            // Electron dots on shells
            int electrons = model.Shells[i];
            for (int e = 0; e < Math.Min(electrons, 8); e++)
            {
                double angle = (e * 360.0 / Math.Min(electrons, 8)) * Math.PI / 180.0;
                double ex = bohrX + r * Math.Cos(angle);
                double ey = bohrY + 15 + r * Math.Sin(angle);
                sb.AppendLine($"  <circle cx=\"{ex:F1}\" cy=\"{ey:F1}\" r=\"2\" class=\"elem-electron\" />");
            }
        }

        // Nucleus
        sb.AppendLine($"  <circle cx=\"{bohrX}\" cy=\"{bohrY + 15}\" r=\"8\" fill=\"{model.CategoryColorHex}\" />");
        sb.AppendLine($"  <text x=\"{bohrX}\" y=\"{bohrY + 18}\" font-family=\"monospace\" font-size=\"8\" font-weight=\"700\" fill=\"#ffffff\" text-anchor=\"middle\">{model.Symbol}</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
