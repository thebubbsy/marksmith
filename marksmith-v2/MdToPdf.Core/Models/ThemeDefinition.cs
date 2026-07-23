namespace MdToPdf.Models;

// Mirrors the THEMES dict in md_to_pdf_tui.py so PDFs produced by either app look identical.
public sealed record ThemeDefinition(
    string Name,
    string Background,
    string Text,
    string Heading,
    string Code,
    string Border,
    string Primary,
    string Secondary,
    string Line)
{
    public ThemeDefinition ApplyLightInfluence()
    {
        return new ThemeDefinition(
            Name,
            Background,
            Text: IsLight(Text) ? "#333333" : Text,
            Heading: IsLight(Heading) ? Darken(Heading) : Heading,
            Code: "#f8f8f8", // Light grey code block is safer for a light page
            Border: "#e0e0e0", // Neutral borders prevent neon clashing
            Primary: IsLight(Primary) ? Darken(Primary) : Primary,
            Secondary: "#f0f0f0", // Neutral panels
            Line: IsLight(Line) ? Darken(Line) : Line
        );
    }

    private static string Darken(string hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) return hex;
        r = (int)(r * 0.6);
        g = (int)(g * 0.6);
        b = (int)(b * 0.6);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    public static bool IsLight(string cssColor)
    {
        if (!TryParseHex(cssColor, out var r, out var g, out var b)) return true;
        return (0.299 * r + 0.587 * g + 0.114 * b) > 128;
    }

    private static bool TryParseHex(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        try
        {
            r = System.Convert.ToInt32(s.Substring(0, 2), 16);
            g = System.Convert.ToInt32(s.Substring(2, 2), 16);
            b = System.Convert.ToInt32(s.Substring(4, 2), 16);
            return true;
        }
        catch { return false; }
    }
}
