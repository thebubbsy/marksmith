using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class SmithChartRfModel
{
    public string Title { get; set; } = "RF Smith Chart";
    public double NormalizedR { get; set; } = 1.0;
    public double NormalizedX { get; set; } = 0.5;
    public double CharacteristicZ0 { get; set; } = 50.0;

    public double GammaReal
    {
        get
        {
            double r = NormalizedR;
            double x = NormalizedX;
            double denom = Math.Pow(r + 1.0, 2) + Math.Pow(x, 2);
            return (Math.Pow(r, 2) - 1.0 + Math.Pow(x, 2)) / Math.Max(1e-6, denom);
        }
    }

    public double GammaImag
    {
        get
        {
            double r = NormalizedR;
            double x = NormalizedX;
            double denom = Math.Pow(r + 1.0, 2) + Math.Pow(x, 2);
            return (2.0 * x) / Math.Max(1e-6, denom);
        }
    }

    public double GammaMag => Math.Sqrt(Math.Pow(GammaReal, 2) + Math.Pow(GammaImag, 2));

    public double Vswr => (1.0 + GammaMag) / Math.Max(1e-4, 1.0 - GammaMag);
}

public static class SmithChartRfService
{
    private static readonly Regex SmithFenceRegex = new(
        @":::(?:smith-chart|smithchart|rf-impedance)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ZRegex = new(
        @"z\s*[:=]\s*""?(\d+(?:\.\d+)?)\s*([\+\-]\s*\d+(?:\.\d+)?j|[+-]\s*j\d+(?:\.\d+)?)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Z0Regex = new(
        @"z0\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SmithChartRfModel ParseSmithChart(string blockText, string defaultTitle = "RF Smith Chart")
    {
        var model = new SmithChartRfModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = SmithFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var zm = ZRegex.Match(l);
            if (zm.Success && double.TryParse(zm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double r))
            {
                model.NormalizedR = r;
                string jPart = zm.Groups[2].Value.Replace(" ", "").Replace("j", "");
                if (double.TryParse(jPart, NumberStyles.Any, CultureInfo.InvariantCulture, out double x))
                    model.NormalizedX = x;
            }

            var z0m = Z0Regex.Match(l);
            if (z0m.Success && double.TryParse(z0m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double z0))
                model.CharacteristicZ0 = z0;
        }

        return model;
    }

    public static string RenderSmithChartSvg(SmithChartRfModel model)
    {
        double width = 400;
        double height = 300;
        double cx = 150;
        double cy = 150;
        double rSmith = 100;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-smith-rf-svg\">");
        sb.AppendLine("""
            <style>
              .sm-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .sm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .sm-outer { fill: #0f172a; stroke: #64748b; stroke-width: 1.8; }
              .sm-r-circle { fill: none; stroke: #334155; stroke-width: 1; }
              .sm-point { fill: #f43f5e; stroke: #ffffff; stroke-width: 1.5; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"sm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"sm-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rSmith}\" class=\"sm-outer\" />");

        double[] rVals = { 0.5, 1.0, 2.0 };
        foreach (double r in rVals)
        {
            double cShift = (r / (r + 1.0)) * rSmith;
            double cr = (1.0 / (r + 1.0)) * rSmith;
            sb.AppendLine($"  <circle cx=\"{cx + cShift:F1}\" cy=\"{cy}\" r=\"{cr:F1}\" class=\"sm-r-circle\" />");
        }

        double px = cx + model.GammaReal * rSmith;
        double py = cy - model.GammaImag * rSmith;
        sb.AppendLine($"  <circle cx=\"{px:F1}\" cy=\"{py:F1}\" r=\"4.5\" class=\"sm-point\" />");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
