using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Telephony;

public class MorseCodeModel
{
    public string PlainText { get; set; } = "SOS";
    public string MorseSequence { get; set; } = "... --- ...";
    public int Wpm { get; set; } = 20;
}

/// <summary>
/// Service for translating text to standard ITU Morse code and rendering interactive SVG optical/audio telegraph keys.
/// </summary>
public static class MorseCodeTelegraphService
{
    private static readonly Regex MorseFenceRegex = new(
        @":::morse(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex WpmRegex = new(
        @"\[?(?:wpm|speed):\s*(\d+)\]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<char, string> MorseDictionary = new()
    {
        { 'A', ".-" }, { 'B', "-..." }, { 'C', "-.-." }, { 'D', "-.." }, { 'E', "." },
        { 'F', "..-." }, { 'G', "--." }, { 'H', "...." }, { 'I', ".." }, { 'J', ".---" },
        { 'K', "-.-" }, { 'L', ".-.." }, { 'M', "--" }, { 'N', "-." }, { 'O', "---" },
        { 'P', ".--." }, { 'Q', "--.-" }, { 'R', ".-." }, { 'S', "..." }, { 'T', "-" },
        { 'U', "..-" }, { 'V', "...-" }, { 'W', ".--" }, { 'X', "-..-" }, { 'Y', "-.--" },
        { 'Z', "--.." }, { '0', "-----" }, { '1', ".----" }, { '2', "..---" }, { '3', "...--" },
        { '4', "....-" }, { '5', "....." }, { '6', "-...." }, { '7', "--..." }, { '8', "---.." },
        { '9', "----." }, { ' ', "/" }
    };

    public static MorseCodeModel ParseMorse(string blockText, string defaultText = "SOS")
    {
        var model = new MorseCodeModel { PlainText = defaultText };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            model.MorseSequence = EncodeToMorse(model.PlainText);
            return model;
        }

        var fence = MorseFenceRegex.Match(blockText);
        string rawText = fence.Success && fence.Groups[1].Success ? fence.Groups[1].Value : defaultText;
        string config = fence.Success ? (fence.Groups[2].Value + " " + fence.Groups[3].Value) : blockText;

        var wm = WpmRegex.Match(config);
        if (wm.Success && int.TryParse(wm.Groups[1].Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int wpm))
        {
            model.Wpm = Math.Max(5, Math.Min(40, wpm));
        }

        model.PlainText = rawText.Trim();
        model.MorseSequence = EncodeToMorse(model.PlainText);
        return model;
    }

    public static string EncodeToMorse(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.ToUpperInvariant())
        {
            if (MorseDictionary.TryGetValue(c, out string? morse))
            {
                sb.Append(morse).Append(' ');
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string RenderMorseSvg(MorseCodeModel model)
    {
        double width = 480;
        double height = 180;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-morse-svg\">");
        sb.AppendLine("""
            <style>
              .mo-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .mo-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .mo-meta { font-family: monospace; font-size: 10px; fill: #94a3b8; }
              .mo-plain { font-family: monospace; font-size: 14px; font-weight: 700; fill: #38bdf8; letter-spacing: 0.15em; }
              .mo-seq { font-family: monospace; font-size: 16px; font-weight: 700; fill: #f59e0b; letter-spacing: 0.2em; }
              .mo-led { fill: #ef4444; stroke: #f87171; stroke-width: 2; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"mo-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"mo-title\">Morse Code Telegraph</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"42\" class=\"mo-meta\">Speed: {model.Wpm} WPM • ITU Standard</text>");

        // Signal LED lamp
        sb.AppendLine("  <circle cx=\"440\" cy=\"30\" r=\"8\" class=\"mo-led\" />");

        // Text & Sequence
        sb.AppendLine($"  <text x=\"20\" y=\"85\" class=\"mo-plain\">{System.Net.WebUtility.HtmlEncode(model.PlainText)}</text>");
        sb.AppendLine($"  <text x=\"20\" y=\"125\" class=\"mo-seq\">{System.Net.WebUtility.HtmlEncode(model.MorseSequence)}</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
