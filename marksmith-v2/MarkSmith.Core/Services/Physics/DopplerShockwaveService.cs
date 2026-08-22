using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Physics;

public class DopplerModel
{
    public string Title { get; set; } = "Doppler Effect & Mach Shockwave";
    public double MachNumber { get; set; } = 1.4; // M = v / v_sound
    public int WavefrontCount { get; set; } = 8;
    public double SoundSpeed { get; set; } = 343.0; // m/s
    public bool IsSupersonic => MachNumber > 1.0;
    public bool IsTransonic => Math.Abs(MachNumber - 1.0) < 0.05;
    public double MachAngleDeg => IsSupersonic ? Math.Asin(1.0 / MachNumber) * (180.0 / Math.PI) : 90.0;
}

public static class DopplerShockwaveService
{
    private static readonly Regex DopplerFenceRegex = new(
        @":::(?:doppler|shockwave|mach)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MachRegex = new(
        @"mach\s*[:=]\s*""?(\d+(?:\.\d+)?)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WavesRegex = new(
        @"(?:waves|wavefronts|count)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static DopplerModel ParseDoppler(string blockText, string defaultTitle = "Doppler Effect & Mach Shockwave")
    {
        var model = new DopplerModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = DopplerFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var mm = MachRegex.Match(header);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double m))
            {
                model.MachNumber = Math.Clamp(m, 0.1, 5.0);
            }
            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var mm = MachRegex.Match(l);
            if (mm.Success && double.TryParse(mm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double m))
            {
                model.MachNumber = Math.Clamp(m, 0.1, 5.0);
            }
            var wm = WavesRegex.Match(l);
            if (wm.Success && int.TryParse(wm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wc))
            {
                model.WavefrontCount = Math.Clamp(wc, 3, 16);
            }
        }

        return model;
    }

    public static string RenderDopplerSvg(DopplerModel model)
    {
        double width = 500;
        double height = 280;
        double cy = 145;
        double sourceX = 380; // Current position of moving emitter
        double timeSpacing = 16.0;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-doppler-svg\">");
        sb.AppendLine("""
            <style>
              .dop-bg { fill: #0a0f1d; stroke: #1e293b; stroke-width: 1.5; }
              .dop-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .dop-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .dop-wave { fill: none; stroke: #0284c7; stroke-width: 1.5; stroke-opacity: 0.7; }
              .dop-shock { stroke: #f43f5e; stroke-width: 2.5; stroke-dasharray: 6 3; }
              .dop-emitter { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .dop-center { fill: #38bdf8; opacity: 0.5; }
              .dop-label { font-family: monospace; font-size: 10px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"dop-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"dop-title\">✈ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        string regime = model.IsSupersonic ? $"Supersonic (Mach {model.MachNumber:F2}, Shock Cone μ={model.MachAngleDeg:F1}°)"
                      : model.IsTransonic ? $"Transonic (Mach {model.MachNumber:F2})"
                      : $"Subsonic (Mach {model.MachNumber:F2})";

        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"dop-meta\">Regime: {regime}</text>");

        // Render flight trajectory axis
        sb.AppendLine($"  <line x1=\"30\" y1=\"{cy}\" x2=\"{width - 30}\" y2=\"{cy}\" stroke=\"#1e293b\" stroke-width=\"1.2\" stroke-dasharray=\"4 4\" />");

        // Render circular wavefronts emitted at past time steps
        for (int i = 1; i <= model.WavefrontCount; i++)
        {
            double dt = i * timeSpacing;
            double pastCenterX = sourceX - model.MachNumber * dt;
            double radius = dt;

            if (pastCenterX >= -50 && pastCenterX <= width + 50 && radius > 0)
            {
                sb.AppendLine($"  <circle cx=\"{pastCenterX:F1}\" cy=\"{cy:F1}\" r=\"{radius:F1}\" class=\"dop-wave\" />");
                sb.AppendLine($"  <circle cx=\"{pastCenterX:F1}\" cy=\"{cy:F1}\" r=\"2\" class=\"dop-center\" />");
            }
        }

        // Render Mach Cone shockwave lines if Supersonic
        if (model.IsSupersonic)
        {
            double muRad = Math.Asin(1.0 / model.MachNumber);
            double coneLength = 340;
            double dx = coneLength * Math.Cos(muRad);
            double dy = coneLength * Math.Sin(muRad);

            double topX = sourceX - dx;
            double topY = cy - dy;
            double botX = sourceX - dx;
            double botY = cy + dy;

            sb.AppendLine($"  <line x1=\"{sourceX:F1}\" y1=\"{cy:F1}\" x2=\"{topX:F1}\" y2=\"{topY:F1}\" class=\"dop-shock\" />");
            sb.AppendLine($"  <line x1=\"{sourceX:F1}\" y1=\"{cy:F1}\" x2=\"{botX:F1}\" y2=\"{botY:F1}\" class=\"dop-shock\" />");

            // Shockwave Envelope Angle Label
            sb.AppendLine($"  <text x=\"{sourceX - 90:F1}\" y=\"{cy - 45:F1}\" class=\"dop-label\" fill=\"#f43f5e\">Mach Cone (μ={model.MachAngleDeg:F1}°)</text>");
        }

        // Emitter Source Dot
        sb.AppendLine($"  <circle cx=\"{sourceX:F1}\" cy=\"{cy:F1}\" r=\"5.5\" class=\"dop-emitter\" />");
        sb.AppendLine($"  <text x=\"{sourceX + 10:F1}\" y=\"{cy + 4:F1}\" class=\"dop-label\" fill=\"#fbbf24\">Source (v={model.MachNumber * model.SoundSpeed:F0} m/s)</text>");

        // Footer
        sb.AppendLine($"  <text x=\"20\" y=\"{height - 16}\" class=\"dop-label\">Wavefronts compress in motion direction (Doppler blue-shift) and expand behind.</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
