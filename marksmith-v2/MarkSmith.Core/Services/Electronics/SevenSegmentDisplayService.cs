using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Electronics;

public class SevenSegmentDigit
{
    public char Char { get; set; }
    public byte Segments { get; set; } // bits: a(0), b(1), c(2), d(3), e(4), f(5), g(6), dp(7)
}

public class SevenSegmentModel
{
    public string Title { get; set; } = "7-Segment Display";
    public string Text { get; set; } = "12:34";
    public string LedColor { get; set; } = "#22c55e"; // green
    public List<SevenSegmentDigit> Digits { get; } = new();
}

/// <summary>
/// Service for decoding alphanumeric characters to standard 7-segment LED bitmasks and rendering SVG displays.
/// </summary>
public static class SevenSegmentDisplayService
{
    private static readonly Regex SevenSegFenceRegex = new(
        @":::7seg([^\r\n]*)\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex ColorRegex = new(
        @"color\s*=\s*""?([A-Za-z0-9#-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Standard 7-segment bitmasks: a(1), b(2), c(4), d(8), e(16), f(32), g(64)
    private static readonly Dictionary<char, byte> SegmentMap = new()
    {
        { '0', 0b00111111 }, { '1', 0b00000110 }, { '2', 0b01011011 }, { '3', 0b01001111 },
        { '4', 0b01100110 }, { '5', 0b01101101 }, { '6', 0b01111101 }, { '7', 0b00000111 },
        { '8', 0b01111111 }, { '9', 0b01101111 }, { '-', 0b01000000 }, { 'H', 0b01110110 },
        { 'E', 0b01111001 }, { 'L', 0b00111000 }, { 'O', 0b00111111 }, { 'P', 0b01110011 },
        { ' ', 0b00000000 }, { ':', 0b10000000 }
    };

    public static SevenSegmentModel ParseSevenSegment(string blockText, string defaultText = "12:34")
    {
        var model = new SevenSegmentModel { Title = "7-Segment Display", Text = defaultText };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            PopulateDigits(model);
            return model;
        }

        var fence = SevenSegFenceRegex.Match(blockText);
        string raw = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Text = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Text = header;

            var cm = ColorRegex.Match(header);
            if (cm.Success)
            {
                string col = cm.Groups[1].Value.ToLowerInvariant();
                model.LedColor = col switch
                {
                    "red" => "#ef4444",
                    "green" => "#22c55e",
                    "blue" => "#3b82f6",
                    "yellow" => "#eab308",
                    _ => col.StartsWith("#") ? col : "#22c55e"
                };
            }

            string body = fence.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(body)) model.Text = body;
        }

        PopulateDigits(model);
        return model;
    }

    private static void PopulateDigits(SevenSegmentModel model)
    {
        foreach (char c in model.Text.ToUpperInvariant())
        {
            byte mask = SegmentMap.TryGetValue(c, out byte m) ? m : (byte)0;
            model.Digits.Add(new SevenSegmentDigit { Char = c, Segments = mask });
        }
    }

    public static string RenderSevenSegmentSvg(SevenSegmentModel model)
    {
        double digitW = 38;
        double width = Math.Max(260, model.Digits.Count * digitW + 60);
        double height = 140;
        double ox = 30;
        double oy = 48;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-7seg-svg\">");
        sb.AppendLine("""
            <style>
              .ss-bg { fill: #090d16; stroke: #1e293b; stroke-width: 1.5; }
              .ss-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ss-off { fill: #1e293b; fill-opacity: 0.3; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ss-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ss-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        for (int i = 0; i < model.Digits.Count; i++)
        {
            var digit = model.Digits[i];
            double dx = ox + i * digitW;

            if (digit.Char == ':')
            {
                // Colon dots
                sb.AppendLine($"  <circle cx=\"{dx + 12}\" cy=\"{oy + 18}\" r=\"3\" fill=\"{model.LedColor}\" />");
                sb.AppendLine($"  <circle cx=\"{dx + 12}\" cy=\"{oy + 42}\" r=\"3\" fill=\"{model.LedColor}\" />");
                continue;
            }

            // Segments a, b, c, d, e, f, g
            // a (top horizontal)
            bool a = (digit.Segments & 1) != 0;
            sb.AppendLine($"  <rect x=\"{dx + 4}\" y=\"{oy}\" width=\"20\" height=\"5\" rx=\"2\" fill=\"{(a ? model.LedColor : "")}\" class=\"{(a ? "" : "ss-off")}\" />");

            // b (top-right vertical)
            bool b = (digit.Segments & 2) != 0;
            sb.AppendLine($"  <rect x=\"{dx + 23}\" y=\"{oy + 4}\" width=\"5\" height=\"24\" rx=\"2\" fill=\"{(b ? model.LedColor : "")}\" class=\"{(b ? "" : "ss-off")}\" />");

            // c (bottom-right vertical)
            bool c = (digit.Segments & 4) != 0;
            sb.AppendLine($"  <rect x=\"{dx + 23}\" y=\"{oy + 30}\" width=\"5\" height=\"24\" rx=\"2\" fill=\"{(c ? model.LedColor : "")}\" class=\"{(c ? "" : "ss-off")}\" />");

            // d (bottom horizontal)
            bool d = (digit.Segments & 8) != 0;
            sb.AppendLine($"  <rect x=\"{dx + 4}\" y=\"{oy + 53}\" width=\"20\" height=\"5\" rx=\"2\" fill=\"{(d ? model.LedColor : "")}\" class=\"{(d ? "" : "ss-off")}\" />");

            // e (bottom-left vertical)
            bool e = (digit.Segments & 16) != 0;
            sb.AppendLine($"  <rect x=\"{dx}\" y=\"{oy + 30}\" width=\"5\" height=\"24\" rx=\"2\" fill=\"{(e ? model.LedColor : "")}\" class=\"{(e ? "" : "ss-off")}\" />");

            // f (top-left vertical)
            bool f = (digit.Segments & 32) != 0;
            sb.AppendLine($"  <rect x=\"{dx}\" y=\"{oy + 4}\" width=\"5\" height=\"24\" rx=\"2\" fill=\"{(f ? model.LedColor : "")}\" class=\"{(f ? "" : "ss-off")}\" />");

            // g (middle horizontal)
            bool g = (digit.Segments & 64) != 0;
            sb.AppendLine($"  <rect x=\"{dx + 4}\" y=\"{oy + 26}\" width=\"20\" height=\"5\" rx=\"2\" fill=\"{(g ? model.LedColor : "")}\" class=\"{(g ? "" : "ss-off")}\" />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
