using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public record ResistorBand(string ColorName, string HexCode);

public class ResistorModel
{
    public string Title { get; set; } = "Resistor Color Code";
    public double ResistanceOhms { get; set; } = 4700;
    public double TolerancePercent { get; set; } = 5;
    public List<ResistorBand> Bands { get; } = new();
}

/// <summary>
/// Service for calculating 4-band/5-band EIA resistor color codes and rendering SVG axial resistor schematics.
/// </summary>
public static class ResistorColorCodeService
{
    private static readonly Regex ResistorFenceRegex = new(
        @":::resistor([^\r\n]*)\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex ResistorValRegex = new(
        @"(\d+(?:\.\d+)?)\s*([kKmM]?)\s*(?:[ΩoO]|ohms?)?(?:\s+(\d+(?:\.\d+)?)\s*%)?",
        RegexOptions.Compiled);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    private static readonly (string Name, string Hex)[] DigitColors =
    {
        ("Black", "#1e293b"),
        ("Brown", "#854d0e"),
        ("Red", "#dc2626"),
        ("Orange", "#ea580c"),
        ("Yellow", "#ca8a04"),
        ("Green", "#16a34a"),
        ("Blue", "#2563eb"),
        ("Violet", "#9333ea"),
        ("Gray", "#64748b"),
        ("White", "#f8fafc")
    };

    public static ResistorModel ParseResistor(string blockText, string defaultText = "4.7k 5%")
    {
        var model = new ResistorModel { Title = "Resistor Code" };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            DecodeResistance(model, 4700, 5);
            return model;
        }

        var fence = ResistorFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) text = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) text = header;
        }

        var rm = ResistorValRegex.Match(text);
        if (rm.Success)
        {
            double val = double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pv) ? pv : 4700.0;
            string mult = rm.Groups[2].Value.ToUpperInvariant();
            if (mult == "K") val *= 1000;
            else if (mult == "M") val *= 1_000_000;

            double tol = rm.Groups[3].Success && double.TryParse(rm.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double tv) ? tv : 5.0;
            DecodeResistance(model, val, tol);
        }
        else
        {
            DecodeResistance(model, 4700, 5);
        }

        return model;
    }

    private static void DecodeResistance(ResistorModel model, double ohms, double tol)
    {
        model.ResistanceOhms = ohms;
        model.TolerancePercent = tol;

        // 4-band calculation: 2 digits + multiplier + tolerance
        int multPower = (int)Math.Floor(Math.Log10(Math.Max(1, ohms)));
        double mantissa = ohms / Math.Pow(10, multPower - 1);
        int d1 = (int)(mantissa / 10) % 10;
        int d2 = (int)mantissa % 10;
        int multiplierExp = multPower - 1;

        if (d1 >= 0 && d1 < 10) model.Bands.Add(new ResistorBand(DigitColors[d1].Name, DigitColors[d1].Hex));
        if (d2 >= 0 && d2 < 10) model.Bands.Add(new ResistorBand(DigitColors[d2].Name, DigitColors[d2].Hex));

        // Multiplier band
        if (multiplierExp >= 0 && multiplierExp < 10)
        {
            model.Bands.Add(new ResistorBand(DigitColors[multiplierExp].Name, DigitColors[multiplierExp].Hex));
        }
        else
        {
            model.Bands.Add(new ResistorBand("Gold", "#eab308"));
        }

        // Tolerance band (Gold = 5%, Silver = 10%, Brown = 1%)
        if (tol <= 1.0) model.Bands.Add(new ResistorBand("Brown", "#854d0e"));
        else if (tol <= 5.0) model.Bands.Add(new ResistorBand("Gold", "#eab308"));
        else model.Bands.Add(new ResistorBand("Silver", "#cbd5e1"));
    }

    public static string RenderResistorSvg(ResistorModel model)
    {
        double width = 360;
        double height = 180;
        double cy = 95;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-resistor-svg\">");
        sb.AppendLine("""
            <style>
              .rs-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .rs-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .rs-meta { font-family: monospace; font-size: 11px; fill: #38bdf8; }
              .rs-lead { stroke: #cbd5e1; stroke-width: 4; }
              .rs-body { fill: #fed7aa; stroke: #ea580c; stroke-width: 1.5; rx: 12; }
              .rs-band { stroke: #0f172a; stroke-width: 0.5; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"rs-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"rs-title\">Resistor: {model.ResistanceOhms} Ω ±{model.TolerancePercent}%</text>");

        // Leads
        sb.AppendLine($"  <line x1=\"20\" y1=\"{cy}\" x2=\"340\" y2=\"{cy}\" class=\"rs-lead\" />");

        // Ceramic Body
        sb.AppendLine($"  <rect x=\"80\" y=\"{cy - 25}\" width=\"200\" height=\"50\" class=\"rs-body\" />");

        // Color Bands
        double[] bandX = { 110, 145, 180, 240 };
        for (int i = 0; i < Math.Min(bandX.Length, model.Bands.Count); i++)
        {
            var band = model.Bands[i];
            sb.AppendLine($"  <rect x=\"{bandX[i]}\" y=\"{cy - 25}\" width=\"14\" height=\"50\" fill=\"{band.HexCode}\" class=\"rs-band\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
