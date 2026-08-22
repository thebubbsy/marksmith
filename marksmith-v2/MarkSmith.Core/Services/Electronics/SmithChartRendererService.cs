using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public record SmithPoint(string Label, double R, double X);

public class SmithChartModel
{
    public string Title { get; set; } = "Smith Chart";
    public List<SmithPoint> Points { get; } = new();
}

/// <summary>
/// Service for parsing RF complex impedance coordinates and rendering constant resistance/reactance SVG Smith charts.
/// </summary>
public static class SmithChartRendererService
{
    private static readonly Regex SmithFenceRegex = new(
        @":::smith-chart([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex PointRegex = new(
        @"point\s+([A-Za-z0-9_]+)\s*=\s*(-?\d+(?:\.\d+)?)\s*([\+\-]\s*\d+(?:\.\d+)?)\s*j(?:\s*\[label:\s*""([^""]+)""\])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static SmithChartModel ParseSmithChart(string blockText, string defaultTitle = "RF Smith Chart")
    {
        var model = new SmithChartModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = SmithFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var pm = PointRegex.Match(l);
            if (pm.Success)
            {
                string id = pm.Groups[1].Value;
                double r = double.TryParse(pm.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double rv) ? rv : 1.0;
                string xStr = pm.Groups[3].Value.Replace(" ", "");
                double x = double.TryParse(xStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double xv) ? xv : 0.0;
                string label = pm.Groups[4].Success ? pm.Groups[4].Value : id;
                model.Points.Add(new SmithPoint(label, r, x));
            }
        }

        return model;
    }

    public static string RenderSmithChartSvg(SmithChartModel model)
    {
        double width = 360;
        double height = 320;
        double cx = width / 2;
        double cy = height / 2 + 10;
        double chartR = 110;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-smith-svg\">");
        sb.AppendLine("""
            <style>
              .sm-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .sm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sm-outer { fill: none; stroke: #38bdf8; stroke-width: 2; }
              .sm-grid { fill: none; stroke: #334155; stroke-width: 1; }
              .sm-axis { stroke: #64748b; stroke-width: 1.5; }
              .sm-pt { fill: #f43f5e; stroke: #ffffff; stroke-width: 1.5; }
              .sm-label { font-family: Segoe UI, sans-serif; font-size: 10px; font-weight: 700; fill: #f8fafc; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sm-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Outer unity circle (|Gamma| = 1)
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{chartR}\" class=\"sm-outer\" />");

        // Horizontal real axis
        sb.AppendLine($"  <line x1=\"{cx - chartR}\" y1=\"{cy}\" x2=\"{cx + chartR}\" y2=\"{cy}\" class=\"sm-axis\" />");

        // Constant Resistance Circles (r = 0.5, 1.0, 2.0)
        double[] rVals = { 0.5, 1.0, 2.0 };
        foreach (var r in rVals)
        {
            double rCenterGamma = r / (r + 1.0);
            double rRadiusGamma = 1.0 / (r + 1.0);
            double rx = cx + rCenterGamma * chartR;
            double rRad = rRadiusGamma * chartR;
            sb.AppendLine($"  <circle cx=\"{rx}\" cy=\"{cy}\" r=\"{rRad}\" class=\"sm-grid\" />");
        }

        // Complex Gamma transform: Gamma = (Z - 1) / (Z + 1)
        foreach (var pt in model.Points)
        {
            double r = pt.R;
            double x = pt.X;
            double denom = (r + 1) * (r + 1) + x * x;
            double gammaReal = (r * r + x * x - 1) / denom;
            double gammaImag = (2 * x) / denom;

            double px = cx + gammaReal * chartR;
            double py = cy - gammaImag * chartR;

            sb.AppendLine($"  <circle cx=\"{px}\" cy=\"{py}\" r=\"5\" class=\"sm-pt\" />");
            sb.AppendLine($"  <text x=\"{px + 8}\" y=\"{py + 3}\" class=\"sm-label\">{System.Net.WebUtility.HtmlEncode(pt.Label)} ({r}+{x}j)</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
