using System;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Services;

/// <summary>
/// Service for applying OpenType font features, ligatures, kerning, and micro-tracking to Word OpenXML runs.
/// </summary>
public static class DocxTypographyService
{
    private static readonly HashSet<string> LigatureFontFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cascadia Code",
        "Cascadia Mono",
        "Fira Code",
        "JetBrains Mono",
        "Victor Mono",
        "Iosevka",
        "Hasklig",
        "Monaspace Neon",
        "Monaspace Argon",
        "Monaspace Xenon",
        "Monaspace Radon",
        "Monaspace Krypton"
    };

    /// <summary>
    /// Decorates an OpenXML RunProperties element with advanced typography options.
    /// </summary>
    public static void ApplyTypographyProperties(
        RunProperties rPr,
        string? fontFamily = null,
        bool enableLigatures = true,
        int kerningHalfPoints = 24,
        int spacingTwips = 0)
    {
        if (rPr == null) return;

        // 1. Kerning: <w:kern w:val="24"/> (points * 2)
        if (kerningHalfPoints > 0)
        {
            rPr.Kern = new Kern { Val = (uint)kerningHalfPoints };
        }

        // 2. Micro-tracking / Character Spacing: <w:spacing w:val="twips"/>
        if (spacingTwips != 0)
        {
            rPr.Spacing = new Spacing { Val = spacingTwips };
        }

        // 3. OpenType Ligatures for programming fonts
        if (enableLigatures && !string.IsNullOrEmpty(fontFamily) && LigatureFontFamilies.Contains(fontFamily))
        {
            var ligatures = new OpenXmlUnknownElement("w14", "ligatures", "http://schemas.microsoft.com/office/word/2010/wordml");
            rPr.AppendChild(ligatures);
        }
    }

    /// <summary>
    /// Checks if a given font family is known to feature OpenType programming ligatures.
    /// </summary>
    public static bool SupportsProgrammingLigatures(string fontFamily)
    {
        return !string.IsNullOrEmpty(fontFamily) && LigatureFontFamilies.Contains(fontFamily);
    }
}
