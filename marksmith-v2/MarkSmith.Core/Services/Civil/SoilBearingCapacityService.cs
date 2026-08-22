using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class SoilBearingCapacityModel
{
    public string Title { get; set; } = "Meyerhof Soil Bearing Capacity & Shallow Footing";
    public double WidthM { get; set; } = 2.0;          // Footing width B (m)
    public double LengthM { get; set; } = 2.0;         // Footing length L (m)
    public double EmbedmentDepthM { get; set; } = 1.5; // Embedment depth Df (m)
    public double UnitWeightGamma { get; set; } = 19.0;// Soil unit weight (kN/m3)
    public double CohesionC { get; set; } = 10.0;      // Cohesion c' (kPa)
    public double FrictionAnglePhiDeg { get; set; } = 30.0; // Friction angle phi (deg)
    public double FactorOfSafety { get; set; } = 3.0;  // FS

    // Friction angle in radians
    public double PhiRad => FrictionAnglePhiDeg * (Math.PI / 180.0);

    // Meyerhof Bearing Capacity Factors
    public double Nq => Math.Exp(Math.PI * Math.Tan(PhiRad)) * Math.Pow(Math.Tan((Math.PI / 4.0) + (PhiRad / 2.0)), 2);
    public double Nc => FrictionAnglePhiDeg > 0 ? (Nq - 1.0) / Math.Tan(PhiRad) : 5.14;
    public double Ngamma => 2.0 * (Nq + 1.0) * Math.Tan(PhiRad);

    // Shape Factors (Meyerhof)
    public double Kp => Math.Pow(Math.Tan((Math.PI / 4.0) + (PhiRad / 2.0)), 2);
    public double Sc => 1.0 + 0.2 * (WidthM / Math.Max(0.1, LengthM)) * Kp;
    public double Sq => 1.0 + 0.1 * (WidthM / Math.Max(0.1, LengthM)) * Kp;
    public double Sgamma => Sq;

    // Depth Factors
    public double Dc => 1.0 + 0.2 * (EmbedmentDepthM / Math.Max(0.1, WidthM)) * Math.Sqrt(Kp);
    public double Dq => 1.0 + 0.1 * (EmbedmentDepthM / Math.Max(0.1, WidthM)) * Math.Sqrt(Kp);
    public double Dgamma => Dq;

    // Surcharge at base q = gamma * Df (kPa)
    public double SurchargeQ => UnitWeightGamma * EmbedmentDepthM;

    // Terms (kPa)
    public double TermCohesion => CohesionC * Nc * Sc * Dc;
    public double TermSurcharge => SurchargeQ * Nq * Sq * Dq;
    public double TermSoilWeight => 0.5 * UnitWeightGamma * WidthM * Ngamma * Sgamma * Dgamma;

    // Ultimate Bearing Capacity q_ult (kPa)
    public double UltimateBearingCapacity => TermCohesion + TermSurcharge + TermSoilWeight;

    // Allowable Bearing Pressure q_all (kPa)
    public double AllowableBearingPressure => UltimateBearingCapacity / Math.Max(1.0, FactorOfSafety);

    // Allowable Total Column Load (kN)
    public double AllowableColumnLoadKn => AllowableBearingPressure * WidthM * LengthM;
}

public static class SoilBearingCapacityService
{
    private static readonly Regex BearingFenceRegex = new(
        @":::(?:bearing-capacity|footing-bearing|soil-bearing)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WidthRegex = new(
        @"(?:\bwidth\b|\bb\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LengthRegex = new(
        @"(?:\blength\b|\bl\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DepthRegex = new(
        @"(?:\bdepth\b|\bdf\b|\bembedment\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GammaRegex = new(
        @"(?:\bgamma\b|\bunit_weight\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kN/m3)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CohesionRegex = new(
        @"(?:\bcohesion\b|\bc\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kPa)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhiRegex = new(
        @"(?:\bphi\b|\bfriction\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg|°)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FsRegex = new(
        @"(?:\bsafety_factor\b|\bfs\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SoilBearingCapacityModel ParseBearing(string blockText, string defaultTitle = "Meyerhof Soil Bearing Capacity & Shallow Footing")
    {
        var model = new SoilBearingCapacityModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BearingFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var wm = WidthRegex.Match(header);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.WidthM = Math.Clamp(w, 0.2, 50.0);

            var lm = LengthRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
                model.LengthM = Math.Clamp(l, 0.2, 50.0);

            var dm = DepthRegex.Match(header);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                model.EmbedmentDepthM = Math.Clamp(d, 0.0, 30.0);

            var gm = GammaRegex.Match(header);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.UnitWeightGamma = Math.Clamp(g, 10.0, 30.0);

            var cm = CohesionRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionC = Math.Clamp(c, 0.0, 300.0);

            var pm = PhiRegex.Match(header);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                model.FrictionAnglePhiDeg = Math.Clamp(p, 0.0, 50.0);

            var fsm = FsRegex.Match(header);
            if (fsm.Success && double.TryParse(fsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fs))
                model.FactorOfSafety = Math.Clamp(fs, 1.0, 10.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var wm = WidthRegex.Match(l);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.WidthM = Math.Clamp(w, 0.2, 50.0);

            var lm = LengthRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lVal))
                model.LengthM = Math.Clamp(lVal, 0.2, 50.0);

            var dm = DepthRegex.Match(l);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                model.EmbedmentDepthM = Math.Clamp(d, 0.0, 30.0);

            var gm = GammaRegex.Match(l);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.UnitWeightGamma = Math.Clamp(g, 10.0, 30.0);

            var cm = CohesionRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionC = Math.Clamp(c, 0.0, 300.0);

            var pm = PhiRegex.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                model.FrictionAnglePhiDeg = Math.Clamp(p, 0.0, 50.0);

            var fsm = FsRegex.Match(l);
            if (fsm.Success && double.TryParse(fsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double fs))
                model.FactorOfSafety = Math.Clamp(fs, 1.0, 10.0);
        }

        return model;
    }

    public static string RenderBearingSvg(SoilBearingCapacityModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-bearing-svg\">");
        sb.AppendLine("""
            <style>
              .bc-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bc-ground { stroke: #64748b; stroke-width: 1.5; }
              .bc-soil { fill: #334155; fill-opacity: 0.4; }
              .bc-footing { fill: #475569; stroke: #64748b; stroke-width: 1.5; }
              .bc-shear-wedge { fill: #0284c7; fill-opacity: 0.25; stroke: #38bdf8; stroke-width: 1.2; stroke-dasharray: 3 3; }
              .bc-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .bc-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .bc-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bc-title\">🏗️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bc-meta\">{model.WidthM:F1}x{model.LengthM:F1}m (Df={model.EmbedmentDepthM:F1}m) • c'={model.CohesionC:F0}kPa • φ'={model.FrictionAnglePhiDeg:F0}° • q_all={model.AllowableBearingPressure:F0}kPa (FS={model.FactorOfSafety:F1})</text>");

        // Footing Cross-Section on Left
        double cx = 135;
        double yGround = 80;
        double footingW = 90;
        double footingH = 25;
        double colW = 24;
        double colH = 45;
        double yBase = yGround + colH + footingH;

        // Ground line
        sb.AppendLine($"  <line x1=\"25\" y1=\"{yGround}\" x2=\"245\" y2=\"{yGround}\" class=\"bc-ground\" />");
        sb.AppendLine($"  <text x=\"30\" y=\"{yGround - 6}\" font-family=\"monospace\" font-size=\"8\" fill=\"#64748b\">Ground Surface (Df={model.EmbedmentDepthM:F1}m)</text>");

        // Soil region below ground
        sb.AppendLine($"  <rect x=\"25\" y=\"{yGround}\" width=\"220\" height=\"145\" class=\"bc-soil\" />");

        // Prandtl Triangular Active Shear Wedge below footing
        double wedgeTipY = yBase + footingW * 0.45;
        sb.AppendLine($"  <polygon points=\"{cx - footingW/2},{yBase} {cx + footingW/2},{yBase} {cx},{wedgeTipY:F1}\" class=\"bc-shear-wedge\" />");
        // Radial shear zone arcs (Meyerhof)
        sb.AppendLine($"  <path d=\"M {cx - footingW/2},{yBase} Q {cx - footingW},{yBase + 20} {cx - footingW*1.1},{yGround + 30}\" class=\"bc-shear-wedge\" />");
        sb.AppendLine($"  <path d=\"M {cx + footingW/2},{yBase} Q {cx + footingW},{yBase + 20} {cx + footingW*1.1},{yGround + 30}\" class=\"bc-shear-wedge\" />");

        // Column Pedestal
        sb.AppendLine($"  <rect x=\"{cx - colW/2}\" y=\"{yGround - 5}\" width=\"{colW}\" height=\"{colH + 5}\" class=\"bc-footing\" />");

        // Footing Slab
        sb.AppendLine($"  <rect x=\"{cx - footingW/2}\" y=\"{yBase - footingH}\" width=\"{footingW}\" height=\"{footingH}\" rx=\"2\" class=\"bc-footing\" />");
        sb.AppendLine($"  <text x=\"{cx - 20}\" y=\"{yBase - 8}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f8fafc\">B={model.WidthM:F1}m</text>");

        // Results Card on Right
        double cardX = 265;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"245\" height=\"205\" rx=\"6\" class=\"bc-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"bc-lbl\">Allowable Bearing Pressure (q_all):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"bc-val\" font-size=\"14\" fill=\"#10b981\">q_all = {model.AllowableBearingPressure:F1} kPa ({model.AllowableBearingPressure/100:F2} MPa)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"bc-lbl\">Ultimate Bearing Capacity (q_ult):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"bc-val\" fill=\"#38bdf8\">q_ult = {model.UltimateBearingCapacity:F1} kPa (FS = {model.FactorOfSafety:F1})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"bc-lbl\">Allowable Column Load Capacity (V_all):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"bc-val\" fill=\"#fbbf24\">V_all = {model.AllowableColumnLoadKn:F0} kN ({model.AllowableColumnLoadKn/9.81:F0} tons)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"bc-lbl\">Meyerhof Factors (Nc / Nq / Nγ):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"bc-val\" font-size=\"11\">Nc={model.Nc:F1}  |  Nq={model.Nq:F1}  |  Nγ={model.Ngamma:F1}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"bc-lbl\">Capacity Contributions (Cohesion + Surcharge + Weight):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" font-family=\"monospace\" font-size=\"10.5\" fill=\"#94a3b8\">{model.TermCohesion:F0} + {model.TermSurcharge:F0} + {model.TermSoilWeight:F0} kPa</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
