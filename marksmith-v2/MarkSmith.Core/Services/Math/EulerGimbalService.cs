using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.MathCore;

public class EulerGimbalModel
{
    public string Title { get; set; } = "3D Euler Angle Gimbal Simulator";
    public double RollDeg { get; set; } = 30.0;   // phi (x-axis)
    public double PitchDeg { get; set; } = 15.0;  // theta (y-axis)
    public double YawDeg { get; set; } = -45.0;   // psi (z-axis)
    public string Sequence { get; set; } = "ZYX";

    public bool IsGimbalLock => Math.Abs(Math.Abs(PitchDeg) - 90.0) < 1.0;

    // Unit Quaternion [qw, qx, qy, qz]
    public (double W, double X, double Y, double Z) Quaternion
    {
        get
        {
            double r = (RollDeg * Math.PI / 180.0) / 2.0;
            double p = (PitchDeg * Math.PI / 180.0) / 2.0;
            double y = (YawDeg * Math.PI / 180.0) / 2.0;

            double cr = Math.Cos(r), sr = Math.Sin(r);
            double cp = Math.Cos(p), sp = Math.Sin(p);
            double cy = Math.Cos(y), sy = Math.Sin(y);

            double qw = cr * cp * cy + sr * sp * sy;
            double qx = sr * cp * cy - cr * sp * sy;
            double qy = cr * sp * cy + sr * cp * sy;
            double qz = cr * cp * sy - sr * sp * cy;

            return (qw, qx, qy, qz);
        }
    }
}

public static class EulerGimbalService
{
    private static readonly Regex GimbalFenceRegex = new(
        @":::(?:euler-gimbal|gimbal|euler-angle)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RollRegex = new(
        @"roll\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PitchRegex = new(
        @"pitch\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YawRegex = new(
        @"yaw\s*[:=]\s*""?(-?\d+(?:\.\d+)?)(?:deg)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static EulerGimbalModel ParseGimbal(string blockText, string defaultTitle = "3D Euler Angle Gimbal Simulator")
    {
        var model = new EulerGimbalModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = GimbalFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var rm = RollRegex.Match(header);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double roll))
                model.RollDeg = roll;

            var pm = PitchRegex.Match(header);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pitch))
                model.PitchDeg = Math.Clamp(pitch, -90.0, 90.0);

            var ym = YawRegex.Match(header);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double yaw))
                model.YawDeg = yaw;

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var rm = RollRegex.Match(l);
            if (rm.Success && double.TryParse(rm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double roll))
                model.RollDeg = roll;

            var pm = PitchRegex.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double pitch))
                model.PitchDeg = Math.Clamp(pitch, -90.0, 90.0);

            var ym = YawRegex.Match(l);
            if (ym.Success && double.TryParse(ym.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double yaw))
                model.YawDeg = yaw;
        }

        return model;
    }

    public static string RenderGimbalSvg(EulerGimbalModel model)
    {
        double width = 500;
        double height = 280;
        double cx = 160;
        double cy = 150;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-gimbal-svg\">");
        sb.AppendLine("""
            <style>
              .gm-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .gm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .gm-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .gm-ring-yaw { fill: none; stroke: #3b82f6; stroke-width: 2.5; }
              .gm-ring-pitch { fill: none; stroke: #10b981; stroke-width: 2.5; }
              .gm-ring-roll { fill: none; stroke: #f43f5e; stroke-width: 2.5; }
              .gm-axis-x { stroke: #f43f5e; stroke-width: 2; }
              .gm-axis-y { stroke: #10b981; stroke-width: 2; }
              .gm-axis-z { stroke: #3b82f6; stroke-width: 2; }
              .gm-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .gm-val { font-family: monospace; font-size: 11px; font-weight: 700; fill: #38bdf8; }
              .gm-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"gm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"gm-title\">🧭 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"gm-meta\">Roll(φ) = {model.RollDeg:F0}° • Pitch(θ) = {model.PitchDeg:F0}° • Yaw(ψ) = {model.YawDeg:F0}°</text>");

        // Outer Yaw Ring (Z-axis - Blue)
        sb.AppendLine($"  <ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"90\" ry=\"35\" class=\"gm-ring-yaw\" transform=\"rotate({model.YawDeg}, {cx}, {cy})\" />");

        // Middle Pitch Ring (Y-axis - Green)
        sb.AppendLine($"  <ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"70\" ry=\"28\" class=\"gm-ring-pitch\" transform=\"rotate({model.PitchDeg + 45}, {cx}, {cy})\" />");

        // Inner Roll Ring (X-axis - Red)
        sb.AppendLine($"  <ellipse cx=\"{cx}\" cy=\"{cy}\" rx=\"50\" ry=\"20\" class=\"gm-ring-roll\" transform=\"rotate({model.RollDeg - 45}, {cx}, {cy})\" />");

        // Central Body Frame Vector Arrows
        double radRoll = model.RollDeg * Math.PI / 180.0;
        double radPitch = model.PitchDeg * Math.PI / 180.0;
        double radYaw = model.YawDeg * Math.PI / 180.0;

        double axLen = 42.0;
        double xEndX = cx + axLen * Math.Cos(radYaw);
        double xEndY = cy + axLen * Math.Sin(radYaw) * 0.4 - axLen * Math.Sin(radPitch) * 0.4;
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{cy}\" x2=\"{xEndX:F1}\" y2=\"{xEndY:F1}\" class=\"gm-axis-x\" />");
        sb.AppendLine($"  <text x=\"{xEndX + 4:F1}\" y=\"{xEndY + 3:F1}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#f43f5e\">X(Roll)</text>");

        double zEndY = cy - axLen * 0.85;
        sb.AppendLine($"  <line x1=\"{cx}\" y1=\"{cy}\" x2=\"{cx}\" y2=\"{zEndY:F1}\" class=\"gm-axis-z\" />");
        sb.AppendLine($"  <text x=\"{cx + 4}\" y=\"{zEndY - 4:F1}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#3b82f6\">Z(Yaw)</text>");

        // Center Pivot Sphere
        sb.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"5\" fill=\"#f8fafc\" />");

        // Results Card on Right (Quaternion & Direction Matrix)
        double cardX = 300;
        double cardY = 60;
        var q = model.Quaternion;

        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"180\" height=\"195\" rx=\"6\" class=\"gm-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"gm-lbl\" font-weight=\"700\" fill=\"#f8fafc\">Attitude Quaternion:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 38}\" class=\"gm-val\">w = {q.W:F3}</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 54}\" class=\"gm-val\">x = {q.X:F3}, y = {q.Y:F3}</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 70}\" class=\"gm-val\">z = {q.Z:F3}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 95}\" class=\"gm-lbl\" font-weight=\"700\" fill=\"#f8fafc\">Euler Rotation Angles:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"gm-val\" fill=\"#f43f5e\">Roll (φ): {model.RollDeg:F1}°</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 128}\" class=\"gm-val\" fill=\"#10b981\">Pitch (θ): {model.PitchDeg:F1}°</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 144}\" class=\"gm-val\" fill=\"#3b82f6\">Yaw (ψ): {model.YawDeg:F1}°</text>");

        string lockStatus = model.IsGimbalLock ? "⚠ Gimbal Lock! (|θ| ≈ 90°)" : "✓ Free 3-DOF Rotation";
        string lockColor = model.IsGimbalLock ? "#f43f5e" : "#10b981";
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 175}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9.5\" font-weight=\"700\" fill=\"{lockColor}\">{lockStatus}</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
