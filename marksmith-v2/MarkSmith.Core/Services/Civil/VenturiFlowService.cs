using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class VenturiFlowModel
{
    public string Title { get; set; } = "Bernoulli Venturi Flowmeter & Manometer";
    public double InletDiameterMm { get; set; } = 100.0;  // D1 (mm)
    public double ThroatDiameterMm { get; set; } = 50.0;  // D2 (mm)
    public double ManometerDhMm { get; set; } = 180.0;    // dh (mm)
    public double FluidDensityKgM3 { get; set; } = 1000.0;// Water = 1000 kg/m3
    public double ManometerDensityKgM3 { get; set; } = 13600.0; // Mercury = 13600 kg/m3
    public double Cd { get; set; } = 0.98;                // Discharge coefficient
    public const double Gravity = 9.80665;

    public double InletAreaM2 => Math.PI * Math.Pow((InletDiameterMm / 1000.0) / 2.0, 2);
    public double ThroatAreaM2 => Math.PI * Math.Pow((ThroatDiameterMm / 1000.0) / 2.0, 2);

    // Differential Pressure Delta P = (rho_m - rho_f) * g * dh
    public double DeltaPressurePa => (ManometerDensityKgM3 - FluidDensityKgM3) * Gravity * (ManometerDhMm / 1000.0);

    // Theoretical Velocity v2 at throat
    public double ThroatVelocityMps
    {
        get
        {
            double a1 = InletAreaM2;
            double a2 = ThroatAreaM2;
            double num = 2.0 * DeltaPressurePa / FluidDensityKgM3;
            double den = 1.0 - Math.Pow(a2 / a1, 2);
            return den > 0 ? Math.Sqrt(num / den) : 0.0;
        }
    }

    // Volumetric flow rate Q = Cd * A2 * v2
    public double DischargeQ => Cd * ThroatAreaM2 * ThroatVelocityMps;
    public double DischargeLps => DischargeQ * 1000.0;
}

public static class VenturiFlowService
{
    private static readonly Regex VenturiFenceRegex = new(
        @":::(?:venturi|venturi-flow|bernoulli-tube)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex D1Regex = new(
        @"(?:d1|inlet)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex D2Regex = new(
        @"(?:d2|throat)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DhRegex = new(
        @"(?:dh|height|manometer)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static VenturiFlowModel ParseVenturi(string blockText, string defaultTitle = "Bernoulli Venturi Flowmeter & Manometer")
    {
        var model = new VenturiFlowModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = VenturiFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var d1m = D1Regex.Match(header);
            if (d1m.Success && double.TryParse(d1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d1))
                model.InletDiameterMm = Math.Clamp(d1, 20.0, 1000.0);

            var d2m = D2Regex.Match(header);
            if (d2m.Success && double.TryParse(d2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d2))
                model.ThroatDiameterMm = Math.Clamp(d2, 10.0, model.InletDiameterMm * 0.9);

            var dhm = DhRegex.Match(header);
            if (dhm.Success && double.TryParse(dhm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dh))
                model.ManometerDhMm = Math.Clamp(dh, 5.0, 2000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var d1m = D1Regex.Match(l);
            if (d1m.Success && double.TryParse(d1m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d1))
                model.InletDiameterMm = Math.Clamp(d1, 20.0, 1000.0);

            var d2m = D2Regex.Match(l);
            if (d2m.Success && double.TryParse(d2m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d2))
                model.ThroatDiameterMm = Math.Clamp(d2, 10.0, model.InletDiameterMm * 0.9);

            var dhm = DhRegex.Match(l);
            if (dhm.Success && double.TryParse(dhm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dh))
                model.ManometerDhMm = Math.Clamp(dh, 5.0, 2000.0);
        }

        return model;
    }

    public static string RenderVenturiSvg(VenturiFlowModel model)
    {
        double width = 500;
        double height = 280;
        double cy = 90;

        // Pipe geometry
        double x0 = 40, x1 = 100, x2 = 160, x3 = 210, x4 = 290;
        double r1 = 38, r2 = 18;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-venturi-svg\">");
        sb.AppendLine("""
            <style>
              .vn-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .vn-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .vn-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .vn-pipe { fill: #1e293b; fill-opacity: 0.4; stroke: #64748b; stroke-width: 2; }
              .vn-water { fill: #0284c7; fill-opacity: 0.3; }
              .vn-mano-tube { fill: none; stroke: #94a3b8; stroke-width: 4; }
              .vn-mano-fluid { fill: none; stroke: #f43f5e; stroke-width: 4; }
              .vn-streamline { stroke: #38bdf8; stroke-width: 1.2; stroke-dasharray: 4 3; }
              .vn-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .vn-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .vn-lbl { font-family: Segoe UI, sans-serif; font-size: 10px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"vn-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"vn-title\">🚰 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"vn-meta\">D1 = {model.InletDiameterMm:F0}mm • D2 (Throat) = {model.ThroatDiameterMm:F0}mm • Δh = {model.ManometerDhMm:F0}mm</text>");

        // Symmetrical Venturi Pipe Contour
        string topPath = $"M {x0} {cy - r1} L {x1} {cy - r1} L {x2} {cy - r2} L {x3} {cy - r2} L {x4} {cy - r1}";
        string botPath = $"M {x0} {cy + r1} L {x1} {cy + r1} L {x2} {cy + r2} L {x3} {cy + r2} L {x4} {cy + r1}";
        string pipeShape = $"{topPath} L {x4} {cy + r1} L {x3} {cy + r2} L {x2} {cy + r2} L {x1} {cy + r1} L {x0} {cy + r1} Z";

        sb.AppendLine($"  <path d=\"{pipeShape}\" class=\"vn-water\" />");
        sb.AppendLine($"  <path d=\"{topPath}\" class=\"vn-pipe\" fill=\"none\" />");
        sb.AppendLine($"  <path d=\"{botPath}\" class=\"vn-pipe\" fill=\"none\" />");

        // Fluid Streamlines through converging-diverging nozzle
        sb.AppendLine($"  <line x1=\"{x0}\" y1=\"{cy}\" x2=\"{x4}\" y2=\"{cy}\" class=\"vn-streamline\" />");
        sb.AppendLine($"  <line x1=\"{x0}\" y1=\"{cy - 16}\" x2=\"{x1}\" y2=\"{cy - 16}\" class=\"vn-streamline\" />");
        sb.AppendLine($"  <line x1=\"{x2}\" y1=\"{cy - 8}\" x2=\"{x3}\" y2=\"{cy - 8}\" class=\"vn-streamline\" />");

        // Differential U-Tube Manometer below pipe
        double p1TapX = (x0 + x1) / 2;
        double p2TapX = (x2 + x3) / 2;
        double manoBotY = 220;
        double manoH1 = 170;
        double manoH2 = 170 - (model.ManometerDhMm / 10.0);

        string manoGlass = $"M {p1TapX} {cy + r1} L {p1TapX} {manoBotY} L {p2TapX} {manoBotY} L {p2TapX} {cy + r2}";
        sb.AppendLine($"  <path d=\"{manoGlass}\" class=\"vn-mano-tube\" />");

        // Manometer Red Heavy Fluid (Hg column delta h)
        string manoFluid = $"M {p1TapX} {manoH1} L {p1TapX} {manoBotY} L {p2TapX} {manoBotY} L {p2TapX} {manoH2}";
        sb.AppendLine($"  <path d=\"{manoFluid}\" class=\"vn-mano-fluid\" />");

        // Delta h annotation
        sb.AppendLine($"  <line x1=\"{p2TapX + 8}\" y1=\"{manoH2}\" x2=\"{p2TapX + 8}\" y2=\"{manoH1}\" stroke=\"#fbbf24\" stroke-width=\"1.2\" stroke-dasharray=\"2 2\" />");
        sb.AppendLine($"  <text x=\"{p2TapX + 12}\" y=\"{(manoH1 + manoH2) / 2 + 3}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">Δh</text>");

        // Results Card on Right
        double cardX = 310;
        double cardY = 65;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"185\" rx=\"6\" class=\"vn-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 22}\" class=\"vn-lbl\">Diff Pressure (ΔP):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 38}\" class=\"vn-val\">{model.DeltaPressurePa / 1000.0:F2} kPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 62}\" class=\"vn-lbl\">Throat Velocity (v2):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 78}\" class=\"vn-val\" fill=\"#10b981\">{model.ThroatVelocityMps:F2} m/s</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 102}\" class=\"vn-lbl\">Discharge Rate (Q):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 118}\" class=\"vn-val\" fill=\"#38bdf8\">{model.DischargeLps:F2} L/s</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" font-family=\"monospace\" font-size=\"10\" fill=\"#94a3b8\">({model.DischargeQ:F4} m³/s)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 160}\" class=\"vn-lbl\">Coefficient (Cd):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" class=\"vn-val\">{model.Cd:F2}</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
