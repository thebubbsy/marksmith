using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class StormwaterDetentionModel
{
    public string Title { get; set; } = "Stormwater Detention Basin & Hydrograph Routing";
    public double AreaHectares { get; set; } = 4.5;       // Catchment area A (ha)
    public double RunoffCoeffPre { get; set; } = 0.25;    // Pre-development C_pre
    public double RunoffCoeffPost { get; set; } = 0.75;   // Post-development C_post
    public double TimeConcentrationMin { get; set; } = 20.0;// Time of concentration tc (min)
    public double RainfallIntensityMmHr { get; set; } = 85.0;// Design storm intensity I (mm/hr)
    public double AllowableReleaseLps { get; set; } = 180.0;// Allowable pre-dev outflow Q_allow (L/s)

    // Pre-development Peak Flow Q_pre (m3/s) = (C_pre * I * A) / 360
    public double PeakFlowPreM3S => (RunoffCoeffPre * RainfallIntensityMmHr * AreaHectares) / 360.0;

    // Post-development Peak Flow Q_post (m3/s) = (C_post * I * A) / 360
    public double PeakFlowPostM3S => (RunoffCoeffPost * RainfallIntensityMmHr * AreaHectares) / 360.0;

    // Outflow Release Rate (m3/s)
    public double AllowableReleaseM3S => AllowableReleaseLps / 1000.0;

    // Excess Peak Rate (m3/s)
    public double ExcessPeakFlowM3S => Math.Max(0.0, PeakFlowPostM3S - AllowableReleaseM3S);

    // Required Active Detention Storage Volume V_det (m3) = Delta Q * (tc * 60)
    public double StorageVolumeM3 => ExcessPeakFlowM3S * (TimeConcentrationMin * 60.0);

    // Flow Attenuation Ratio (%)
    public double FlowAttenuationPercent => PeakFlowPostM3S > 0
        ? Math.Clamp((1.0 - (AllowableReleaseM3S / PeakFlowPostM3S)) * 100.0, 0.0, 100.0)
        : 0.0;
}

public static class StormwaterDetentionService
{
    private static readonly Regex BasinFenceRegex = new(
        @":::(?:stormwater-basin|stormwater|detention-basin)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AreaRegex = new(
        @"(?:\barea\b|\ba\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:ha)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CPreRegex = new(
        @"(?:\bc_pre\b|\bpre_c\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CPostRegex = new(
        @"(?:\bc_post\b|\bpost_c\b|\bc\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TcRegex = new(
        @"(?:\btc\b|\btime_concentration\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:min)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IntRegex = new(
        @"(?:\bi_storm\b|\bintensity\b|\bi\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:mm/hr)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QAllowRegex = new(
        @"(?:\bq_allow\b|\brelease\b|\boutflow\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[lL]/s)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StormwaterDetentionModel ParseBasin(string blockText, string defaultTitle = "Stormwater Detention Basin & Hydrograph Routing")
    {
        var model = new StormwaterDetentionModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = BasinFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var am = AreaRegex.Match(header);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.AreaHectares = Math.Clamp(a, 0.01, 1000.0);

            var cpm = CPreRegex.Match(header);
            if (cpm.Success && double.TryParse(cpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cp))
                model.RunoffCoeffPre = Math.Clamp(cp, 0.05, 0.95);

            var cpostm = CPostRegex.Match(header);
            if (cpostm.Success && double.TryParse(cpostm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cpost))
                model.RunoffCoeffPost = Math.Clamp(cpost, 0.05, 0.95);

            var tcm = TcRegex.Match(header);
            if (tcm.Success && double.TryParse(tcm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tc))
                model.TimeConcentrationMin = Math.Clamp(tc, 1.0, 300.0);

            var im = IntRegex.Match(header);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double iVal))
                model.RainfallIntensityMmHr = Math.Clamp(iVal, 1.0, 500.0);

            var qm = QAllowRegex.Match(header);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double qa))
                model.AllowableReleaseLps = Math.Clamp(qa, 1.0, 50000.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var am = AreaRegex.Match(l);
            if (am.Success && double.TryParse(am.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double a))
                model.AreaHectares = Math.Clamp(a, 0.01, 1000.0);

            var cpm = CPreRegex.Match(l);
            if (cpm.Success && double.TryParse(cpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cp))
                model.RunoffCoeffPre = Math.Clamp(cp, 0.05, 0.95);

            var cpostm = CPostRegex.Match(l);
            if (cpostm.Success && double.TryParse(cpostm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double cpost))
                model.RunoffCoeffPost = Math.Clamp(cpost, 0.05, 0.95);

            var tcm = TcRegex.Match(l);
            if (tcm.Success && double.TryParse(tcm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tc))
                model.TimeConcentrationMin = Math.Clamp(tc, 1.0, 300.0);

            var im = IntRegex.Match(l);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double iVal))
                model.RainfallIntensityMmHr = Math.Clamp(iVal, 1.0, 500.0);

            var qm = QAllowRegex.Match(l);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double qa))
                model.AllowableReleaseLps = Math.Clamp(qa, 1.0, 50000.0);
        }

        return model;
    }

    public static string RenderBasinSvg(StormwaterDetentionModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-basin-svg\">");
        sb.AppendLine("""
            <style>
              .sb-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sb-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sb-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .sb-axis { stroke: #64748b; stroke-width: 1.5; }
              .sb-inflow { fill: none; stroke: #f43f5e; stroke-width: 2.5; }
              .sb-outflow { fill: none; stroke: #10b981; stroke-width: 2; stroke-dasharray: 4 2; }
              .sb-storage { fill: #0284c7; fill-opacity: 0.3; stroke: #38bdf8; stroke-width: 1; }
              .sb-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .sb-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .sb-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sb-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sb-title\">🌊 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"sb-meta\">A = {model.AreaHectares:F1}ha • C_post = {model.RunoffCoeffPost:F2} • I = {model.RainfallIntensityMmHr:F0}mm/hr • Q_post = {model.PeakFlowPostM3S*1000:F0}L/s • Q_allow = {model.AllowableReleaseLps:F0}L/s</text>");

        // Hydrograph Axes on Left
        double x1 = 35;
        double x2 = 270;
        double yBase = 220;
        double yTop = 75;

        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yBase}\" x2=\"{x2}\" y2=\"{yBase}\" class=\"sb-axis\" />");
        sb.AppendLine($"  <line x1=\"{x1}\" y1=\"{yTop}\" x2=\"{x1}\" y2=\"{yBase}\" class=\"sb-axis\" />");
        sb.AppendLine($"  <text x=\"{x2 - 35}\" y=\"{yBase + 16}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Time (t)</text>");
        sb.AppendLine($"  <text x=\"{x1 - 10}\" y=\"{yTop - 6}\" font-family=\"monospace\" font-size=\"9\" fill=\"#64748b\">Q (L/s)</text>");

        // Inflow Hydrograph Triangle (Peak at t_c, ends at 2*t_c)
        double tStart = x1 + 10;
        double tPeak = tStart + 90;
        double tEnd = tPeak + 90;

        double maxQ = Math.Max(model.PeakFlowPostM3S, model.AllowableReleaseM3S * 1.5);
        double scaleQ = (yBase - yTop - 20) / Math.Max(0.01, maxQ);

        double yInflowPeak = yBase - model.PeakFlowPostM3S * scaleQ;
        double yOutflowPeak = yBase - model.AllowableReleaseM3S * scaleQ;

        // Active Storage Shaded Polygon
        sb.AppendLine($"  <polygon points=\"{tStart},{yBase} {tPeak},{yInflowPeak:F1} {tEnd},{yBase} {tEnd},{yOutflowPeak:F1} {tStart + 40},{yOutflowPeak:F1}\" class=\"sb-storage\" />");

        // Inflow curve
        sb.AppendLine($"  <polyline points=\"{tStart},{yBase} {tPeak},{yInflowPeak:F1} {tEnd},{yBase}\" class=\"sb-inflow\" />");
        sb.AppendLine($"  <text x=\"{tPeak - 25:F1}\" y=\"{yInflowPeak - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#f43f5e\">Q_in={model.PeakFlowPostM3S*1000:F0}L/s</text>");

        // Outflow curve
        sb.AppendLine($"  <polyline points=\"{tStart},{yBase} {tStart + 40},{yOutflowPeak:F1} {tEnd},{yOutflowPeak:F1} {tEnd + 30},{yBase}\" class=\"sb-outflow\" />");
        sb.AppendLine($"  <text x=\"{tStart + 50:F1}\" y=\"{yOutflowPeak - 6:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#10b981\">Q_out={model.AllowableReleaseLps:F0}L/s</text>");

        // Results Card on Right
        double cardX = 285;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"225\" height=\"205\" rx=\"6\" class=\"sb-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"sb-lbl\">Detention Storage Volume (V_det):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"sb-val\" font-size=\"14\" fill=\"#38bdf8\">V_det = {model.StorageVolumeM3:F0} m³</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"sb-lbl\">Peak Inflow vs Outflow Rate:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"sb-val\" fill=\"#f43f5e\">Q_post = {model.PeakFlowPostM3S*1000:F0} L/s ({model.PeakFlowPostM3S:F2} m³/s)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"sb-lbl\">Allowable Discharge Release Rate:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"sb-val\" fill=\"#10b981\">Q_allow = {model.AllowableReleaseLps:F0} L/s ({model.AllowableReleaseM3S:F2} m³/s)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"sb-lbl\">Peak Flow Attenuation:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"sb-val\" fill=\"#fbbf24\">{model.FlowAttenuationPercent:F1}% Attenuated</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"sb-lbl\">Pre-development Natural Flow:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" class=\"sb-val\" font-size=\"11\">Q_pre = {model.PeakFlowPreM3S*1000:F0} L/s (C={model.RunoffCoeffPre:F2})</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
