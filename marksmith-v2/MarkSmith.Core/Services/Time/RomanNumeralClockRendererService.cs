using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Time;

public class RomanClockModel
{
    public string Title { get; set; } = "Roman Clock";
    public int Hours { get; set; } = 10;
    public int Minutes { get; set; } = 10;
}

/// <summary>
/// Service for parsing clock timestamps and rendering vintage Roman numeral clock faces in SVG.
/// </summary>
public static class RomanNumeralClockRendererService
{
    private static readonly Regex ClockFenceRegex = new(
        @":::clock([^\r\n]*)\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex TimeRegex = new(
        @"time\s*=\s*""?(\d{1,2}):(\d{2})""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    private static readonly string[] RomanNumerals =
    {
        "XII", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI"
    };

    public static RomanClockModel ParseClock(string blockText, string defaultTitle = "Classical Clock")
    {
        var model = new RomanClockModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = ClockFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var tmMatch = TimeRegex.Match(text);
        if (tmMatch.Success)
        {
            if (int.TryParse(tmMatch.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int h)) model.Hours = h;
            if (int.TryParse(tmMatch.Groups[2].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int m)) model.Minutes = m;
        }

        return model;
    }

    public static string RenderClockSvg(RomanClockModel model)
    {
        double width = 300;
        double height = 300;
        double cx = width / 2;
        double cy = height / 2 + 10;
        double radius = 105;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-clock-svg\">");
        sb.AppendLine("""
            <style>
              .ck-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .ck-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; text-anchor: middle; }
              .ck-dial { fill: #f8fafc; stroke: #94a3b8; stroke-width: 4; }
              .ck-num { font-family: Times New Roman, serif; font-size: 13px; font-weight: 700; fill: #1e293b; text-anchor: middle; }
              .ck-hour { stroke: #0f172a; stroke-width: 4; stroke-linecap: round; }
              .ck-min { stroke: #2563eb; stroke-width: 2.5; stroke-linecap: round; }
              .ck-center { fill: #dc2626; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ck-bg\" />");
        sb.AppendLine($"  <text x=\"{cx}\" y=\"24\" class=\"ck-title\">{System.Net.WebUtility.HtmlEncode(model.Title)} ({model.Hours:D2}:{model.Minutes:D2})</text>");

        // Dial Face
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{radius}\" class=\"ck-dial\" />");

        // Roman Numerals around dial (0 to 11 -> XII, I, II, ...)
        for (int i = 0; i < 12; i++)
        {
            double angleDeg = i * 30.0 - 90.0;
            double angleRad = angleDeg * (Math.PI / 180.0);
            double nx = cx + (radius - 20) * Math.Cos(angleRad);
            double ny = cy + (radius - 20) * Math.Sin(angleRad) + 4.5;
            sb.AppendLine($"  <text x=\"{nx}\" y=\"{ny}\" class=\"ck-num\">{RomanNumerals[i]}</text>");
        }

        // Hands calculation
        double hourAngle = ((model.Hours % 12) + model.Minutes / 60.0) * 30.0 - 90.0;
        double hourRad = hourAngle * (Math.PI / 180.0);
        double hx = cx + (radius * 0.52) * Math.Cos(hourRad);
        double hy = cy + (radius * 0.52) * Math.Sin(hourRad);

        double minAngle = model.Minutes * 6.0 - 90.0;
        double minRad = minAngle * (Math.PI / 180.0);
        double mx = cx + (radius * 0.75) * Math.Cos(minRad);
        double my = cy + (radius * 0.75) * Math.Sin(minRad);

        // Draw Hands
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{cy}\" x2=\"{hx}\" y2=\"{hy}\" class=\"ck-hour\" />");
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{cy}\" x2=\"{mx}\" y2=\"{my}\" class=\"ck-min\" />");
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"4\" class=\"ck-center\" />");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
