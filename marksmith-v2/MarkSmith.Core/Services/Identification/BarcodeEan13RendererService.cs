using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Identification;

public class BarcodeModel
{
    public string Title { get; set; } = "EAN-13 Barcode";
    public string Code { get; set; } = "9780132350884";
    public string BitPattern { get; set; } = "";
}

/// <summary>
/// Service for encoding 12-digit and 13-digit product codes into EAN-13 binary bar sequences and rendering SVG barcodes.
/// </summary>
public static class BarcodeEan13RendererService
{
    private static readonly Regex BarcodeFenceRegex = new(
        @":::barcode([^\r\n]*)\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    // EAN-13 7-bit patterns: L-code and R-code
    private static readonly string[] LCode =
    {
        "0001101", "0011001", "0010011", "0111101", "0100011",
        "0110001", "0101111", "0111011", "0110111", "0001011"
    };

    private static readonly string[] RCode =
    {
        "1110010", "1100110", "1101100", "1000010", "1011100",
        "1001110", "1010000", "1000100", "1001000", "1110100"
    };

    public static BarcodeModel ParseBarcode(string blockText, string defaultCode = "9780132350884")
    {
        var model = new BarcodeModel { Title = "Barcode EAN-13", Code = defaultCode };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            EncodeEan13(model);
            return model;
        }

        var fence = BarcodeFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Value.Trim();
            var tm = Regex.Match(header, @"""([^""]+)""");
            if (tm.Success) model.Code = tm.Groups[1].Value;
            else if (!string.IsNullOrEmpty(header)) model.Code = header;

            string body = fence.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(body)) model.Code = body;
        }

        var digitsOnly = Regex.Replace(model.Code, @"\D", "");
        if (digitsOnly.Length >= 12)
        {
            model.Code = digitsOnly.Substring(0, Math.Min(13, digitsOnly.Length));
        }

        EncodeEan13(model);
        return model;
    }

    private static void EncodeEan13(BarcodeModel model)
    {
        string padded = model.Code.PadRight(13, '0').Substring(0, 13);
        var sb = new StringBuilder();

        // Start guard (101)
        sb.Append("101");

        // 6 Left Digits (L-Code)
        for (int i = 1; i <= 6; i++)
        {
            int d = padded[i] - '0';
            sb.Append(LCode[d]);
        }

        // Center guard (01010)
        sb.Append("01010");

        // 6 Right Digits (R-Code)
        for (int i = 7; i <= 12; i++)
        {
            int d = padded[i] - '0';
            sb.Append(RCode[d]);
        }

        // End guard (101)
        sb.Append("101");

        model.BitPattern = sb.ToString();
    }

    public static string RenderBarcodeSvg(BarcodeModel model)
    {
        double moduleW = 2.4;
        double barHeight = 80;
        double width = model.BitPattern.Length * moduleW + 80;
        double height = barHeight + 90;
        double ox = 40;
        double oy = 45;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-barcode-svg\">");
        sb.AppendLine("""
            <style>
              .bc-bg { fill: #ffffff; stroke: #cbd5e1; stroke-width: 1.5; }
              .bc-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #0f172a; }
              .bc-bar { fill: #0f172a; }
              .bc-num { font-family: monospace; font-size: 13px; font-weight: 700; fill: #0f172a; text-anchor: middle; letter-spacing: 0.1em; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"bc-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"bc-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Render Bars
        for (int i = 0; i < model.BitPattern.Length; i++)
        {
            if (model.BitPattern[i] == '1')
            {
                double bx = ox + i * moduleW;
                bool isGuard = i < 3 || (i >= 45 && i < 50) || i >= model.BitPattern.Length - 3;
                double h = isGuard ? barHeight + 8 : barHeight;
                sb.AppendLine($"  <rect x=\"{bx}\" y=\"{oy}\" width=\"{moduleW}\" height=\"{h}\" class=\"bc-bar\" />");
            }
        }

        // Code digits label
        sb.AppendLine($"  <text x=\"{ox + (model.BitPattern.Length * moduleW) / 2}\" y=\"{oy + barHeight + 25}\" class=\"bc-num\">{System.Net.WebUtility.HtmlEncode(model.Code)}</text>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
