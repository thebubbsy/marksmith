using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class PumpCurveModel
{
    public string Title { get; set; } = "Centrifugal Pump Operating Point";
    public double ShutoffHeadMeters { get; set; } = 45.0;   // H0 (m)
    public double PumpDropKp { get; set; } = 0.005;         // kp (m/(L/s)^2)
    public double StaticHeadMeters { get; set; } = 15.0;    // H_stat (m)
    public double SystemLossKsys { get; set; } = 0.008;     // k_sys (m/(L/s)^2)
    public double BepFlowLps { get; set; } = 50.0;          // Best Efficiency Point (L/s)

    // Operating Flow Rate Q_op = sqrt((H0 - H_stat) / (kp + k_sys)) in L/s
    public double OperatingFlowLps => Math.Sqrt(Math.Max(0.0, (ShutoffHeadMeters - StaticHeadMeters) / (PumpDropKp + SystemLossKsys)));

    // Operating Head H_op = H0 - kp * Q_op^2
    public double OperatingHeadMeters => Math.Max(0.0, ShutoffHeadMeters - PumpDropKp * Math.Pow(OperatingFlowLps, 2));

    // Hydraulic Power P_hyd = rho * g * Q * H in kW (Water: rho = 1000 kg/m3)
    public double HydraulicPowerKw => (1000.0 * 9.80665 * (OperatingFlowLps / 1000.0) * OperatingHeadMeters) / 1000.0;

    // Peak Efficiency at BEP (approx 82%)
    public double EfficiencyPercent
    {
        get
        {
            double dev = (OperatingFlowLps - BepFlowLps) / Math.Max(1.0, BepFlowLps);
            return Math.Max(20.0, 82.0 * (1.0 - Math.Pow(dev, 2) * 0.8));
        }
    }
}

public static class PumpCharacteristicService
{
    private static readonly Regex PumpFenceRegex = new(
        @":::(?:pump-curve|centrifugal-pump|pump-system)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex H0Regex = new(
        @"(?:h0|shutoff|shutoff_head)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KpRegex = new(
        @"(?:kp|pump_k|pump_curve)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HstatRegex = new(
        @"(?:h_stat|static|static_head)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex KsysRegex = new(
        @"(?:k_sys|sys_k|system_loss)\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BepRegex = new(
        @"(?:bep|design_flow|q_bep)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[lL]/s)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PumpCurveModel ParsePump(string blockText, string defaultTitle = "Centrifugal Pump Operating Point")
    {
        var model = new PumpCurveModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = PumpFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var hm = H0Regex.Match(header);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h0))
                model.ShutoffHeadMeters = Math.Clamp(h0, 5.0, 500.0);

            var kpm = KpRegex.Match(header);
            if (kpm.Success && double.TryParse(kpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double kp))
                model.PumpDropKp = Math.Clamp(kp, 0.0001, 1.0);

            var hsm = HstatRegex.Match(header);
            if (hsm.Success && double.TryParse(hsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double hs))
                model.StaticHeadMeters = Math.Clamp(hs, 0.0, model.ShutoffHeadMeters * 0.9);

            var ksm = KsysRegex.Match(header);
            if (ksm.Success && double.TryParse(ksm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ksys))
                model.SystemLossKsys = Math.Clamp(ksys, 0.0001, 1.0);

            var bm = BepRegex.Match(header);
            if (bm.Success && double.TryParse(bm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double bep))
                model.BepFlowLps = Math.Clamp(bep, 1.0, 500.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var hm = H0Regex.Match(l);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h0))
                model.ShutoffHeadMeters = Math.Clamp(h0, 5.0, 500.0);

            var kpm = KpRegex.Match(l);
            if (kpm.Success && double.TryParse(kpm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double kp))
                model.PumpDropKp = Math.Clamp(kp, 0.0001, 1.0);

            var hsm = HstatRegex.Match(l);
            if (hsm.Success && double.TryParse(hsm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double hs))
                model.StaticHeadMeters = Math.Clamp(hs, 0.0, model.ShutoffHeadMeters * 0.9);

            var ksm = KsysRegex.Match(l);
            if (ksm.Success && double.TryParse(ksm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ksys))
                model.SystemLossKsys = Math.Clamp(ksys, 0.0001, 1.0);

            var bm = BepRegex.Match(l);
            if (bm.Success && double.TryParse(bm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double bep))
                model.BepFlowLps = Math.Clamp(bep, 1.0, 500.0);
        }

        return model;
    }

    public static string RenderPumpSvg(PumpCurveModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 230;
        double axisW = 235;
        double axisH = 150;

        double maxQ = Math.Max(80.0, model.OperatingFlowLps * 1.5);
        double maxH = Math.Max(50.0, model.ShutoffHeadMeters * 1.15);

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-pump-svg\">");
        sb.AppendLine("""
            <style>
              .pc-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .pc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .pc-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .pc-axis { stroke: #475569; stroke-width: 1.2; }
              .pc-pump-curve { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .pc-sys-curve { fill: none; stroke: #f43f5e; stroke-width: 2; }
              .pc-duty-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .pc-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .pc-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .pc-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pc-title\">💧 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"pc-meta\">H0 = {model.ShutoffHeadMeters:F0}m • Hstat = {model.StaticHeadMeters:F0}m • Q_op = {model.OperatingFlowLps:F1} L/s</text>");

        // Coordinate Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"pc-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"pc-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">Q (L/s)</text>");
        sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{oy - axisH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\" text-anchor=\"end\">Head (m)</text>");

        // Draw Pump Head Curve H_pump(Q) = H0 - kp * Q^2
        var pumpPath = new StringBuilder();
        // Draw System Head Curve H_sys(Q) = H_stat + k_sys * Q^2
        var sysPath = new StringBuilder();

        int steps = 50;
        for (int i = 0; i <= steps; i++)
        {
            double q = (i / (double)steps) * maxQ;
            double hPump = Math.Max(0.0, model.ShutoffHeadMeters - model.PumpDropKp * Math.Pow(q, 2));
            double hSys = model.StaticHeadMeters + model.SystemLossKsys * Math.Pow(q, 2);

            double px = ox + (q / maxQ) * axisW;
            double pyPump = oy - (hPump / maxH) * axisH;
            double pySys = oy - (Math.Min(maxH, hSys) / maxH) * axisH;

            if (i == 0)
            {
                pumpPath.Append($"M {px:F1} {pyPump:F1}");
                sysPath.Append($"M {px:F1} {pySys:F1}");
            }
            else
            {
                pumpPath.Append($" L {px:F1} {pyPump:F1}");
                sysPath.Append($" L {px:F1} {pySys:F1}");
            }
        }

        sb.AppendLine($"  <path d=\"{pumpPath}\" class=\"pc-pump-curve\" />");
        sb.AppendLine($"  <path d=\"{sysPath}\" class=\"pc-sys-curve\" />");

        // Duty Operating Point Marker
        double qOp = model.OperatingFlowLps;
        double hOp = model.OperatingHeadMeters;
        double pOpX = ox + (qOp / maxQ) * axisW;
        double pOpY = oy - (hOp / maxH) * axisH;

        sb.AppendLine($"  <circle cx=\"{pOpX:F1}\" cy=\"{pOpY:F1}\" r=\"4.5\" class=\"pc-duty-pt\" />");
        sb.AppendLine($"  <text x=\"{pOpX + 6:F1}\" y=\"{pOpY - 4:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">Duty Point</text>");

        // Curve Labels
        sb.AppendLine($"  <text x=\"{ox + 20}\" y=\"{oy - (model.ShutoffHeadMeters / maxH) * axisH + 14:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#38bdf8\">Pump H-Q</text>");
        sb.AppendLine($"  <text x=\"{ox + axisW - 30:F1}\" y=\"{oy - 0.7 * axisH:F1}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#f43f5e\">System</text>");

        // Results Card on Right
        double cardX = 305;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"175\" height=\"195\" rx=\"6\" class=\"pc-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"pc-lbl\">Operating Flow (Qop):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"pc-val\">{model.OperatingFlowLps:F1} L/s</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"pc-lbl\">Operating Head (Hop):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"pc-val\" fill=\"#10b981\">{model.OperatingHeadMeters:F1} m</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"pc-lbl\">Hydraulic Power (Phyd):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"pc-val\">{model.HydraulicPowerKw:F2} kW</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"pc-lbl\">Pump Efficiency (η):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"pc-val\" fill=\"#fbbf24\">η = {model.EfficiencyPercent:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Fluid System Equilibrium</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
