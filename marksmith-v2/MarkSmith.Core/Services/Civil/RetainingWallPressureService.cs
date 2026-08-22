using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Civil;

public class RetainingWallModel
{
    public string Title { get; set; } = "Rankine Earth Pressure Retaining Wall";
    public double HeightM { get; set; } = 6.0;         // Wall height H (m)
    public double UnitWeightGamma { get; set; } = 18.0;// Soil unit weight gamma (kN/m3)
    public double FrictionAnglePhiDeg { get; set; } = 32.0; // Soil internal friction angle phi (deg)
    public double CohesionC { get; set; } = 0.0;       // Soil cohesion c (kPa)
    public double SurchargeQ { get; set; } = 15.0;     // Uniform surcharge q (kPa)

    // Friction angle in radians
    public double PhiRad => FrictionAnglePhiDeg * (Math.PI / 180.0);

    // Rankine Active & Passive Earth Pressure Coefficients
    public double Ka => (1.0 - Math.Sin(PhiRad)) / Math.Max(1e-4, 1.0 + Math.Sin(PhiRad));
    public double Kp => (1.0 + Math.Sin(PhiRad)) / Math.Max(1e-4, 1.0 - Math.Sin(PhiRad));

    // Lateral Earth Pressures (kPa)
    public double SurchargePressureTop => Ka * SurchargeQ;
    public double SoilPressureBottom => Math.Max(0.0, Ka * (UnitWeightGamma * HeightM + SurchargeQ) - 2.0 * CohesionC * Math.Sqrt(Ka));

    // Resultant Active Lateral Forces (kN/m run)
    public double ForceSurcharge => Ka * SurchargeQ * HeightM;
    public double ForceSoil => 0.5 * Ka * UnitWeightGamma * Math.Pow(HeightM, 2);
    public double TotalActiveThrustPa => ForceSurcharge + ForceSoil;

    // Line of action from base (m)
    public double LineOfActionY => (ForceSurcharge * (HeightM / 2.0) + ForceSoil * (HeightM / 3.0)) / Math.Max(1e-4, TotalActiveThrustPa);

    // Overturning Moment about toe (kNm/m)
    public double OverturningMoment => ForceSurcharge * (HeightM / 2.0) + ForceSoil * (HeightM / 3.0);
}

public static class RetainingWallPressureService
{
    private static readonly Regex WallFenceRegex = new(
        @":::(?:retaining-wall|retainingwall|earth-pressure)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeightRegex = new(
        @"(?:\bheight\b|\bh\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:m)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GammaRegex = new(
        @"(?:\bgamma\b|\bunit_weight\b|\bdensity\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kN/m3)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhiRegex = new(
        @"(?:\bphi\b|\bfriction\b|\bfriction_angle\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:deg|°)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CohesionRegex = new(
        @"(?:\bcohesion\b|\bc\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kPa)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SurchargeRegex = new(
        @"(?:\bsurcharge\b|\bq\b)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:kPa)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RetainingWallModel ParseRetainingWall(string blockText, string defaultTitle = "Rankine Earth Pressure Retaining Wall")
    {
        var model = new RetainingWallModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = WallFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var hm = HeightRegex.Match(header);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.HeightM = Math.Clamp(h, 1.0, 30.0);

            var gm = GammaRegex.Match(header);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.UnitWeightGamma = Math.Clamp(g, 10.0, 30.0);

            var pm = PhiRegex.Match(header);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                model.FrictionAnglePhiDeg = Math.Clamp(p, 10.0, 50.0);

            var cm = CohesionRegex.Match(header);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionC = Math.Clamp(c, 0.0, 200.0);

            var qm = SurchargeRegex.Match(header);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double q))
                model.SurchargeQ = Math.Clamp(q, 0.0, 500.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var hm = HeightRegex.Match(l);
            if (hm.Success && double.TryParse(hm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                model.HeightM = Math.Clamp(h, 1.0, 30.0);

            var gm = GammaRegex.Match(l);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.UnitWeightGamma = Math.Clamp(g, 10.0, 30.0);

            var pm = PhiRegex.Match(l);
            if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                model.FrictionAnglePhiDeg = Math.Clamp(p, 10.0, 50.0);

            var cm = CohesionRegex.Match(l);
            if (cm.Success && double.TryParse(cm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double c))
                model.CohesionC = Math.Clamp(c, 0.0, 200.0);

            var qm = SurchargeRegex.Match(l);
            if (qm.Success && double.TryParse(qm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double q))
                model.SurchargeQ = Math.Clamp(q, 0.0, 500.0);
        }

        return model;
    }

    public static string RenderRetainingWallSvg(RetainingWallModel model)
    {
        double width = 520;
        double height = 280;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-wall-svg\">");
        sb.AppendLine("""
            <style>
              .rw-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .rw-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .rw-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .rw-concrete { fill: #475569; stroke: #64748b; stroke-width: 1.5; }
              .rw-soil { fill: #334155; stroke: #475569; stroke-width: 1; fill-opacity: 0.6; }
              .rw-pressure { fill: #0284c7; stroke: #38bdf8; stroke-width: 1.5; fill-opacity: 0.35; }
              .rw-thrust-arrow { stroke: #f43f5e; stroke-width: 2.5; marker-end: url(#arrow-thrust); }
              .rw-surcharge { fill: #d97706; stroke: #fbbf24; stroke-width: 1; fill-opacity: 0.3; }
              .rw-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .rw-val { font-family: monospace; font-size: 12px; font-weight: 700; fill: #38bdf8; }
              .rw-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine("""
            <defs>
              <marker id="arrow-thrust" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                <path d="M 0 1 L 10 5 L 0 9 z" fill="#f43f5e" />
              </marker>
            </defs>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"rw-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"rw-title\">🧱 {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"rw-meta\">H = {model.HeightM:F1}m • γ = {model.UnitWeightGamma:F1}kN/m³ • φ' = {model.FrictionAnglePhiDeg:F1}° • q = {model.SurchargeQ:F0}kPa • Ka = {model.Ka:F3}</text>");

        // Retaining Wall Concrete Geometry
        double wallX = 90;
        double wallTopY = 70;
        double wallBotY = 220;
        double stemWidth = 14;
        double baseLeft = wallX - 25;
        double baseRight = wallX + 35;
        double baseHeight = 16;

        // Backfill Soil Region on Right
        sb.AppendLine($"  <polygon points=\"{wallX + stemWidth},{wallTopY} 240,{wallTopY} 240,{wallBotY} {baseRight},{wallBotY} {baseRight},{wallBotY - baseHeight} {wallX + stemWidth},{wallBotY - baseHeight}\" class=\"rw-soil\" />");

        // Surcharge Block on top of soil
        if (model.SurchargeQ > 0)
        {
            sb.AppendLine($"  <rect x=\"{wallX + stemWidth}\" y=\"{wallTopY - 14}\" width=\"136\" height=\"14\" class=\"rw-surcharge\" />");
            sb.AppendLine($"  <text x=\"{wallX + stemWidth + 25}\" y=\"{wallTopY - 4}\" font-family=\"monospace\" font-size=\"8.5\" fill=\"#fbbf24\">q = {model.SurchargeQ:F0} kPa</text>");
        }

        // Concrete Wall Stem and Base
        sb.AppendLine($"  <polygon points=\"{wallX},{wallTopY} {wallX + stemWidth},{wallTopY} {wallX + stemWidth},{wallBotY - baseHeight} {baseRight},{wallBotY - baseHeight} {baseRight},{wallBotY} {baseLeft},{wallBotY} {baseLeft},{wallBotY - baseHeight} {wallX},{wallBotY - baseHeight}\" class=\"rw-concrete\" />");

        // Active Pressure Diagram on Back of Wall
        double maxPress = Math.Max(1.0, model.SoilPressureBottom);
        double scalePress = Math.Min(60.0, 70.0 * (maxPress / 80.0));
        double topPressWidth = Math.Min(30.0, scalePress * (model.SurchargePressureTop / maxPress));

        // Pressure polygon: back of stem to pressure line
        double pX = wallX + stemWidth;
        sb.AppendLine($"  <polygon points=\"{pX},{wallTopY} {pX + topPressWidth:F1},{wallTopY} {pX + scalePress:F1},{wallBotY - baseHeight} {pX},{wallBotY - baseHeight}\" class=\"rw-pressure\" />");
        sb.AppendLine($"  <line x1=\"{pX + topPressWidth:F1}\" y1=\"{wallTopY}\" x2=\"{pX + scalePress:F1}\" y2=\"{wallBotY - baseHeight}\" stroke=\"#38bdf8\" stroke-width=\"1.5\" />");

        // Active Thrust Resultant Arrow (Pa)
        double arrowY = wallBotY - baseHeight - (model.LineOfActionY / model.HeightM) * (wallBotY - baseHeight - wallTopY);
        sb.AppendLine($"  <line x1=\"{pX + scalePress + 35}\" y1=\"{arrowY:F1}\" x2=\"{pX + 5}\" y2=\"{arrowY:F1}\" class=\"rw-thrust-arrow\" />");
        sb.AppendLine($"  <text x=\"{pX + scalePress + 10}\" y=\"{arrowY - 6:F1}\" font-family=\"monospace\" font-size=\"9\" font-weight=\"700\" fill=\"#f43f5e\">Pa={model.TotalActiveThrustPa:F1}kN</text>");

        // Results Card on Right
        double cardX = 270;
        double cardY = 55;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"235\" height=\"205\" rx=\"6\" class=\"rw-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"rw-lbl\">Rankine Ka / Kp Coefficients:</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"rw-val\" font-size=\"14\" fill=\"#38bdf8\">Ka = {model.Ka:F3}  |  Kp = {model.Kp:F2}</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"rw-lbl\">Total Active Thrust (Pa):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"rw-val\" fill=\"#f43f5e\">Pa = {model.TotalActiveThrustPa:F1} kN/m</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"rw-lbl\">Thrust Breakdown (Soil + Surcharge):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"rw-val\" font-size=\"11\">P_soil={model.ForceSoil:F1} kN  |  P_q={model.ForceSurcharge:F1} kN</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"rw-lbl\">Line of Action (y_bar from base):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"rw-val\" fill=\"#fbbf24\">y = {model.LineOfActionY:F2} m ({model.LineOfActionY/model.HeightM*100:F0}% H)</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 172}\" class=\"rw-lbl\">Overturning Moment (M_over):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 188}\" class=\"rw-val\" fill=\"#10b981\">{model.OverturningMoment:F1} kNm/m</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
