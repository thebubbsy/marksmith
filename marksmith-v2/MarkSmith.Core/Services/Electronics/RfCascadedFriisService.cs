using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class RfCascadeStage
{
    public string Name { get; set; } = "Stage";
    public double GainDb { get; set; } = 10.0;     // Gain (dB)
    public double NoiseFigureDb { get; set; } = 2.0;// NF (dB)
    public double Iip3Dbm { get; set; } = 10.0;     // IIP3 (dBm)

    // Linear conversions
    public double GainLinear => Math.Pow(10.0, GainDb / 10.0);
    public double NoiseFactorLinear => Math.Pow(10.0, NoiseFigureDb / 10.0);
    public double Iip3Milliwatts => Math.Pow(10.0, Iip3Dbm / 10.0);
}

public class RfCascadeModel
{
    public string Title { get; set; } = "RF Receiver Front-End Friis Budget";
    public RfCascadeStage Stage1 { get; } = new() { Name = "LNA", GainDb = 18.0, NoiseFigureDb = 1.5, Iip3Dbm = 5.0 };
    public RfCascadeStage Stage2 { get; } = new() { Name = "BPF", GainDb = -2.0, NoiseFigureDb = 2.0, Iip3Dbm = 50.0 };
    public RfCascadeStage Stage3 { get; } = new() { Name = "Mixer", GainDb = 8.0, NoiseFigureDb = 9.0, Iip3Dbm = 12.0 };

    // Total Gain (dB)
    public double TotalGainDb => Stage1.GainDb + Stage2.GainDb + Stage3.GainDb;

    // Friis Cascaded Noise Factor: F = F1 + (F2 - 1)/G1 + (F3 - 1)/(G1*G2)
    public double TotalNoiseFactor
    {
        get
        {
            double f1 = Stage1.NoiseFactorLinear;
            double f2 = Stage2.NoiseFactorLinear;
            double f3 = Stage3.NoiseFactorLinear;
            double g1 = Math.Max(1e-4, Stage1.GainLinear);
            double g1g2 = Math.Max(1e-4, Stage1.GainLinear * Stage2.GainLinear);
            return f1 + (f2 - 1.0) / g1 + (f3 - 1.0) / g1g2;
        }
    }

    // Total Cascaded Noise Figure (dB)
    public double TotalNoiseFigureDb => 10.0 * Math.Log10(Math.Max(1.0, TotalNoiseFactor));

    // Cascaded IIP3 (mW): 1/IIP3_tot = 1/IIP3_1 + G1/IIP3_2 + (G1*G2)/IIP3_3
    public double TotalIip3Dbm
    {
        get
        {
            double p1 = Math.Max(1e-6, Stage1.Iip3Milliwatts);
            double p2 = Math.Max(1e-6, Stage2.Iip3Milliwatts);
            double p3 = Math.Max(1e-6, Stage3.Iip3Milliwatts);
            double g1 = Stage1.GainLinear;
            double g1g2 = Stage1.GainLinear * Stage2.GainLinear;

            double invTotal = (1.0 / p1) + (g1 / p2) + (g1g2 / p3);
            double pTotMw = 1.0 / Math.Max(1e-9, invTotal);
            return 10.0 * Math.Log10(Math.Max(1e-6, pTotMw));
        }
    }
}

public static class RfCascadedFriisService
{
    private static readonly Regex CascadeFenceRegex = new(
        @":::(?:rf-cascade|friis-budget|rf-budget)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StageRegex = new(
        @"(?:lna|filter|mixer|stage\d)\s*[:=]\s*""?([^""\r\n]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ParseStageValues(string input, RfCascadeStage stage)
    {
        var gm = Regex.Match(input, @"[gG]\s*[:=]\s*([+-]?\d+(?:\.\d+)?)");
        if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
            stage.GainDb = Math.Clamp(g, -50.0, 60.0);

        var nfm = Regex.Match(input, @"[nN][fF]\s*[:=]\s*([+-]?\d+(?:\.\d+)?)");
        if (nfm.Success && double.TryParse(nfm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double nf))
            stage.NoiseFigureDb = Math.Clamp(nf, 0.0, 40.0);

        var iipm = Regex.Match(input, @"[iI][iI][pP]3?\s*[:=]\s*([+-]?\d+(?:\.\d+)?)");
        if (iipm.Success && double.TryParse(iipm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double iip))
            stage.Iip3Dbm = Math.Clamp(iip, -50.0, 100.0);
    }

    public static RfCascadeModel ParseCascade(string blockText, string defaultTitle = "RF Receiver Front-End Friis Budget")
    {
        var model = new RfCascadeModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = CascadeFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            if (l.StartsWith("lna", StringComparison.OrdinalIgnoreCase) || l.StartsWith("stage1", StringComparison.OrdinalIgnoreCase))
                ParseStageValues(l, model.Stage1);
            else if (l.StartsWith("filter", StringComparison.OrdinalIgnoreCase) || l.StartsWith("bpf", StringComparison.OrdinalIgnoreCase) || l.StartsWith("stage2", StringComparison.OrdinalIgnoreCase))
                ParseStageValues(l, model.Stage2);
            else if (l.StartsWith("mixer", StringComparison.OrdinalIgnoreCase) || l.StartsWith("stage3", StringComparison.OrdinalIgnoreCase))
                ParseStageValues(l, model.Stage3);
        }

        return model;
    }

    public static string RenderCascadeSvg(RfCascadeModel model)
    {
        double width = 530;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-cascade-svg\">");
        sb.AppendLine("""
            <style>
              .cs-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .cs-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .cs-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .cs-stage-box { fill: #1e293b; stroke: #0284c7; stroke-width: 1.5; }
              .cs-stage-lna { fill: #1e293b; stroke: #38bdf8; stroke-width: 1.5; }
              .cs-line { stroke: #64748b; stroke-width: 2; marker-end: url(#arrow-cascade); }
              .cs-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .cs-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .cs-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine("""
            <defs>
              <marker id="arrow-cascade" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse">
                <path d="M 0 1 L 10 5 L 0 9 z" fill="#64748b" />
              </marker>
            </defs>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"cs-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"cs-title\">📡 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"cs-meta\">G_tot = {model.TotalGainDb:F1}dB • NF_tot = {model.TotalNoiseFigureDb:F2}dB • IIP3_tot = {model.TotalIip3Dbm:F1}dBm</text>");

        // 3-Stage Diagram on Left
        double startX = 25;
        double cy = 110;

        // Stage 1: LNA (Triangle Amplifier)
        double lnaW = 55;
        double lnaH = 50;
        sb.AppendLine($"  <polygon points=\"{startX},{cy - lnaH/2} {startX + lnaW},{cy} {startX},{cy + lnaH/2}\" class=\"cs-stage-lna\" />");
        sb.AppendLine($"  <text x=\"{startX + 10}\" y=\"{cy + 4}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#38bdf8\">LNA</text>");
        sb.AppendLine($"  <text x=\"{startX - 2}\" y=\"{cy + 40}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">G={model.Stage1.GainDb:F0}dB</text>");
        sb.AppendLine($"  <text x=\"{startX - 2}\" y=\"{cy + 52}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">NF={model.Stage1.NoiseFigureDb:F1}dB</text>");

        // Arrow 1->2
        sb.AppendLine($"  <line x1=\"{startX + lnaW}\" y1=\"{cy}\" x2=\"{startX + lnaW + 20}\" y2=\"{cy}\" class=\"cs-line\" />");

        // Stage 2: BPF (Rectangle)
        double bpfX = startX + lnaW + 20;
        double bpfW = 50;
        double bpfH = 40;
        sb.AppendLine($"  <rect x=\"{bpfX}\" y=\"{cy - bpfH/2}\" width=\"{bpfW}\" height=\"{bpfH}\" rx=\"4\" class=\"cs-stage-box\" />");
        sb.AppendLine($"  <text x=\"{bpfX + 14}\" y=\"{cy + 4}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#38bdf8\">BPF</text>");
        sb.AppendLine($"  <text x=\"{bpfX - 2}\" y=\"{cy + 40}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">G={model.Stage2.GainDb:F0}dB</text>");
        sb.AppendLine($"  <text x=\"{bpfX - 2}\" y=\"{cy + 52}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">NF={model.Stage2.NoiseFigureDb:F1}dB</text>");

        // Arrow 2->3
        sb.AppendLine($"  <line x1=\"{bpfX + bpfW}\" y1=\"{cy}\" x2=\"{bpfX + bpfW + 20}\" y2=\"{cy}\" class=\"cs-line\" />");

        // Stage 3: Mixer (Circle with X)
        double mixX = bpfX + bpfW + 20;
        double mixR = 22;
        double mixCx = mixX + mixR;
        sb.AppendLine($"  <circle cx=\"{mixCx}\" cy=\"{cy}\" r=\"{mixR}\" class=\"cs-stage-box\" />");
        sb.AppendLine($"  <line x1=\"{mixCx - 10}\" y1=\"{cy - 10}\" x2=\"{mixCx + 10}\" y2=\"{cy + 10}\" stroke=\"#fbbf24\" stroke-width=\"1.8\" />");
        sb.AppendLine($"  <line x1=\"{mixCx - 10}\" y1=\"{cy + 10}\" x2=\"{mixCx + 10}\" y2=\"{cy - 10}\" stroke=\"#fbbf24\" stroke-width=\"1.8\" />");
        sb.AppendLine($"  <text x=\"{mixX - 2}\" y=\"{cy + 40}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">G={model.Stage3.GainDb:F0}dB</text>");
        sb.AppendLine($"  <text x=\"{mixX - 2}\" y=\"{cy + 52}\" font-family=\"monospace\" font-size=\"8\" fill=\"#94a3b8\">NF={model.Stage3.NoiseFigureDb:F1}dB</text>");

        // Results Card on Right
        double cardX = 265;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"245\" height=\"205\" rx=\"6\" class=\"cs-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"cs-lbl\">Cascaded Noise Figure (NF_tot):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"cs-val\" font-size=\"14\" fill=\"#10b981\">NF_tot = {model.TotalNoiseFigureDb:F2} dB (F = {model.TotalNoiseFactor:F2})</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"cs-lbl\">Total System Gain (G_tot):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"cs-val\" fill=\"#38bdf8\">G_tot = {model.TotalGainDb:F1} dB</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"cs-lbl\">Cascaded Linearity (IIP3_tot):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"cs-val\" font-size=\"13\" fill=\"#fbbf24\">IIP3_tot = {model.TotalIip3Dbm:F1} dBm</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"cs-lbl\">Friis NF Formula Dominance:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" font-family=\"monospace\" font-size=\"10.5\" fill=\"#94a3b8\">LNA sets {model.Stage1.NoiseFigureDb:F1}dB NF; Mixer is suppressed by G1</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 174}\" class=\"cs-lbl\">IIP3 Bottleneck Stage:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 190}\" font-family=\"monospace\" font-size=\"10.5\" fill=\"#f43f5e\">Mixer IIP3 ({model.Stage3.Iip3Dbm:F0}dBm) referred to input</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
