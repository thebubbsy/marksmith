using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class TimingSignal
{
    public string Name { get; set; } = "CLK";
    public string Waveform { get; set; } = "P...P...P...P"; // P=pulse/clock, 1/H=high, 0/L=low, Z=high-z, D=bus data
}

public class DigitalTimingModel
{
    public string Title { get; set; } = "Digital Logic Timing Diagram";
    public List<TimingSignal> Signals { get; } = new();
    public int ClockCycles { get; set; } = 8;
}

public static class DigitalTimingDiagramService
{
    private static readonly Regex TimingFenceRegex = new(
        @":::(?:timing-diagram|timing-wave|logic-timing)([^\r\n]*)\r?\n([\s\S]*?):::",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static DigitalTimingModel ParseTiming(string blockText, string defaultTitle = "Digital Logic Timing Diagram")
    {
        var model = new DigitalTimingModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText)) return model;

        var fence = TimingFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Title = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Title = header;

            text = fence.Groups[2].Value;
        }

        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            if (string.IsNullOrWhiteSpace(l) || l.StartsWith("#") || l.StartsWith("//")) continue;

            var kv = l.Split(new[] { ':', '=' }, 2);
            if (kv.Length == 2)
            {
                string name = kv[0].Trim();
                string wave = kv[1].Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(wave))
                {
                    model.Signals.Add(new TimingSignal { Name = name, Waveform = wave });
                }
            }
        }

        if (model.Signals.Count == 0)
        {
            model.Signals.Add(new TimingSignal { Name = "CLK", Waveform = "P...P...P...P" });
            model.Signals.Add(new TimingSignal { Name = "CS_N", Waveform = "1...0...0...1" });
            model.Signals.Add(new TimingSignal { Name = "MOSI", Waveform = "x...0...1...x" });
            model.Signals.Add(new TimingSignal { Name = "MISO", Waveform = "z...z...D...z" });
        }

        return model;
    }

    public static string RenderTimingSvg(DigitalTimingModel model)
    {
        double width = 500;
        int rowCount = Math.Max(1, model.Signals.Count);
        double rowHeight = 36;
        double startY = 70;
        double height = Math.Max(280, startY + rowCount * rowHeight + 40);

        double labelW = 75;
        double waveStartX = labelW + 10;
        double waveEndX = width - 25;
        double waveW = waveEndX - waveStartX;

        int totalTicks = 12;
        foreach (var sig in model.Signals)
        {
            totalTicks = Math.Max(totalTicks, sig.Waveform.Length);
        }
        double tickW = waveW / totalTicks;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-timing-svg\">");
        sb.AppendLine("""
            <style>
              .tm-bg { fill: #0b0f19; stroke: #1e293b; stroke-width: 1.5; }
              .tm-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .tm-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .tm-grid { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .tm-sig-name { font-family: monospace; font-size: 10px; font-weight: 700; fill: #f8fafc; }
              .tm-wave { fill: none; stroke: #38bdf8; stroke-width: 1.8; }
              .tm-bus { fill: #0284c7; fill-opacity: 0.2; stroke: #38bdf8; stroke-width: 1.5; }
              .tm-hiz { fill: #475569; fill-opacity: 0.3; stroke: #64748b; stroke-width: 1.5; stroke-dasharray: 3 3; }
              .tm-bus-text { font-family: monospace; font-size: 8.5px; font-weight: 700; fill: #fbbf24; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"tm-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"tm-title\">⏱ {System.Net.WebUtility.HtmlEncode(model.Title)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"tm-meta\">Synchronous Digital Logic Timing • {rowCount} Channels • {totalTicks} Time Units</text>");

        // Vertical Timing Grid Ticks
        for (int t = 0; t <= totalTicks; t++)
        {
            double gx = waveStartX + t * tickW;
            sb.AppendLine($"  <line x1=\"{gx:F1}\" y1=\"{startY - 10}\" x2=\"{gx:F1}\" y2=\"{startY + rowCount * rowHeight}\" class=\"tm-grid\" />");
            if (t % 2 == 0)
                sb.AppendLine($"  <text x=\"{gx:F1}\" y=\"{startY - 14}\" font-family=\"monospace\" font-size=\"8\" fill=\"#64748b\" text-anchor=\"middle\">T{t}</text>");
        }

        // Render each signal track
        for (int s = 0; s < model.Signals.Count; s++)
        {
            var sig = model.Signals[s];
            double trackY = startY + s * rowHeight;
            double highY = trackY + 6;
            double lowY = trackY + 24;
            double midY = trackY + 15;

            // Signal Name in left gutter
            sb.AppendLine($"  <text x=\"{labelW}\" y=\"{trackY + 18}\" class=\"tm-sig-name\" text-anchor=\"end\">{System.Net.WebUtility.HtmlEncode(sig.Name)}</text>");

            string wave = sig.Waveform;
            var path = new StringBuilder();
            double curX = waveStartX;
            double curY = lowY;

            for (int i = 0; i < totalTicks; i++)
            {
                char c = i < wave.Length ? wave[i] : '.';
                double nextX = waveStartX + (i + 1) * tickW;

                if (c == 'P' || c == 'p' || c == 'C' || c == 'c')
                {
                    // Clock pulse: Low -> High -> Low in one tick
                    double halfX = curX + tickW / 2.0;
                    path.Append($"M {curX:F1} {lowY} L {curX:F1} {highY} L {halfX:F1} {highY} L {halfX:F1} {lowY} L {nextX:F1} {lowY} ");
                }
                else if (c == '1' || c == 'H' || c == 'h')
                {
                    if (path.Length == 0) path.Append($"M {curX:F1} {highY} ");
                    else path.Append($"L {curX:F1} {highY} ");
                    path.Append($"L {nextX:F1} {highY} ");
                }
                else if (c == '0' || c == 'L' || c == 'l')
                {
                    if (path.Length == 0) path.Append($"M {curX:F1} {lowY} ");
                    else path.Append($"L {curX:F1} {lowY} ");
                    path.Append($"L {nextX:F1} {lowY} ");
                }
                else if (c == 'Z' || c == 'z')
                {
                    // Tri-state High-Z line in middle
                    sb.AppendLine($"  <line x1=\"{curX:F1}\" y1=\"{midY}\" x2=\"{nextX:F1}\" y2=\"{midY}\" class=\"tm-hiz\" />");
                }
                else if (c == 'D' || c == 'd' || c == 'X' || c == 'x')
                {
                    // Bus Data Polygon (Hex packet)
                    double slant = 3.0;
                    string poly = $"{curX:F1},{midY} {curX + slant:F1},{highY} {nextX - slant:F1},{highY} {nextX:F1},{midY} {nextX - slant:F1},{lowY} {curX + slant:F1},{lowY} Z";
                    sb.AppendLine($"  <polygon points=\"{poly}\" class=\"tm-bus\" />");
                    sb.AppendLine($"  <text x=\"{(curX + nextX) / 2:F1}\" y=\"{midY + 3}\" class=\"tm-bus-text\" text-anchor=\"middle\">DATA</text>");
                }
                else
                {
                    // Continue last state
                    if (path.Length == 0) path.Append($"M {curX:F1} {lowY} ");
                    path.Append($"L {nextX:F1} {lowY} ");
                }

                curX = nextX;
            }

            if (path.Length > 0)
                sb.AppendLine($"  <path d=\"{path}\" class=\"tm-wave\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
