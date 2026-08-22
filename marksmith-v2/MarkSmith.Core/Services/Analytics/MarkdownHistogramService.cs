using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Analytics;

public record HistogramBin(double Start, double End, int Count);

public class HistogramModel
{
    public string Title { get; set; } = "Histogram";
    public List<double> DataPoints { get; } = new();
    public int BinCount { get; set; } = 6;
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public List<HistogramBin> Bins { get; } = new();
}

/// <summary>
/// Service for parsing numeric datasets, calculating statistical distributions, and rendering SVG column histograms.
/// </summary>
public static class MarkdownHistogramService
{
    private static readonly Regex HistogramFenceRegex = new(
        @":::histogram(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex DataRegex = new(
        @"Data:\s*([0-9,\.\s\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BinsRegex = new(
        @"Bins:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a histogram block, computes statistical distribution, and buckets into frequency bins.
    /// </summary>
    public static HistogramModel ParseHistogram(string blockText, string defaultTitle = "Distribution")
    {
        var model = new HistogramModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = HistogramFenceRegex.Match(blockText);
        string text = fence.Success ? fence.Groups[2].Value : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Title = fence.Groups[1].Value.Trim();
        }

        var dataMatch = DataRegex.Match(text);
        if (dataMatch.Success)
        {
            foreach (var tok in dataMatch.Groups[1].Value.Split(','))
            {
                if (double.TryParse(tok.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                {
                    model.DataPoints.Add(val);
                }
            }
        }

        var binsMatch = BinsRegex.Match(text);
        if (binsMatch.Success && int.TryParse(binsMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
        {
            model.BinCount = Math.Max(2, Math.Min(20, b));
        }

        if (model.DataPoints.Count > 0)
        {
            ComputeBins(model);
        }

        return model;
    }

    private static void ComputeBins(HistogramModel model)
    {
        double min = model.DataPoints.Min();
        double max = model.DataPoints.Max();
        if (Math.Abs(max - min) < 0.0001)
        {
            max = min + 1.0;
        }

        double step = (max - min) / model.BinCount;
        model.Mean = model.DataPoints.Average();
        double sumSq = model.DataPoints.Sum(d => Math.Pow(d - model.Mean, 2));
        model.StdDev = Math.Sqrt(sumSq / model.DataPoints.Count);

        for (int i = 0; i < model.BinCount; i++)
        {
            double bStart = min + i * step;
            double bEnd = (i == model.BinCount - 1) ? max + 0.001 : bStart + step;
            int count = model.DataPoints.Count(d => d >= bStart && d < bEnd);
            model.Bins.Add(new HistogramBin(Math.Round(bStart, 1), Math.Round(bEnd, 1), count));
        }
    }

    /// <summary>
    /// Renders an SVG bar histogram with frequency labels.
    /// </summary>
    public static string RenderHistogramSvg(HistogramModel model)
    {
        double width = 450;
        double height = 250;
        double chartBottom = 200;
        double chartTop = 60;
        double chartLeft = 50;
        double chartRight = 410;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-histogram-svg\">");
        sb.AppendLine("""
            <style>
              .h-bg { fill: #0d1117; stroke: #30363d; stroke-width: 1.5; }
              .h-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .h-stats { font-family: monospace; font-size: 10px; fill: #8b949e; }
              .h-bar { fill: #238636; stroke: #2ea043; stroke-width: 1.5; rx: 3; }
              .h-axis { stroke: #30363d; stroke-width: 1; }
              .h-label { font-family: Segoe UI, sans-serif; font-size: 9px; fill: #8b949e; text-anchor: middle; }
              .h-val { font-family: Segoe UI, sans-serif; font-size: 10px; font-weight: 600; fill: #ffffff; text-anchor: middle; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"h-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"h-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"h-stats\">n={model.DataPoints.Count}  μ={model.Mean:F1}  σ={model.StdDev:F1}</text>");

        // Axis line
        sb.AppendLine($"  <line x1=\"{chartLeft}\" y1=\"{chartBottom}\" x2=\"{chartRight}\" y2=\"{chartBottom}\" class=\"h-axis\" />");

        if (model.Bins.Count > 0)
        {
            int maxCount = Math.Max(1, model.Bins.Max(b => b.Count));
            double barWidth = (chartRight - chartLeft) / model.Bins.Count;

            for (int i = 0; i < model.Bins.Count; i++)
            {
                var bin = model.Bins[i];
                double barH = (bin.Count / (double)maxCount) * (chartBottom - chartTop);
                double bx = chartLeft + i * barWidth + 4;
                double by = chartBottom - barH;
                double bw = barWidth - 8;

                if (barH > 0)
                {
                    sb.AppendLine($"  <rect x=\"{bx}\" y=\"{by}\" width=\"{bw}\" height=\"{barH}\" class=\"h-bar\" />");
                    sb.AppendLine($"  <text x=\"{bx + bw / 2}\" y=\"{by - 4}\" class=\"h-val\">{bin.Count}</text>");
                }
                sb.AppendLine($"  <text x=\"{bx + bw / 2}\" y=\"{chartBottom + 16}\" class=\"h-label\">{bin.Start}</text>");
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
