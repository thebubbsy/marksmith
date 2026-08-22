using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio;

public class AudioToneModel
{
    public string Name { get; set; } = "Sine Wave";
    public double FrequencyHz { get; set; } = 440;
    public string WaveformType { get; set; } = "sine";
    public double DurationSeconds { get; set; } = 1.5;
}

/// <summary>
/// Service for parsing acoustic frequency tone definitions and rendering mathematical SVG waveform oscillograms.
/// </summary>
public static class MarkdownAudioToneService
{
    private static readonly Regex AudioFenceRegex = new(
        @":::audio-tone(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex FreqRegex = new(
        @"freq(?:uency)?\s*=\s*(-?\d+(?:\.\d+)?)\s*(?:Hz)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TypeRegex = new(
        @"type\s*=\s*(sine|square|triangle|sawtooth)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DurationRegex = new(
        @"duration\s*=\s*(-?\d+(?:\.\d+)?)\s*s?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses an audio-tone block into a structured model.
    /// </summary>
    public static AudioToneModel ParseTone(string blockText, string defaultName = "Tone")
    {
        var model = new AudioToneModel { Name = defaultName };
        if (string.IsNullOrWhiteSpace(blockText))
            return model;

        var fence = AudioFenceRegex.Match(blockText);
        string text = fence.Success ? (fence.Groups[2].Value + " " + fence.Groups[3].Value) : blockText;
        if (fence.Success && fence.Groups[1].Success)
        {
            model.Name = fence.Groups[1].Value.Trim();
        }

        var fm = FreqRegex.Match(text);
        if (fm.Success && double.TryParse(fm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double freq))
        {
            model.FrequencyHz = Math.Clamp(freq, 20.0, 20000.0);
        }

        var tm = TypeRegex.Match(text);
        if (tm.Success)
        {
            model.WaveformType = tm.Groups[1].Value.ToLowerInvariant();
        }

        var dm = DurationRegex.Match(text);
        if (dm.Success && double.TryParse(dm.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double dur))
        {
            model.DurationSeconds = Math.Clamp(dur, 0.1, 60.0);
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG oscillogram plot of the specified audio waveform.
    /// </summary>
    public static string RenderToneSvg(AudioToneModel model)
    {
        double width = 460;
        double height = 200;
        double plotY = 110;
        double amp = 45;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-audio-tone-svg\">");
        sb.AppendLine("""
            <style>
              .at-bg { fill: #0a0e17; stroke: #1e293b; stroke-width: 1.5; }
              .at-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .at-meta { font-family: monospace; font-size: 10px; fill: #38bdf8; }
              .at-axis { stroke: #1e293b; stroke-width: 1; stroke-dasharray: 2 2; }
              .at-wave { stroke: #38bdf8; stroke-width: 2.2; fill: none; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"at-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"at-title\">{System.Net.WebUtility.HtmlEncode(model.Name)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"at-meta\">{model.FrequencyHz} Hz  •  {model.WaveformType.ToUpperInvariant()}  •  {model.DurationSeconds}s</text>");

        // Axis
        sb.AppendLine($"  <line x1=\"20\" y1=\"{plotY}\" x2=\"{width - 20}\" y2=\"{plotY}\" class=\"at-axis\" />");

        // Compute waveform points (3 cycles)
        int sampleCount = 100;
        double step = (width - 40) / sampleCount;
        var pathSb = new StringBuilder();

        for (int i = 0; i <= sampleCount; i++)
        {
            double x = 20 + i * step;
            double t = (i / (double)sampleCount) * 3 * 2 * Math.PI;
            double yVal = model.WaveformType switch
            {
                "square" => Math.Sin(t) >= 0 ? 1.0 : -1.0,
                "triangle" => (2.0 / Math.PI) * Math.Asin(Math.Sin(t)),
                "sawtooth" => 2.0 * ((t / (2 * Math.PI)) - Math.Floor(0.5 + (t / (2 * Math.PI)))),
                _ => Math.Sin(t)
            };

            double y = plotY - yVal * amp;
            if (i == 0) pathSb.Append($"M {x:F1} {y:F1}");
            else pathSb.Append($" L {x:F1} {y:F1}");
        }

        sb.AppendLine($"  <path d=\"{pathSb}\" class=\"at-wave\" />");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
