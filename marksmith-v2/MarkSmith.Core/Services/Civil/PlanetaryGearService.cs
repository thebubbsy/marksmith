using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class PlanetaryGearModel
{
    public string Title { get; set; } = "Planetary Epicyclic Gear Train";
    public int SunTeethZs { get; set; } = 18;     // Zs
    public int PlanetTeethZp { get; set; } = 24;  // Zp
    public int PlanetCount { get; set; } = 3;     // Number of planet gears
    public string FixedMember { get; set; } = "ring"; // "ring", "sun", "carrier"
    public string InputMember { get; set; } = "sun";

    // Ring Gear Teeth Zr = Zs + 2 * Zp
    public int RingTeethZr => SunTeethZs + 2 * PlanetTeethZp;

    // Willis Speed Ratio i = omega_in / omega_out
    public double SpeedRatio
    {
        get
        {
            if (FixedMember.Contains("ring"))
            {
                // Fixed Ring, Sun Input, Carrier Output: i = 1 + Zr / Zs
                return 1.0 + (double)RingTeethZr / SunTeethZs;
            }
            if (FixedMember.Contains("sun"))
            {
                // Fixed Sun, Ring Input, Carrier Output: i = 1 + Zs / Zr
                return 1.0 + (double)SunTeethZs / RingTeethZr;
            }
            // Fixed Carrier: i = -Zr / Zs (Inverting reducer)
            return -(double)RingTeethZr / SunTeethZs;
        }
    }

    public double MechanicalAdvantage => Math.Abs(SpeedRatio);
}

public static class PlanetaryGearService
{
    private static readonly Regex GearFenceRegex = new(
        @":::(?:gear-train|planetary-gear|epicyclic-gear)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SunRegex = new(
        @"(?:sun|zs)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlanetRegex = new(
        @"(?:planet|zp)\s*[:=]\s*""?(\d+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FixedRegex = new(
        @"(?:fixed|held)\s*[:=]\s*""?([a-zA-Z0-9_\-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedTitleRe = new(@"""([^""]+)""", RegexOptions.Compiled);

    public static PlanetaryGearModel ParseGear(string blockText, string defaultTitle = "Planetary Epicyclic Gear Train")
    {
        var model = new PlanetaryGearModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = GearFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = QuotedTitleRe.Match(header);
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var sm = SunRegex.Match(header);
            if (sm.Success && int.TryParse(sm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int zs))
                model.SunTeethZs = Math.Clamp(zs, 10, 100);

            var pm = PlanetRegex.Match(header);
            if (pm.Success && int.TryParse(pm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int zp))
                model.PlanetTeethZp = Math.Clamp(zp, 10, 100);

            var fm = FixedRegex.Match(header);
            if (fm.Success) model.FixedMember = fm.Groups[1].Value.ToLowerInvariant();

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var sm = SunRegex.Match(l);
            if (sm.Success && int.TryParse(sm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int zs))
                model.SunTeethZs = Math.Clamp(zs, 10, 100);

            var pm = PlanetRegex.Match(l);
            if (pm.Success && int.TryParse(pm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int zp))
                model.PlanetTeethZp = Math.Clamp(zp, 10, 100);

            var fm = FixedRegex.Match(l);
            if (fm.Success) model.FixedMember = fm.Groups[1].Value.ToLowerInvariant();
        }

        return model;
    }

    public static string RenderGearSvg(PlanetaryGearModel model)
    {
        double width = 500;
        double height = 280;
        double cx = 150;
        double cy = 150;

        double rSun = 26.0;
        double rPlanet = 28.0;
        double rRing = rSun + 2.0 * rPlanet; // approx 82 px
        double rCarrier = rSun + rPlanet;   // 54 px

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-gear-svg\">");
        sb.AppendLine("""
            <style>
              .gr-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .gr-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .gr-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .gr-ring { fill: none; stroke: #64748b; stroke-width: 4; stroke-dasharray: 6 3; }
              .gr-sun { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .gr-planet { fill: #0284c7; stroke: #38bdf8; stroke-width: 1.5; }
              .gr-carrier { fill: none; stroke: #10b981; stroke-width: 1.5; stroke-dasharray: 4 2; }
              .gr-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .gr-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .gr-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"gr-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"gr-title\">⚙ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"gr-meta\">Sun Zs = {model.SunTeethZs} • Planet Zp = {model.PlanetTeethZp} • Ring Zr = {model.RingTeethZr} (Fixed: {model.FixedMember.ToUpperInvariant()})</text>");

        // Outer Ring Gear Circle
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rRing:F1}\" class=\"gr-ring\" />");

        // Carrier Triangle / Pitch Circle
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rCarrier:F1}\" class=\"gr-carrier\" />");

        // Center Sun Gear
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{rSun:F1}\" class=\"gr-sun\" />");
        sb.AppendLine($"  <text x=\"{cx}\" y=\"{cy + 3}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#0f172a\" text-anchor=\"middle\">SUN</text>");

        // 3 Planet Gears orbiting at 120-degree intervals
        for (int i = 0; i < model.PlanetCount; i++)
        {
            double angleRad = (i * (360.0 / model.PlanetCount) - 90.0) * Math.PI / 180.0;
            double px = cx + rCarrier * Math.Cos(angleRad);
            double py = cy + rCarrier * Math.Sin(angleRad);

            sb.AppendLine($"  <circle cx=\"{px:F1}\" cy=\"{py:F1}\" r=\"{rPlanet:F1}\" class=\"gr-planet\" />");
            sb.AppendLine($"  <text x=\"{px:F1}\" y=\"{py + 3:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#ffffff\" text-anchor=\"middle\">P{i + 1}</text>");
        }

        // Results Card on Right
        double cardX = 300;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"gr-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"gr-lbl\">Speed Ratio (i = ω_in/ω_out):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"gr-val\" font-size=\"14\" fill=\"#fbbf24\">{model.SpeedRatio:F2} : 1</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"gr-lbl\">Ring Teeth (Zr = Zs + 2Zp):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"gr-val\" fill=\"#10b981\">Zr = {model.RingTeethZr} teeth</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"gr-lbl\">Torque Advantage (τ_out/τ_in):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"gr-val\">{model.MechanicalAdvantage:F2}x Multiplier</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"gr-lbl\">Kinematic Configuration:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"gr-val\" fill=\"#38bdf8\">Fixed {model.FixedMember.ToUpperInvariant()} Reducer</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Willis Epicyclic Mechanism</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
