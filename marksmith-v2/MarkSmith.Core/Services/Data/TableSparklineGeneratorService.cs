using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Data;

public record SparklineMetrics(double Min, double Max, double Delta, double DeltaPercent, bool IsUpTrend);

/// <summary>
/// Service that parses numerical time-series in Markdown tables and synthesizes compact inline SVG sparklines.
/// </summary>
public static class TableSparklineGeneratorService
{
    private static readonly Regex SparklineTagRegex = new(
        @"\[sparkline:\s*([0-9.,\s\-]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Transforms all [sparkline: ...] placeholders in Markdown tables into inline SVG vector graphics.
    /// </summary>
    public static string TransformSparklines(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        if (!markdown.Contains("[sparkline:", StringComparison.OrdinalIgnoreCase))
            return markdown;

        return SparklineTagRegex.Replace(markdown, match =>
        {
            string rawNumbers = match.Groups[1].Value;
            var points = ParseNumbers(rawNumbers);
            if (points.Count < 2) return match.Value;

            return GenerateSparklineSvg(points);
        });
    }

    /// <summary>
    /// Computes summary metrics for a list of data points.
    /// </summary>
    public static SparklineMetrics CalculateMetrics(List<double> points)
    {
        if (points == null || points.Count == 0)
            return new SparklineMetrics(0, 0, 0, 0, false);

        double min = points.Min();
        double max = points.Max();
        double delta = points[^1] - points[0];
        double deltaPct = points[0] != 0 ? (delta / Math.Abs(points[0])) * 100.0 : 0.0;
        return new SparklineMetrics(min, max, delta, deltaPct, delta >= 0);
    }

    /// <summary>
    /// Generates an inline SVG polyline sparkline string.
    /// </summary>
    public static string GenerateSparklineSvg(List<double> points, double width = 60, double height = 18)
    {
        if (points == null || points.Count < 2)
            return string.Empty;

        double min = points.Min();
        double max = points.Max();
        double range = max - min;
        if (range == 0) range = 1.0;

        double padding = 2.0;
        double w = width - (padding * 2);
        double h = height - (padding * 2);
        double step = w / (points.Count - 1);

        var coordPairs = new List<string>();
        for (int i = 0; i < points.Count; i++)
        {
            double x = padding + (i * step);
            double y = height - (padding + ((points[i] - min) / range) * h);
            coordPairs.Add($"{x:F1},{y:F1}");
        }

        bool isUp = points[^1] >= points[0];
        string strokeColor = isUp ? "#3fb950" : "#f85149";
        string polylineStr = string.Join(" ", coordPairs);

        var lastCoord = coordPairs[^1].Split(',');

        return $"""<svg width="{width}" height="{height}" viewBox="0 0 {width} {height}" class="ms-sparkline" style="vertical-align: middle; display: inline-block;"><polyline fill="none" stroke="{strokeColor}" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" points="{polylineStr}" /><circle cx="{lastCoord[0]}" cy="{lastCoord[1]}" r="2" fill="{strokeColor}" /></svg>""";
    }

    private static List<double> ParseNumbers(string text)
    {
        var list = new List<double>();
        var tokens = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (double.TryParse(t.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                list.Add(val);
            }
        }
        return list;
    }
}
