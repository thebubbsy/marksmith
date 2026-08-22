using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Design;

public class ColorHarmonyNode
{
    public string Name { get; set; } = "Primary";
    public double HueDeg { get; set; }
    public double Saturation { get; set; } = 1.0;
    public double Lightness { get; set; } = 0.5;
    public string HexColor => HslToHex(HueDeg, Saturation, Lightness);

    private static string HslToHex(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;

        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }

        int ri = (int)Math.Round((r + m) * 255);
        int gi = (int)Math.Round((g + m) * 255);
        int bi = (int)Math.Round((b + m) * 255);
        return $"#{ri:X2}{gi:X2}{bi:X2}";
    }
}

public class ColorWheelModel
{
    public string Title { get; set; } = "Color Wheel & Harmony Gamut";
    public double BaseHueDeg { get; set; } = 200.0; // 0..360
    public string HarmonyMode { get; set; } = "triadic"; // "complementary", "triadic", "analogous", "tetradic", "split-complementary"
    public List<ColorHarmonyNode> Swatches { get; } = new();
}

public static class ColorWheelGamutService
{
    private static readonly Regex ColorWheelFenceRegex = new(
        @":::(?:color-wheel|gamut|colorwheel)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HueRegex = new(
        @"hue\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HarmonyRegex = new(
        @"harmony\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ColorWheelModel ParseColorWheel(string blockText, string defaultTitle = "Color Wheel & Harmony Gamut")
    {
        var model = new ColorWheelModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            BuildHarmonySwatches(model);
            return model;
        }

        var fence = ColorWheelFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var hm = HueRegex.Match(header);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.BaseHueDeg = h;

            var harm = HarmonyRegex.Match(header);
            if (harm.Success) model.HarmonyMode = harm.Groups[1].Value.ToLowerInvariant();

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var hm = HueRegex.Match(l);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.BaseHueDeg = h;

            var harm = HarmonyRegex.Match(l);
            if (harm.Success) model.HarmonyMode = harm.Groups[1].Value.ToLowerInvariant();
        }

        BuildHarmonySwatches(model);
        return model;
    }

    private static void BuildHarmonySwatches(ColorWheelModel model)
    {
        model.Swatches.Clear();
        double baseH = model.BaseHueDeg;

        switch (model.HarmonyMode)
        {
            case "complementary":
                model.Swatches.Add(new ColorHarmonyNode { Name = "Base", HueDeg = baseH });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Complement", HueDeg = baseH + 180 });
                break;
            case "analogous":
                model.Swatches.Add(new ColorHarmonyNode { Name = "Analogous 1", HueDeg = baseH - 30 });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Base", HueDeg = baseH });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Analogous 2", HueDeg = baseH + 30 });
                break;
            case "tetradic":
            case "square":
                model.Swatches.Add(new ColorHarmonyNode { Name = "Base", HueDeg = baseH });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Accent 1", HueDeg = baseH + 90 });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Accent 2", HueDeg = baseH + 180 });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Accent 3", HueDeg = baseH + 270 });
                break;
            case "split-complementary":
                model.Swatches.Add(new ColorHarmonyNode { Name = "Base", HueDeg = baseH });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Split 1", HueDeg = baseH + 150 });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Split 2", HueDeg = baseH + 210 });
                break;
            case "triadic":
            default:
                model.Swatches.Add(new ColorHarmonyNode { Name = "Base", HueDeg = baseH });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Triad 1", HueDeg = baseH + 120 });
                model.Swatches.Add(new ColorHarmonyNode { Name = "Triad 2", HueDeg = baseH + 240 });
                break;
        }
    }

    public static string RenderColorWheelSvg(ColorWheelModel model)
    {
        double width = 480;
        double height = 300;
        double cx = 150;
        double cy = 160;
        double rOuter = 100;
        double rInner = 65;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-color-wheel-svg\">");
        sb.AppendLine("""
            <style>
              .cw-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .cw-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .cw-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .cw-chord { fill: #38bdf8; fill-opacity: 0.15; stroke: #ffffff; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .cw-node { stroke: #ffffff; stroke-width: 2; }
              .cw-swatch-box { rx: 4; stroke: #334155; stroke-width: 1; }
              .cw-label { font-family: Segoe UI, sans-serif; font-size: 10px; fill: #94a3b8; }
              .cw-hex { font-family: monospace; font-size: 10px; font-weight: 700; fill: #f8fafc; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"cw-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"cw-title\">🎨 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"cw-meta\">Harmony: {model.HarmonyMode.ToUpperInvariant()} • Base Hue: {model.BaseHueDeg:F0}°</text>");

        // Render 24 color wheel arc sectors
        int sectors = 24;
        for (int i = 0; i < sectors; i++)
        {
            double a1 = (i * 360.0 / sectors - 90) * Math.PI / 180.0;
            double a2 = ((i + 1) * 360.0 / sectors - 90) * Math.PI / 180.0;
            double hueMid = (i + 0.5) * (360.0 / sectors);

            double x1Out = cx + rOuter * Math.Cos(a1);
            double y1Out = cy + rOuter * Math.Sin(a1);
            double x2Out = cx + rOuter * Math.Cos(a2);
            double y2Out = cy + rOuter * Math.Sin(a2);

            double x1In = cx + rInner * Math.Cos(a1);
            double y1In = cy + rInner * Math.Sin(a1);
            double x2In = cx + rInner * Math.Cos(a2);
            double y2In = cy + rInner * Math.Sin(a2);

            string path = $"M {x1In:F1} {y1In:F1} L {x1Out:F1} {y1Out:F1} A {rOuter} {rOuter} 0 0 1 {x2Out:F1} {y2Out:F1} L {x2In:F1} {y2In:F1} A {rInner} {rInner} 0 0 0 {x1In:F1} {y1In:F1} Z";
            string color = $"hsl({hueMid:F0}, 90%, 50%)";

            sb.AppendLine($"  <path d=\"{path}\" fill=\"{color}\" stroke=\"#0f172a\" stroke-width=\"1\" />");
        }

        // Render geometric polygon chords connecting harmony nodes
        if (model.Swatches.Count > 1)
        {
            var polyPoints = new StringBuilder();
            double rNode = (rOuter + rInner) / 2.0;

            foreach (var node in model.Swatches)
            {
                double rad = (node.HueDeg - 90) * Math.PI / 180.0;
                double nx = cx + rNode * Math.Cos(rad);
                double ny = cy + rNode * Math.Sin(rad);
                polyPoints.Append($"{nx:F1},{ny:F1} ");
            }

            sb.AppendLine($"  <polygon points=\"{polyPoints.ToString().TrimEnd()}\" class=\"cw-chord\" />");
        }

        // Render Harmony Nodes on the wheel
        double rMid = (rOuter + rInner) / 2.0;
        foreach (var node in model.Swatches)
        {
            double rad = (node.HueDeg - 90) * Math.PI / 180.0;
            double nx = cx + rMid * Math.Cos(rad);
            double ny = cy + rMid * Math.Sin(rad);

            sb.AppendLine($"  <circle cx=\"{nx:F1}\" cy=\"{ny:F1}\" r=\"6\" fill=\"{node.HexColor}\" class=\"cw-node\" />");
        }

        // Swatch Palette Cards on Right
        double listX = 290;
        double listY = 70;
        for (int i = 0; i < model.Swatches.Count; i++)
        {
            var swatch = model.Swatches[i];
            double sy = listY + i * 44;

            sb.AppendLine($"  <rect x=\"{listX}\" y=\"{sy}\" width=\"28\" height=\"28\" fill=\"{swatch.HexColor}\" class=\"cw-swatch-box\" />");
            sb.AppendLine($"  <text x=\"{listX + 36}\" y=\"{sy + 13}\" class=\"cw-hex\">{swatch.HexColor}</text>");
            sb.AppendLine($"  <text x=\"{listX + 36}\" y=\"{sy + 25}\" class=\"cw-label\">{swatch.Name} ({swatch.HueDeg % 360:F0}°)</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
