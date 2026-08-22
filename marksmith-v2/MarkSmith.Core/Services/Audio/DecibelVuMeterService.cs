using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio;

public class VuMeterModel
{
    public string Title { get; set; } = "Stereo Audio VU Meter";
    public double LeftDb { get; set; } = -6.0;  // -40.0 to +6.0 dB
    public double RightDb { get; set; } = -3.0; // -40.0 to +6.0 dB
    public double LeftPeakDb { get; set; } = -1.5;
    public double RightPeakDb { get; set; } = 0.5;
    public int SegmentCount { get; set; } = 24;
}

public static class DecibelVuMeterService
{
    private static readonly Regex VuMeterFenceRegex = new(
        @":::(?:vumeter|vu-meter|audio-meter)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LeftDbRegex = new(
        @"left(?:_db)?\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:dB)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RightDbRegex = new(
        @"right(?:_db)?\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:dB)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static VuMeterModel ParseVuMeter(string blockText, string defaultTitle = "Stereo Audio VU Meter")
    {
        var model = new VuMeterModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = VuMeterFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var lm = LeftDbRegex.Match(header);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ldb))
                model.LeftDb = Math.Clamp(ldb, -40.0, 6.0);

            var rm = RightDbRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rdb))
                model.RightDb = Math.Clamp(rdb, -40.0, 6.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var lm = LeftDbRegex.Match(l);
            if (lm.Success && double.TryParse(lm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ldb))
                model.LeftDb = Math.Clamp(ldb, -40.0, 6.0);

            var rm = RightDbRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rdb))
                model.RightDb = Math.Clamp(rdb, -40.0, 6.0);
        }

        model.LeftPeakDb = Math.Min(6.0, model.LeftDb + 2.0);
        model.RightPeakDb = Math.Min(6.0, model.RightDb + 1.5);
        return model;
    }

    public static string RenderVuMeterSvg(VuMeterModel model)
    {
        double width = 480;
        double height = 220;
        double barX = 70;
        double segW = 10;
        double segH = 22;
        double segGap = 4;
        int totalSegs = model.SegmentCount;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-vumeter-svg\">");
        sb.AppendLine("""
            <style>
              .vu-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .vu-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .vu-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .vu-chan { font-family: monospace; font-size: 11px; font-weight: 700; fill: #94a3b8; }
              .vu-db-text { font-family: monospace; font-size: 10px; font-weight: 700; fill: #f8fafc; text-anchor: end; }
              .vu-scale-label { font-family: monospace; font-size: 8.5px; fill: #64748b; text-anchor: middle; }
              .vu-peak { stroke: #ffffff; stroke-width: 2; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"vu-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"vu-title\">🎛 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"vu-meta\">Calibrated Logarithmic Decibel Volume (0 dB = Reference Line)</text>");

        // Render Scale Labels (-40, -30, -20, -12, -6, 0, +3, +6)
        double[] dbMarkers = { -40, -30, -20, -12, -6, -3, 0, 3, 6 };
        double scaleY = 70;

        foreach (var db in dbMarkers)
        {
            double frac = (db + 40.0) / 46.0;
            double markX = barX + frac * ((segW + segGap) * totalSegs - segGap);
            string lbl = db > 0 ? $"+{db:F0}" : $"{db:F0}";
            sb.AppendLine($"  <text x=\"{markX:F1}\" y=\"{scaleY}\" class=\"vu-scale-label\">{lbl}</text>");
            sb.AppendLine($"  <line x1=\"{markX:F1}\" y1=\"{scaleY + 3}\" x2=\"{markX:F1}\" y2=\"{scaleY + 7}\" stroke=\"#475569\" stroke-width=\"1\" />");
        }

        // Render Left & Right Channels
        RenderChannel(sb, "L", model.LeftDb, model.LeftPeakDb, barX, 90, totalSegs, segW, segH, segGap);
        RenderChannel(sb, "R", model.RightDb, model.RightPeakDb, barX, 130, totalSegs, segW, segH, segGap);

        // Footer
        sb.AppendLine($"  <text x=\"20\" y=\"{height - 16}\" class=\"vu-meta\" fill=\"#64748b\">Green: Normal (&lt; -6 dB) • Amber: Caution (-6 to 0 dB) • Red: Clip / Overload (&gt; 0 dB)</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void RenderChannel(StringBuilder sb, string label, double db, double peakDb, double startX, double y, int segs, double w, double h, double gap)
    {
        sb.AppendLine($"  <text x=\"{startX - 18}\" y=\"{y + h * 0.7}\" class=\"vu-chan\">{label}</text>");

        double normDb = Math.Clamp((db + 40.0) / 46.0, 0.0, 1.0);
        int activeCount = (int)Math.Round(normDb * segs);

        double normPeak = Math.Clamp((peakDb + 40.0) / 46.0, 0.0, 1.0);
        int peakIndex = (int)Math.Round(normPeak * (segs - 1));

        for (int i = 0; i < segs; i++)
        {
            double sx = startX + i * (w + gap);
            double segDb = -40.0 + (i / (double)(segs - 1)) * 46.0;

            string color;
            if (segDb > 0) color = "#ef4444"; // Red
            else if (segDb >= -6) color = "#f59e0b"; // Amber
            else color = "#10b981"; // Green

            bool isActive = i < activeCount;
            double opacity = isActive ? 1.0 : 0.15;

            sb.AppendLine($"  <rect x=\"{sx:F1}\" y=\"{y:F1}\" width=\"{w}\" height=\"{h}\" rx=\"2\" fill=\"{color}\" fill-opacity=\"{opacity:F2}\" />");

            if (i == peakIndex)
            {
                sb.AppendLine($"  <rect x=\"{sx:F1}\" y=\"{y:F1}\" width=\"2.5\" height=\"{h}\" fill=\"#ffffff\" />");
            }
        }

        string valStr = db >= 0 ? $"+{db:F1} dB" : $"{db:F1} dB";
        sb.AppendLine($"  <text x=\"{startX + segs * (w + gap) + 12:F1}\" y=\"{y + h * 0.7}\" class=\"vu-db-text\">{valStr}</text>");
    }
}
