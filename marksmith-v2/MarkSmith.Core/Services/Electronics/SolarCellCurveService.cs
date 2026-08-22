using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class SolarCellModel
{
    public string Title { get; set; } = "PV Solar Cell IV/PV Power Characteristics";
    public double ShortCircuitIsc { get; set; } = 9.5;   // Isc (A)
    public double OpenCircuitVoc { get; set; } = 45.0;   // Voc (V)
    public double IdealityFactorN { get; set; } = 1.3;   // n
    public double IrradianceWm2 { get; set; } = 1000.0;  // G (W/m2)
    public double TemperatureC { get; set; } = 25.0;     // T (deg C)

    public double ThermalVoltageVt => (1.380649e-23 * (TemperatureC + 273.15)) / 1.60217663e-19; // Vt approx 0.0257 V

    // Reverse saturation current I0
    public double SaturationCurrentI0 => ShortCircuitIsc / (Math.Exp(OpenCircuitVoc / (IdealityFactorN * ThermalVoltageVt * 60.0)) - 1.0); // 60 series cells

    // Maximum Power Point MPP
    public double Vmp => OpenCircuitVoc * 0.82;
    public double Imp => ShortCircuitIsc * 0.92;
    public double Pmax => Vmp * Imp;

    // Fill Factor FF = Pmax / (Voc * Isc)
    public double FillFactor => Pmax / (OpenCircuitVoc * ShortCircuitIsc);
    public double FillFactorPercent => FillFactor * 100.0;
}

public static class SolarCellCurveService
{
    private static readonly Regex SolarFenceRegex = new(
        @":::(?:solar-cell|pv-curve|photovoltaic)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IscRegex = new(
        @"(?:isc|short_circuit|current)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[aA])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VocRegex = new(
        @"(?:voc|open_circuit|voltage)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[vV])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IrradianceRegex = new(
        @"(?:irradiance|g|sun)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[wW]/m2)?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TempRegex = new(
        @"(?:temp|temperature|t)\s*[:=]\s*""?(\d+(?:\.\d+)?)(?:[cC])?""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SolarCellModel ParseSolar(string blockText, string defaultTitle = "PV Solar Cell IV/PV Power Characteristics")
    {
        var model = new SolarCellModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = SolarFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            var im = IscRegex.Match(header);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double isc))
                model.ShortCircuitIsc = Math.Clamp(isc, 0.1, 100.0);

            var vm = VocRegex.Match(header);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double voc))
                model.OpenCircuitVoc = Math.Clamp(voc, 0.5, 500.0);

            var gm = IrradianceRegex.Match(header);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.IrradianceWm2 = Math.Clamp(g, 50.0, 2000.0);

            var tm0 = TempRegex.Match(header);
            if (tm0.Success && double.TryParse(tm0.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double t))
                model.TemperatureC = Math.Clamp(t, -40.0, 85.0);

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            var im = IscRegex.Match(l);
            if (im.Success && double.TryParse(im.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double isc))
                model.ShortCircuitIsc = Math.Clamp(isc, 0.1, 100.0);

            var vm = VocRegex.Match(l);
            if (vm.Success && double.TryParse(vm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double voc))
                model.OpenCircuitVoc = Math.Clamp(voc, 0.5, 500.0);

            var gm = IrradianceRegex.Match(l);
            if (gm.Success && double.TryParse(gm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double g))
                model.IrradianceWm2 = Math.Clamp(g, 50.0, 2000.0);

            var tm0 = TempRegex.Match(l);
            if (tm0.Success && double.TryParse(tm0.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double t))
                model.TemperatureC = Math.Clamp(t, -40.0, 85.0);
        }

        return model;
    }

    public static string RenderSolarSvg(SolarCellModel model)
    {
        double width = 500;
        double height = 280;
        double ox = 50;
        double oy = 230;
        double axisW = 235;
        double axisH = 150;

        double maxV = model.OpenCircuitVoc * 1.12;
        double maxI = model.ShortCircuitIsc * 1.15;
        double maxP = model.Pmax * 1.25;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-solar-svg\">");
        sb.AppendLine("""
            <style>
              .pv-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .pv-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .pv-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .pv-axis { stroke: #475569; stroke-width: 1.2; }
              .pv-iv-curve { fill: none; stroke: #38bdf8; stroke-width: 2.2; }
              .pv-power-curve { fill: none; stroke: #10b981; stroke-width: 2; stroke-dasharray: 4 2; }
              .pv-mpp-pt { fill: #fbbf24; stroke: #ffffff; stroke-width: 1.5; }
              .pv-card-bg { fill: #1e293b; stroke: #334155; stroke-width: 1; }
              .pv-val { font-family: monospace; font-size: 11.5px; font-weight: 700; fill: #38bdf8; }
              .pv-lbl { font-family: Segoe UI, sans-serif; font-size: 9.5px; fill: #94a3b8; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"pv-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"pv-title\">☀️ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"pv-meta\">Isc = {model.ShortCircuitIsc:F1}A • Voc = {model.OpenCircuitVoc:F1}V • Pmax = {model.Pmax:F0} W (at {model.IrradianceWm2:F0} W/m²)</text>");

        // Coordinate Axes
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox + axisW + 15}\" y2=\"{oy}\" class=\"pv-axis\" />");
        sb.AppendLine($"  <line x1=\"{ox}\" y1=\"{oy}\" x2=\"{ox}\" y2=\"{oy - axisH - 10}\" class=\"pv-axis\" />");
        sb.AppendLine($"  <text x=\"{ox + axisW + 10}\" y=\"{oy + 14}\" font-family=\"monospace\" font-size=\"9\" fill=\"#94a3b8\">V (V)</text>");
        sb.AppendLine($"  <text x=\"{ox - 8}\" y=\"{oy - axisH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#38bdf8\" text-anchor=\"end\">I (A)</text>");

        // Right Axis for Power P(V)
        double rightAxisX = ox + axisW;
        sb.AppendLine($"  <line x1=\"{rightAxisX}\" y1=\"{oy}\" x2=\"{rightAxisX}\" y2=\"{oy - axisH - 10}\" stroke=\"#10b981\" stroke-width=\"1.2\" />");
        sb.AppendLine($"  <text x=\"{rightAxisX + 4}\" y=\"{oy - axisH - 4}\" font-family=\"monospace\" font-size=\"9\" fill=\"#10b981\">P (W)</text>");

        // Generate IV and PV Curves
        var ivPath = new StringBuilder();
        var pvPath = new StringBuilder();
        int steps = 60;

        for (int i = 0; i <= steps; i++)
        {
            double v = (i / (double)steps) * model.OpenCircuitVoc;
            // Solar single-diode approximation: I(V) = Isc * [ 1 - (V/Voc)^12 ]
            double iRatio = Math.Max(0.0, 1.0 - Math.Pow(v / model.OpenCircuitVoc, 10));
            double current = model.ShortCircuitIsc * iRatio;
            double power = v * current;

            double px = ox + (v / maxV) * axisW;
            double pyI = oy - (current / maxI) * axisH;
            double pyP = oy - (power / maxP) * axisH;

            if (i == 0)
            {
                ivPath.Append($"M {px:F1} {pyI:F1}");
                pvPath.Append($"M {px:F1} {pyP:F1}");
            }
            else
            {
                ivPath.Append($" L {px:F1} {pyI:F1}");
                pvPath.Append($" L {px:F1} {pyP:F1}");
            }
        }

        sb.AppendLine($"  <path d=\"{ivPath}\" class=\"pv-iv-curve\" />");
        sb.AppendLine($"  <path d=\"{pvPath}\" class=\"pv-power-curve\" />");

        // Maximum Power Point Marker
        double mppX = ox + (model.Vmp / maxV) * axisW;
        double mppY = oy - (model.Pmax / maxP) * axisH;
        sb.AppendLine($"  <circle cx=\"{mppX:F1}\" cy=\"{mppY:F1}\" r=\"4.5\" class=\"pv-mpp-pt\" />");
        sb.AppendLine($"  <text x=\"{mppX + 6:F1}\" y=\"{mppY - 4:F1}\" font-family=\"monospace\" font-size=\"8.5\" font-weight=\"700\" fill=\"#fbbf24\">MPPT</text>");

        // Results Card on Right
        double cardX = 310;
        double cardY = 60;
        sb.AppendLine($"  <rect x=\"{cardX}\" y=\"{cardY}\" width=\"170\" height=\"195\" rx=\"6\" class=\"pv-card-bg\" />");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 20}\" class=\"pv-lbl\">Max Power (Pmax):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 36}\" class=\"pv-val\" font-size=\"14\" fill=\"#10b981\">{model.Pmax:F1} W</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 58}\" class=\"pv-lbl\">MPP Voltage (Vmp):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 74}\" class=\"pv-val\">{model.Vmp:F1} V</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 96}\" class=\"pv-lbl\">MPP Current (Imp):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 112}\" class=\"pv-val\">{model.Imp:F2} A</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 134}\" class=\"pv-lbl\">Fill Factor (FF):</text>");
        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 150}\" class=\"pv-val\" fill=\"#fbbf24\">FF = {model.FillFactorPercent:F1} %</text>");

        sb.AppendLine($"  <text x=\"{cardX + 12}\" y=\"{cardY + 176}\" font-family=\"Segoe UI, sans-serif\" font-size=\"9\" fill=\"#94a3b8\">Single-Diode PV Model</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
