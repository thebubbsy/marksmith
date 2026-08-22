using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class PavementDesignModel
{
    public string Title { get; set; } = "AASHTO Flexible Pavement Structural Design";
    public double EsalMillions { get; set; } = 5.0;     // Design ESALs W18 (Millions)
    public double ReliabilityPercent { get; set; } = 95.0; // Reliability R (%)
    public double StandardDeviationS0 { get; set; } = 0.35; // S0
    public double DeltaPsi { get; set; } = 1.7;        // Serviceability drop (p0 - pt)
    public double SubgradeMrMpa { get; set; } = 50.0;  // Subgrade Resilient Modulus Mr (MPa)

    // Layer Thicknesses (mm)
    public double AsphaltD1Mm { get; set; } = 100.0;   // D1 (Surface)
    public double BaseD2Mm { get; set; } = 150.0;      // D2 (Crushed Base)
    public double SubbaseD3Mm { get; set; } = 200.0;   // D3 (Granular Subbase)

    // AASHTO Layer Coefficients
    public double A1 => 0.44; // Asphalt Concrete
    public double A2 => 0.14; // Crushed Aggregate Base
    public double A3 => 0.11; // Granular Subbase
    public double M2 => 1.00; // Drainage factor 2
    public double M3 => 1.00; // Drainage factor 3

    // Provided Structural Number SN_prov = a1*D1 + a2*D2*m2 + a3*D3*m3 (in inches)
    public double D1Inches => AsphaltD1Mm / 25.4;
    public double D2Inches => BaseD2Mm / 25.4;
    public double D3Inches => SubbaseD3Mm / 25.4;

    public double SnProvided => (A1 * D1Inches) + (A2 * D2Inches * M2) + (A3 * D3Inches * M3);

    // Standard Normal Deviate Zr for Reliability
    public double Zr => ReliabilityPercent switch
    {
        >= 99.0 => -2.326,
        >= 95.0 => -1.645,
        >= 90.0 => -1.282,
        >= 85.0 => -1.036,
        >= 80.0 => -0.841,
        >= 75.0 => -0.674,
        _ => -0.524
    };

    // Subgrade Modulus in psi (1 MPa = 145.038 psi)
    public double SubgradeMrPsi => SubgradeMrMpa * 145.038;

    // Required Structural Number solved numerically via AASHTO 1993 equation
    public double SnRequired
    {
        get
        {
            double logW18 = Math.Log10(Math.Max(1e4, EsalMillions * 1e6));
            double zr = Zr;
            double s0 = StandardDeviationS0;
            double dPsi = Math.Clamp(DeltaPsi, 0.5, 3.0);
            double mr = Math.Max(1000.0, SubgradeMrPsi);

            // Bisection search for SN in range [1.0, 10.0]
            double low = 1.0, high = 10.0;
            for (int i = 0; i < 30; i++)
            {
                double mid = (low + high) / 2.0;
                double term1 = zr * s0;
                double term2 = 9.36 * Math.Log10(mid + 1.0) - 0.20;
                double term3 = Math.Log10(dPsi / 2.7) / (0.40 + 1094.0 / Math.Pow(mid + 1.0, 5.19));
                double term4 = 2.32 * Math.Log10(mr) - 8.07;
                double calcLogW = term1 + term2 + term3 + term4;

                if (calcLogW < logW18) low = mid;
                else high = mid;
            }
            return (low + high) / 2.0;
        }
    }

    // Structural Adequacy Ratio (SAR)
    public double StructuralAdequacyRatio => SnProvided / Math.Max(1e-4, SnRequired);
    public bool IsAdequate => SnProvided >= SnRequired;
}

public static class PavementDesignService
{
    private static readonly Regex PavementFenceRegex = new(
        @":::(?:pavement-design|pavement|aashto-pavement)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EsalRegex = new(
        @"(?:\besal\b|\bw18\b|\btraffic\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RelRegex = new(
        @"(?:\breliability\b|\br\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:%)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex S0Regex = new(
        @"(?:\bs0\b|\bsd\b|\bstd_dev\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PsiRegex = new(
        @"(?:\bdelta_psi\b|\bpsi\b|\bdpsi\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MrRegex = new(
        @"(?:\bmr\b|\bsubgrade\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[mM][pP][aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex D1Regex = new(
        @"(?:\blayer_d1\b|\bd1\b|\basphalt\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex D2Regex = new(
        @"(?:\blayer_d2\b|\bd2\b|\bbase\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex D3Regex = new(
        @"(?:\blayer_d3\b|\bd3\b|\bsubbase\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PavementDesignModel ParsePavement(string blockText, string defaultTitle = "AASHTO Flexible Pavement Structural Design")
    {
        var model = new PavementDesignModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = PavementFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var em = EsalRegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double esal))
                model.EsalMillions = Math.Clamp(esal, 0.01, 200.0);

            var rm = RelRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rel))
                model.ReliabilityPercent = Math.Clamp(rel, 50.0, 99.9);

            var s0m = S0Regex.Match(header);
            if (s0m.Success && double.TryParse(s0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s0))
                model.StandardDeviationS0 = Math.Clamp(s0, 0.1, 1.0);

            var pm = PsiRegex.Match(header);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double psi))
                model.DeltaPsi = Math.Clamp(psi, 0.5, 3.5);

            var mm = MrRegex.Match(header);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mr))
                model.SubgradeMrMpa = Math.Clamp(mr, 5.0, 500.0);

            var d1m = D1Regex.Match(header);
            if (d1m.Success && double.TryParse(d1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d1))
                model.AsphaltD1Mm = Math.Clamp(d1, 20.0, 500.0);

            var d2m = D2Regex.Match(header);
            if (d2m.Success && double.TryParse(d2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d2))
                model.BaseD2Mm = Math.Clamp(d2, 50.0, 1000.0);

            var d3m = D3Regex.Match(header);
            if (d3m.Success && double.TryParse(d3m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d3))
                model.SubbaseD3Mm = Math.Clamp(d3, 50.0, 1000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var em = EsalRegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double esal))
                model.EsalMillions = Math.Clamp(esal, 0.01, 200.0);

            var rm = RelRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rel))
                model.ReliabilityPercent = Math.Clamp(rel, 50.0, 99.9);

            var s0m = S0Regex.Match(l);
            if (s0m.Success && double.TryParse(s0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s0))
                model.StandardDeviationS0 = Math.Clamp(s0, 0.1, 1.0);

            var pm = PsiRegex.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double psi))
                model.DeltaPsi = Math.Clamp(psi, 0.5, 3.5);

            var mm = MrRegex.Match(l);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double mr))
                model.SubgradeMrMpa = Math.Clamp(mr, 5.0, 500.0);

            var d1m = D1Regex.Match(l);
            if (d1m.Success && double.TryParse(d1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d1))
                model.AsphaltD1Mm = Math.Clamp(d1, 20.0, 500.0);

            var d2m = D2Regex.Match(l);
            if (d2m.Success && double.TryParse(d2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d2))
                model.BaseD2Mm = Math.Clamp(d2, 50.0, 1000.0);

            var d3m = D3Regex.Match(l);
            if (d3m.Success && double.TryParse(d3m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d3))
                model.SubbaseD3Mm = Math.Clamp(d3, 50.0, 1000.0);
        }

        return model;
    }

    public static string RenderPavementSvg(PavementDesignModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-pavement-svg\">");
        sb.AppendLine("""
            <style>
              .pv-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .pv-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .pv-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .pv-asphalt { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .pv-base { fill: #475569; stroke: #64748b; stroke-width: 1; fill-opacity: 0.6; }
              .pv-subbase { fill: #334155; stroke: #475569; stroke-width: 1; fill-opacity: 0.4; }
              .pv-subgrade { fill: #1e293b; stroke: #334155; stroke-width: 1; fill-opacity: 0.2; }
              .pv-wheel { fill: #0284c7; stroke: #38bdf8; stroke-width: 1.5; }
              .pv-cone { fill: none; stroke: #fbbf24; stroke-width: 1.2; stroke-dasharray: 3 3; }
              .pv-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .pv-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .pv-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pv-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pv-title\">🛣️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"pv-meta\">ESAL = {model.EsalMillions:F1}M • R = {model.ReliabilityPercent:F0}% • ΔPSI = {model.DeltaPsi:F1} • Mr = {model.SubgradeMrMpa:F0}MPa • SN_prov = {model.SnProvided:F2}</text>");

        // Pavement Multi-Layer Elevation on Left
        double x = 30;
        double yTop = 75;
        double wLayer = 230;

        double h1 = 30; // Asphalt
        double h2 = 45; // Base
        double h3 = 55; // Subbase
        double h4 = 40; // Subgrade

        // Dual Wheel Footprint on top
        double wheelCx = x + wLayer / 2.0;
        sb.AppendLine($"  <rect x=\"{wheelCx - 22}\" y=\"{yTop - 12}\" width=\"18\" height=\"12\" rx=\"2\" class=\"pv-wheel\" />");
        sb.AppendLine($"  <rect x=\"{wheelCx + 4}\" y=\"{yTop - 12}\" width=\"18\" height=\"12\" rx=\"2\" class=\"pv-wheel\" />");
        sb.AppendLine($"  <text x=\"{wheelCx - 20}\" y=\"{yTop - 16}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\">18-kip Axle</text>");

        // Stress dispersion cone (dashed)
        sb.AppendLine($"  <line x1=\"{wheelCx - 22}\" y1=\"{yTop}\" x2=\"{x + 10}\" y2=\"{yTop + h1 + h2 + h3}\" class=\"pv-cone\" />");
        sb.AppendLine($"  <line x1=\"{wheelCx + 22}\" y1=\"{yTop}\" x2=\"{x + wLayer - 10}\" y2=\"{yTop + h1 + h2 + h3}\" class=\"pv-cone\" />");

        // Layer 1: Asphalt Concrete
        sb.AppendLine($"  <rect x=\"{x}\" y=\"{yTop}\" width=\"{wLayer}\" height=\"{h1}\" class=\"pv-asphalt\" />");
        sb.AppendLine($"  <text x=\"{x + 10}\" y=\"{yTop + h1 / 2.0 + 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f8fafc\">Asphalt Concrete (D1={model.AsphaltD1Mm:F0}mm, a1={model.A1})</text>");

        // Layer 2: Crushed Base
        double yBase = yTop + h1;
        sb.AppendLine($"  <rect x=\"{x}\" y=\"{yBase}\" width=\"{wLayer}\" height=\"{h2}\" class=\"pv-base\" />");
        sb.AppendLine($"  <text x=\"{x + 10}\" y=\"{yBase + h2 / 2.0 + 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f8fafc\">Crushed Stone Base (D2={model.BaseD2Mm:F0}mm, a2={model.A2})</text>");

        // Layer 3: Granular Subbase
        double ySub = yBase + h2;
        sb.AppendLine($"  <rect x=\"{x}\" y=\"{ySub}\" width=\"{wLayer}\" height=\"{h3}\" class=\"pv-subbase\" />");
        sb.AppendLine($"  <text x=\"{x + 10}\" y=\"{ySub + h3 / 2.0 + 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f8fafc\">Granular Subbase (D3={model.SubbaseD3Mm:F0}mm, a3={model.A3})</text>");

        // Layer 4: Prepared Subgrade Soil
        double ySoil = ySub + h3;
        sb.AppendLine($"  <rect x=\"{x}\" y=\"{ySoil}\" width=\"{wLayer}\" height=\"{h4}\" class=\"pv-subgrade\" />");
        sb.AppendLine($"  <text x=\"{x + 10}\" y=\"{ySoil + h4 / 2.0 + 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#94a3b8\">Roadbed Subgrade (Mr={model.SubgradeMrMpa:F0}MPa / {model.SubgradeMrPsi:F0}psi)</text>");

        // Results Card on Right
        double cardX = 280;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"235\" height=\"205\" rx=\"6\" class=\"pv-card-bg\" />");

        string statusColor = model.IsAdequate ? "#10b981" : "#f43f5e";
        string statusText = model.IsAdequate ? "ADEQUATE (PASS)" : "INADEQUATE (FAIL)";

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"pv-lbl\">Design Structural Status:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"pv-val\" font-size=\"13\" fill=\"{statusColor}\">{statusText} (SAR={model.StructuralAdequacyRatio*100:F0}%)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"pv-lbl\">Provided Structural Number (SN_prov):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"pv-val\" font-size=\"14\" fill=\"#38bdf8\">SN_prov = {model.SnProvided:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"pv-lbl\">Required Structural Number (SN_req):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"pv-val\" fill=\"#fbbf24\">SN_req = {model.SnRequired:F2} (AASHTO 1993)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"pv-lbl\">Layer SN Contributions (SN1 / SN2 / SN3):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"pv-val\" font-size=\"11\">{model.A1*model.D1Inches:F2}  +  {model.A2*model.D2Inches*model.M2:F2}  +  {model.A3*model.D3Inches*model.M3:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"pv-lbl\">Total Pavement Depth:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" class=\"pv-val\">{model.AsphaltD1Mm + model.BaseD2Mm + model.SubbaseD3Mm:F0} mm ({(model.AsphaltD1Mm + model.BaseD2Mm + model.SubbaseD3Mm)/25.4:F1} in)</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
