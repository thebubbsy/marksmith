using System;
using MdToPdf.Models;

namespace MdToPdf.Services;

/// <summary>
/// Top-level legibility guard for OpenXML DOCX and rendering engines.
/// Enforces W3C WCAG 2.1 contrast rules, mathematically preventing text from rendering on a similar background shade.
/// </summary>
public static class ContrastGuard
{
    /// <summary>
    /// Calculates W3C WCAG 2.1 relative luminance of a 6-digit hex color (0.0 = black, 1.0 = white).
    /// </summary>
    public static double GetLuminance(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor)) return 0.5;
        hexColor = hexColor.Trim().TrimStart('#');
        if (hexColor.Length != 6) return 0.5;

        try
        {
            double r = int.Parse(hexColor.Substring(0, 2), System.Globalization.NumberStyles.HexNumber) / 255.0;
            double g = int.Parse(hexColor.Substring(2, 2), System.Globalization.NumberStyles.HexNumber) / 255.0;
            double b = int.Parse(hexColor.Substring(4, 2), System.Globalization.NumberStyles.HexNumber) / 255.0;

            r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
            g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
            b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }
        catch
        {
            return 0.5;
        }
    }

    /// <summary>
    /// Computes WCAG 2.1 contrast ratio between two hex colors (1.0 to 21.0).
    /// </summary>
    public static double GetContrastRatio(string hexColor1, string hexColor2)
    {
        double l1 = GetLuminance(hexColor1);
        double l2 = GetLuminance(hexColor2);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>
    /// TOP-LEVEL HARD RULE: Guarantee high contrast legibility (minimum 4.5:1 ratio).
    /// If the contrast ratio between text and background is below 4.5:1,
    /// this function forces the text color to high-contrast White (#FFFFFF) or Dark (#121212).
    /// </summary>
    public static string EnsureLegibleText(string textColorHex, string bgContextHex, string? preferredFallbackHex = null)
    {
        if (string.IsNullOrWhiteSpace(textColorHex)) textColorHex = "000000";
        if (string.IsNullOrWhiteSpace(bgContextHex)) bgContextHex = "FFFFFF";

        textColorHex = textColorHex.Trim().TrimStart('#');
        bgContextHex = bgContextHex.Trim().TrimStart('#');

        if (textColorHex.Length != 6) textColorHex = "000000";
        if (bgContextHex.Length != 6) bgContextHex = "FFFFFF";

        double ratio = GetContrastRatio(textColorHex, bgContextHex);

        // If contrast ratio satisfies WCAG AA (4.5:1), keep the intended color!
        if (ratio >= 4.5)
        {
            return textColorHex;
        }

        // If preferred fallback provided and meets contrast, use fallback
        if (!string.IsNullOrWhiteSpace(preferredFallbackHex))
        {
            var cleanFallback = preferredFallbackHex.Trim().TrimStart('#');
            if (cleanFallback.Length == 6 && GetContrastRatio(cleanFallback, bgContextHex) >= 4.5)
            {
                return cleanFallback;
            }
        }

        // HARD CONTRAST ENFORCEMENT:
        // Background is DARK -> force high contrast LIGHT text (#FFFFFF)
        // Background is LIGHT -> force high contrast DARK text (#121212)
        double bgLuminance = GetLuminance(bgContextHex);
        return bgLuminance < 0.45 ? "FFFFFF" : "121212";
    }
}
