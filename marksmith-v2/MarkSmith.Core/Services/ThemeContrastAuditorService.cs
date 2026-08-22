using System;
using System.Globalization;

namespace MarkSmith.Services;

public record ContrastAuditResult(
    string ForegroundHex,
    string BackgroundHex,
    double ContrastRatio,
    bool PassesAaNormalText,
    bool PassesAaLargeText,
    bool PassesAaaNormalText,
    string ComplianceLevel,
    string? SuggestedForegroundHex = null);

/// <summary>
/// Service that audits theme color palettes against WCAG 2.1 AA/AAA contrast ratios and synthesizes accessible color corrections.
/// </summary>
public static class ThemeContrastAuditorService
{
    /// <summary>
    /// Audits the contrast ratio between a foreground and background color.
    /// </summary>
    public static ContrastAuditResult Audit(string foregroundHex, string backgroundHex)
    {
        var (r1, g1, b1) = ParseHex(foregroundHex);
        var (r2, g2, b2) = ParseHex(backgroundHex);

        double l1 = RelativeLuminance(r1, g1, b1);
        double l2 = RelativeLuminance(r2, g2, b2);

        double ratio = (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        ratio = Math.Round(ratio, 2);

        bool aaNormal = ratio >= 4.5;
        bool aaLarge = ratio >= 3.0;
        bool aaaNormal = ratio >= 7.0;

        string compliance = aaaNormal ? "AAA (Optimal)" : aaNormal ? "AA (Compliant)" : aaLarge ? "AA Large Only" : "Fail (Inaccessible)";

        string? suggested = null;
        if (!aaNormal)
        {
            suggested = SynthesizeAccessibleColor(r1, g1, b1, l2);
        }

        return new ContrastAuditResult(foregroundHex, backgroundHex, ratio, aaNormal, aaLarge, aaaNormal, compliance, suggested);
    }

    /// <summary>
    /// Calculates WCAG relative luminance for an sRGB color component.
    /// </summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        double rs = r / 255.0;
        double gs = g / 255.0;
        double bs = b / 255.0;

        double rLin = (rs <= 0.03928) ? rs / 12.92 : Math.Pow((rs + 0.055) / 1.055, 2.4);
        double gLin = (gs <= 0.03928) ? gs / 12.92 : Math.Pow((gs + 0.055) / 1.055, 2.4);
        double bLin = (bs <= 0.03928) ? bs / 12.92 : Math.Pow((bs + 0.055) / 1.055, 2.4);

        return 0.2126 * rLin + 0.7152 * gLin + 0.0722 * bLin;
    }

    private static (byte r, byte g, byte b) ParseHex(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";

        if (hex.Length >= 6 &&
            byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return (r, g, b);
        }

        return (0, 0, 0);
    }

    private static string SynthesizeAccessibleColor(byte r, byte g, byte b, double bgLum)
    {
        // If background is dark (lum < 0.5), lighten the foreground; otherwise darken it
        if (bgLum < 0.5)
        {
            return $"#{Math.Min(255, r + 70):X2}{Math.Min(255, g + 70):X2}{Math.Min(255, b + 70):X2}";
        }
        else
        {
            return $"#{Math.Max(0, r - 70):X2}{Math.Max(0, g - 70):X2}{Math.Max(0, b - 70):X2}";
        }
    }
}
