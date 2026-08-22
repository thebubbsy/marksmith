using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class PrestressedBeamModel
{
    public string Title { get; set; } = "Prestressed Concrete Beam";
    public double SpanM { get; set; } = 18.0;          // Span length L (m)
    public double DepthM { get; set; } = 1.2;          // Section depth h (m)
    public double WidthM { get; set; } = 0.5;          // Section width b (m)
    public double PrestressForceKn { get; set; } = 2500.0; // Jacking force P (kN)
    public double TendonEccentricityM { get; set; } = 0.35; // Midspan eccentricity e (m)
    public double AppliedLoadKnPerM { get; set; } = 35.0;   // Uniform load w (kN/m)

    // Section Properties
    public double AreaM2 => WidthM * DepthM;
    public double SectionModulusM3 => (WidthM * Math.Pow(DepthM, 2)) / 6.0;

    // Midspan Bending Moment (kNm)
    public double MidspanMomentKnM => (AppliedLoadKnPerM * Math.Pow(SpanM, 2)) / 8.0;

    // Stresses in MPa (N/mm2 = MN/m2 = kN/m2 / 1000)
    // Direct Prestress Compression: -P / A
    public double DirectPrestressMpa => -(PrestressForceKn / AreaM2) / 1000.0;

    // Prestress Eccentricity Bending: + (P * e) / Z at top, - (P * e) / Z at bottom
    public double EccentricityStressMpa => ((PrestressForceKn * TendonEccentricityM) / SectionModulusM3) / 1000.0;

    // External Load Bending: - M / Z at top (compression), + M / Z at bottom (tension)
    public double LoadBendingStressMpa => (MidspanMomentKnM / SectionModulusM3) / 1000.0;

    // Net Midspan Fiber Stresses (MPa)
    public double MidspanTopStressMpa => DirectPrestressMpa + EccentricityStressMpa - LoadBendingStressMpa;
    public double MidspanBottomStressMpa => DirectPrestressMpa - EccentricityStressMpa + LoadBendingStressMpa;
}

public static class PrestressedBeamService
{
    private static readonly Regex BeamFenceRegex = new(
        @":::(?:prestressed-beam|prestressed|post-tensioned)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpanRegex = new(
        @"(?:\bspan\b|\blength\b|\bl\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DepthRegex = new(
        @"(?:\bdepth\b|\bh\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WidthRegex = new(
        @"(?:\bwidth\b|\bb\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ForceRegex = new(
        @"(?:\bp_jack\b|\bp\b|\bforce\b|\bprestress\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kN)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EccRegex = new(
        @"(?:\be_mid\b|\beccentricity\b|\be\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LoadRegex = new(
        @"(?:\bload\b|\bw\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kN/m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PrestressedBeamModel ParsePrestressed(string blockText, string defaultTitle = "Prestressed Concrete Beam")
    {
        var model = new PrestressedBeamModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BeamFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var sm = SpanRegex.Match(header);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                model.SpanM = Math.Clamp(s, 2.0, 100.0);

            var dm = DepthRegex.Match(header);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                model.DepthM = Math.Clamp(d, 0.2, 5.0);

            var wm = WidthRegex.Match(header);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.WidthM = Math.Clamp(w, 0.1, 3.0);

            var fm = ForceRegex.Match(header);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double f))
                model.PrestressForceKn = Math.Clamp(f, 10.0, 50000.0);

            var em = EccRegex.Match(header);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.TendonEccentricityM = Math.Clamp(e, 0.0, model.DepthM / 2.0);

            var lm = LoadRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double l))
                model.AppliedLoadKnPerM = Math.Clamp(l, 0.0, 1000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var sm = SpanRegex.Match(l);
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                model.SpanM = Math.Clamp(s, 2.0, 100.0);

            var dm = DepthRegex.Match(l);
            if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                model.DepthM = Math.Clamp(d, 0.2, 5.0);

            var wm = WidthRegex.Match(l);
            if (wm.Success && double.TryParse(wm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double w))
                model.WidthM = Math.Clamp(w, 0.1, 3.0);

            var fm = ForceRegex.Match(l);
            if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double f))
                model.PrestressForceKn = Math.Clamp(f, 10.0, 50000.0);

            var em = EccRegex.Match(l);
            if (em.Success && double.TryParse(em.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double e))
                model.TendonEccentricityM = Math.Clamp(e, 0.0, model.DepthM / 2.0);

            var lm = LoadRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ld))
                model.AppliedLoadKnPerM = Math.Clamp(ld, 0.0, 1000.0);
        }

        return model;
    }

    public static string RenderPrestressedSvg(PrestressedBeamModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-beam-svg\">");
        sb.AppendLine("""
            <style>
              .bm-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .bm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .bm-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .bm-concrete { fill: #334155; stroke: #64748b; stroke-width: 1.5; fill-opacity: 0.7; }
              .bm-tendon { fill: none; stroke: #f59e0b; stroke-width: 2.5; stroke-dasharray: 4 2; }
              .bm-support { fill: #64748b; }
              .bm-stress-poly { fill: #0284c7; stroke: #38bdf8; stroke-width: 1.5; fill-opacity: 0.3; }
              .bm-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .bm-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .bm-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bm-title\">🏗️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"bm-meta\">L = {model.SpanM:F1}m • {model.WidthM*1000:F0}x{model.DepthM*1000:F0}mm • P = {model.PrestressForceKn:F0}kN • e = {model.TendonEccentricityM*1000:F0}mm • w = {model.AppliedLoadKnPerM:F0}kN/m</text>");

        // Longitudinal Beam Elevation on Left
        double beamX = 30;
        double beamY = 80;
        double beamW = 230;
        double beamH = 45;

        // Beam concrete rectangle
        sb.AppendLine($"  <rect x=\"{beamX}\" y=\"{beamY}\" width=\"{beamW}\" height=\"{beamH}\" rx=\"3\" class=\"bm-concrete\" />");

        // Pin & Roller supports
        sb.AppendLine($"  <polygon points=\"{beamX},{beamY + beamH} {beamX - 6},{beamY + beamH + 12} {beamX + 6},{beamY + beamH + 12}\" class=\"bm-support\" />");
        sb.AppendLine($"  <polygon points=\"{beamX + beamW},{beamY + beamH} {beamX + beamW - 6},{beamY + beamH + 12} {beamX + beamW + 6},{beamY + beamH + 12}\" class=\"bm-support\" />");

        // Parabolic Tendon Drape
        double cY = beamY + beamH / 2.0; // Centroidal axis
        double drapePx = (model.TendonEccentricityM / (model.DepthM / 2.0)) * (beamH / 2.0 - 4.0);
        double midTendonY = cY + Math.Clamp(drapePx, 0.0, beamH / 2.0 - 4.0);

        var tendonPath = new StringBuilder();
        tendonPath.Append($"M {beamX},{cY:F1} Q {beamX + beamW / 2.0:F1},{midTendonY:F1} {beamX + beamW},{cY:F1}");
        sb.AppendLine($"  <path d=\"{tendonPath}\" class=\"bm-tendon\" />");
        sb.AppendLine($"  <text x=\"{beamX + beamW / 2.0 - 30:F1}\" y=\"{beamY + beamH + 28}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f59e0b\">Tendon Profile e(x)</text>");

        // Midspan Stress Diagram below beam
        double stressX = 90;
        double stressY = 175;
        double stressH = 60;
        double maxStressScale = 25.0; // px per 10 MPa

        double topOffset = Math.Clamp(model.MidspanTopStressMpa * (maxStressScale / 10.0), -35.0, 35.0);
        double botOffset = Math.Clamp(model.MidspanBottomStressMpa * (maxStressScale / 10.0), -35.0, 35.0);

        // Neutral axis line
        sb.AppendLine($"  <line x1=\"{stressX}\" y1=\"{stressY}\" x2=\"{stressX}\" y2=\"{stressY + stressH}\" stroke=\"#64748b\" stroke-width=\"1.5\" />");

        // Stress Trapezoid (compression negative is to left, tension positive is to right)
        sb.AppendLine($"  <polygon points=\"{stressX},{stressY} {stressX + topOffset:F1},{stressY} {stressX + botOffset:F1},{stressY + stressH} {stressX},{stressY + stressH}\" class=\"bm-stress-poly\" />");
        sb.AppendLine($"  <text x=\"{stressX + topOffset - 25:F1}\" y=\"{stressY - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\">{model.MidspanTopStressMpa:F1}MPa</text>");
        sb.AppendLine($"  <text x=\"{stressX + botOffset - 25:F1}\" y=\"{stressY + stressH + 12}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\">{model.MidspanBottomStressMpa:F1}MPa</text>");
        sb.AppendLine($"  <text x=\"{stressX - 45}\" y=\"{stressY + stressH / 2.0 + 4}\" font-family=\"Segoe UI, sans-serif\" font-size=\"8.5\" fill=\"#94a3b8\">Midspan σ</text>");

        // Results Card on Right
        double cardX = 285;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"225\" height=\"205\" rx=\"6\" class=\"bm-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"bm-lbl\">Midspan Applied Moment (M):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"bm-val\" font-size=\"14\" fill=\"#fbbf24\">M_mid = {model.MidspanMomentKnM:F1} kNm</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"bm-lbl\">Direct Prestress Compression (-P/A):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"bm-val\" fill=\"#94a3b8\">σ_dir = {model.DirectPrestressMpa:F2} MPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"bm-lbl\">Midspan Top Fiber Stress (σ_top):</text>");
        string topColor = model.MidspanTopStressMpa < 0 ? "#38bdf8" : "#f43f5e";
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"bm-val\" fill=\"{topColor}\">{model.MidspanTopStressMpa:F2} MPa ({(model.MidspanTopStressMpa < 0 ? "Compression" : "Tension")})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"bm-lbl\">Midspan Bottom Fiber Stress (σ_bot):</text>");
        string botColor = model.MidspanBottomStressMpa < 0 ? "#38bdf8" : "#f43f5e";
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"bm-val\" fill=\"{botColor}\">{model.MidspanBottomStressMpa:F2} MPa ({(model.MidspanBottomStressMpa < 0 ? "Compression" : "Tension")})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"bm-lbl\">Section Modulus (Z):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" class=\"bm-val\" fill=\"#10b981\">Z = {model.SectionModulusM3:F4} m³</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
