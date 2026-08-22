using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class SoilConsolidationModel
{
    public string Title { get; set; } = "1D Terzaghi Soil Consolidation Settlement";
    public double LayerThicknessM { get; set; } = 4.0;    // H (m)
    public double InitialVoidRatio { get; set; } = 1.10;  // e0
    public double CompressionIndex { get; set; } = 0.35;  // Cc
    public double InitialStressKPa { get; set; } = 80.0;  // sigma0' (kPa)
    public double StressIncrementKPa { get; set; } = 60.0;// Delta sigma (kPa)
    public double ConsolidationCoeffCv { get; set; } = 2.5; // Cv (m2/year)

    // Primary Consolidation Settlement Sc = (Cc * H / (1 + e0)) * log10((sigma0 + d_sigma) / sigma0)
    public double PrimarySettlementM => (CompressionIndex * LayerThicknessM / (1.0 + InitialVoidRatio)) *
                                        Math.Log10((InitialStressKPa + StressIncrementKPa) / InitialStressKPa);
    public double PrimarySettlementMm => PrimarySettlementM * 1000.0;

    // Time for 50% consolidation t50 (Tv50 = 0.197) in years (assume two-way drainage d = H/2)
    public double DrainageDistM => LayerThicknessM / 2.0;
    public double Time50Years => (0.197 * Math.Pow(DrainageDistM, 2)) / Math.Max(0.01, ConsolidationCoeffCv);
    public double Time90Years => (0.848 * Math.Pow(DrainageDistM, 2)) / Math.Max(0.01, ConsolidationCoeffCv);
}

public static class SoilConsolidationService
{
    private static readonly Regex ConsolidationFenceRegex = new(
        @":::(?:consolidation|soil-settlement|terzaghi-settlement)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ThicknessRegex = new(
        @"(?:h|thickness|depth)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VoidRegex = new(
        @"(?:e0|void|void_ratio)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CcRegex = new(
        @"(?:cc|compression_index)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Sigma0Regex = new(
        @"(?:sigma0|initial_stress|s0)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DSigmaRegex = new(
        @"(?:d_sigma|delta_sigma|stress_inc|ds)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[kK][pP]a)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SoilConsolidationModel ParseConsolidation(string blockText, string defaultTitle = "1D Terzaghi Soil Consolidation Settlement")
    {
        var model = new SoilConsolidationModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = ConsolidationFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var hm = ThicknessRegex.Match(header);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.LayerThicknessM = Math.Clamp(h, 0.5, 50.0);

            var em = VoidRegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e0))
                model.InitialVoidRatio = Math.Clamp(e0, 0.2, 5.0);

            var ccm = CcRegex.Match(header);
            if (ccm.Success && double.TryParse(ccm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cc))
                model.CompressionIndex = Math.Clamp(cc, 0.05, 2.0);

            var s0m = Sigma0Regex.Match(header);
            if (s0m.Success && double.TryParse(s0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s0))
                model.InitialStressKPa = Math.Clamp(s0, 5.0, 2000.0);

            var dsm = DSigmaRegex.Match(header);
            if (dsm.Success && double.TryParse(dsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ds))
                model.StressIncrementKPa = Math.Clamp(ds, 1.0, 2000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var hm = ThicknessRegex.Match(l);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.LayerThicknessM = Math.Clamp(h, 0.5, 50.0);

            var em = VoidRegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e0))
                model.InitialVoidRatio = Math.Clamp(e0, 0.2, 5.0);

            var ccm = CcRegex.Match(l);
            if (ccm.Success && double.TryParse(ccm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cc))
                model.CompressionIndex = Math.Clamp(cc, 0.05, 2.0);

            var s0m = Sigma0Regex.Match(l);
            if (s0m.Success && double.TryParse(s0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s0))
                model.InitialStressKPa = Math.Clamp(s0, 5.0, 2000.0);

            var dsm = DSigmaRegex.Match(l);
            if (dsm.Success && double.TryParse(dsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ds))
                model.StressIncrementKPa = Math.Clamp(ds, 1.0, 2000.0);
        }

        return model;
    }

    public static string RenderConsolidationSvg(SoilConsolidationModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 75;
        double colW = 240;
        double colH = 150;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-consolidation-svg\">");
        sb.AppendLine("""
            <style>
              .sc-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .sc-sand { fill: #d97706; fill-opacity: 0.25; stroke: #f59e0b; stroke-width: 1.2; }
              .sc-clay { fill: #78716c; fill-opacity: 0.3; stroke: #a8a29e; stroke-width: 1.5; }
              .sc-settled { fill: #f43f5e; fill-opacity: 0.3; stroke: #f43f5e; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .sc-isochrone { fill: none; stroke: #38bdf8; stroke-width: 1.8; }
              .sc-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .sc-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .sc-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sc-title\">🏗 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"sc-meta\">H = {model.LayerThicknessM:F1}m • e0 = {model.InitialVoidRatio:F2} • Cc = {model.CompressionIndex:F2} • Δσ = {model.StressIncrementKPa:F0} kPa</text>");

        // Upper Sand Drainage Layer
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{oy}\" width=\"{colW}\" height=\"16\" class=\"sc-sand\" />");
        sb.AppendLine($"  <text x=\"{ox + colW / 2}\" y=\"{oy + 11}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f59e0b\" text-anchor=\"middle\">Upper Drainage Sand Layer</text>");

        // Consolidating Clay Stratum
        double clayY = oy + 16;
        double clayH = colH - 32;
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{clayY}\" width=\"{colW}\" height=\"{clayH}\" class=\"sc-clay\" />");

        // Lower Sand Drainage Layer
        double lowerSandY = clayY + clayH;
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{lowerSandY}\" width=\"{colW}\" height=\"16\" class=\"sc-sand\" />");
        sb.AppendLine($"  <text x=\"{ox + colW / 2}\" y=\"{lowerSandY + 11}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f59e0b\" text-anchor=\"middle\">Lower Drainage Sand Layer</text>");

        // Pore Pressure Dissipation Isochrone (Bulge profile in middle of clay)
        double isoMidY = clayY + clayH / 2.0;
        double isoMaxBulgeX = ox + colW * 0.70;
        string isoPath = $"M {ox + 30} {clayY} Q {isoMaxBulgeX} {isoMidY} {ox + 30} {lowerSandY}";
        sb.AppendLine($"  <path d=\"{isoPath}\" class=\"sc-isochrone\" />");
        sb.AppendLine($"  <text x=\"{isoMaxBulgeX + 6}\" y=\"{isoMidY + 3}\" font-family=\"monospace\" font-size=\"8\" fill=\"#38bdf8\">Pore Press u(z,t)</text>");

        // Settlement S_c indicator line at top
        double scPix = Math.Clamp(model.PrimarySettlementMm * 0.15, 6.0, 24.0);
        sb.AppendLine($"  <rect x=\"{ox}\" y=\"{oy - scPix}\" width=\"{colW}\" height=\"{scPix}\" class=\"sc-settled\" />");
        sb.AppendLine($"  <text x=\"{ox + colW + 6}\" y=\"{oy - scPix / 2 + 3}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f43f5e\">Sc = {model.PrimarySettlementMm:F0}mm</text>");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"sc-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"sc-lbl\">Primary Settlement (Sc):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"sc-val\" font-size=\"14\" fill=\"#f43f5e\">{model.PrimarySettlementMm:F1} mm</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"sc-lbl\">Time for 50% Lock (t50):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"sc-val\" fill=\"#10b981\">{model.Time50Years:F2} years</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"sc-lbl\">Time for 90% Lock (t90):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"sc-val\">{model.Time90Years:F2} years</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"sc-lbl\">Stress Ratio ((σ₀+Δσ)/σ₀):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"sc-val\" fill=\"#fbbf24\">{(model.InitialStressKPa + model.StressIncrementKPa) / model.InitialStressKPa:F2}x</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Terzaghi 1D Consolidation</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
