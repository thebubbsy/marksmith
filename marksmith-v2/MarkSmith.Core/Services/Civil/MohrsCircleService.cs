using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class MohrsCircleModel
{
    public string Title { get; set; } = "Mohr's Stress Circle (2D Planar Tensor)";
    public double SigmaX { get; set; } = 80.0;   // Normal stress on X face (MPa)
    public double SigmaY { get; set; } = 20.0;   // Normal stress on Y face (MPa)
    public double TauXY { get; set; } = 30.0;    // Shear stress on XY face (MPa)

    public double CenterSigma => (SigmaX + SigmaY) / 2.0;
    public double RadiusR => Math.Sqrt(Math.Pow((SigmaX - SigmaY) / 2.0, 2) + Math.Pow(TauXY, 2));

    public double PrincipalSigma1 => CenterSigma + RadiusR;
    public double PrincipalSigma2 => CenterSigma - RadiusR;
    public double MaxShearTau => RadiusR;
    public double PrincipalAngleDeg => 0.5 * Math.Atan2(2.0 * TauXY, SigmaX - SigmaY) * (180.0 / Math.PI);
}

public static class MohrsCircleService
{
    private static readonly Regex MohrFenceRegex = new(
        @":::(?:mohrs-circle|mohr-circle|stress-tensor)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SxRegex = new(
        @"(?:sx|sigma_x)\s*[:=]\s*""?(-?\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SyRegex = new(
        @"(?:sy|sigma_y)\s*[:=]\s*""?(-?\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TxyRegex = new(
        @"(?:txy|tau_xy)\s*[:=]\s*""?(-?\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MohrsCircleModel ParseMohrsCircle(string blockText, string defaultTitle = "Mohr's Stress Circle (2D Planar Tensor)")
    {
        var model = new MohrsCircleModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = MohrFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var sxm = SxRegex.Match(header);
            if (sxm.Success && double.TryParse(sxm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sx))
                model.SigmaX = sx;

            var sym = SyRegex.Match(header);
            if (sym.Success && double.TryParse(sym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sy))
                model.SigmaY = sy;

            var txm = TxyRegex.Match(header);
            if (txm.Success && double.TryParse(txm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double txy))
                model.TauXY = txy;

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var sxm = SxRegex.Match(l);
            if (sxm.Success && double.TryParse(sxm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sx))
                model.SigmaX = sx;

            var sym = SyRegex.Match(l);
            if (sym.Success && double.TryParse(sym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double sy))
                model.SigmaY = sy;

            var txm = TxyRegex.Match(l);
            if (txm.Success && double.TryParse(txm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double txy))
                model.TauXY = txy;
        }

        return model;
    }

    public static string RenderMohrsCircleSvg(MohrsCircleModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 160;
        double oy = 150;
        double scale = 1.1;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-mohr-svg\">");
        sb.AppendLine("""
            <style>
              .mo-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .mo-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .mo-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .mo-axis { stroke: #475569; stroke-width: 1.2; }
              .mo-circle { fill: #38bdf8; fill-opacity: 0.08; stroke: #38bdf8; stroke-width: 2; }
              .mo-diam { stroke: #f43f5e; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .mo-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .mo-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .mo-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .mo-lbl { font-family: Segoe UI, sans-serif; font-size: 10px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"mo-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"mo-title\">⚙ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"mo-meta\">σx = {model.SigmaX:F0} MPa, σy = {model.SigmaY:F0} MPa, τxy = {model.TauXY:F0} MPa</text>");

        // Coordinate axes: Sigma (horizontal), Tau (vertical)
        sb.AppendLine($"  <line x1=\"20\" y1=\"{oy}\" x2=\"290\" y2=\"{oy}\" class=\"mo-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"60\" x2=\"{ox}\" y2=\"240\" class=\"mo-axis\" />");
        sb.AppendLine($"  <text x=\"285\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">σ</text>");
        sb.AppendLine($"  <text x=\"{ox + 6}\" y=\"70\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">τ (cw)</text>");

        // Mohr's circle center & radius mapped to SVG canvas
        double cx = ox + (model.CenterSigma * scale);
        double cy = oy;
        double rad = Math.Max(5, model.RadiusR * scale);

        sb.AppendLine($"  <circle cx=\"{cx:F1}\" cy=\"{cy:F1}\" r=\"{rad:F1}\" class=\"mo-circle\" />");

        // Diameter connecting Point A (sigmaX, -tauXY) and Point B (sigmaY, +tauXY)
        double ax = ox + (model.SigmaX * scale);
        double ay = oy + (model.TauXY * scale); // Downwards is positive in SVG
        double bx = ox + (model.SigmaY * scale);
        double by = oy - (model.TauXY * scale);

        sb.AppendLine($"  <line x1=\"{ax:F1}\" y1=\"{ay:F1}\" x2=\"{bx:F1}\" y2=\"{by:F1}\" class=\"mo-diam\" />");
        sb.AppendLine($"  <circle cx=\"{ax:F1}\" cy=\"{ay:F1}\" r=\"4.5\" class=\"mo-pt\" />");
        sb.AppendLine($"  <circle cx=\"{bx:F1}\" cy=\"{by:F1}\" r=\"4.5\" class=\"mo-pt\" />");
        sb.AppendLine($"  <text x=\"{ax + 6:F1}\" y=\"{ay + 4:F1}\" font-family=\"monospace\" font-size=\"9\" fill=\"#fbbf24\">A(σx, τxy)</text>");

        // Principal Stresses Sigma1 and Sigma2 on axis
        double s1x = cx + rad;
        double s2x = cx - rad;
        sb.AppendLine($"  <circle cx=\"{s1x:F1}\" cy=\"{cy:F1}\" r=\"3.5\" fill=\"#10b981\" />");
        sb.AppendLine($"  <circle cx=\"{s2x:F1}\" cy=\"{cy:F1}\" r=\"3.5\" fill=\"#10b981\" />");

        // Results Card on Right
        double cardX = 310;
        double cardY = 65;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"185\" rx=\"6\" class=\"mo-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 22}\" class=\"mo-lbl\">Principal Stress (σ₁):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 38}\" class=\"mo-val\" fill=\"#10b981\">{model.PrincipalSigma1:F1} MPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 62}\" class=\"mo-lbl\">Minor Principal (σ₂):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 78}\" class=\"mo-val\" fill=\"#10b981\">{model.PrincipalSigma2:F1} MPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 102}\" class=\"mo-lbl\">Max In-Plane Shear (τ_max):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 118}\" class=\"mo-val\" fill=\"#f43f5e\">{model.MaxShearTau:F1} MPa</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 142}\" class=\"mo-lbl\">Principal Plane Angle (θp):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 158}\" class=\"mo-val\" fill=\"#fbbf24\">{model.PrincipalAngleDeg:F1}°</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
