namespace MarkSmith.Services;

/// <summary>
/// Picks text colours that stay readable on (or as) a fixed accent colour, for UI chips whose
/// colour comes from data (an OCR block kind, a tag) rather than from the theme.
/// </summary>
public static class ReadableInk
{
    /// <summary>
    /// True when black text reads better than white on this background. The 0.179 luminance
    /// crossover is where the two WCAG contrast ratios are equal, so the result is always the
    /// higher-contrast of the two.
    /// </summary>
    public static bool PrefersDarkInk(byte r, byte g, byte b) =>
        ThemeContrastAuditorService.RelativeLuminance(r, g, b) > 0.179;

    /// <summary>
    /// The accent itself, toned so it can be used as text on a theme surface: darkened on a
    /// light surface (amber or teal on white is about 2:1) and lightened on a dark one (indigo
    /// or slate on near-black is under 4:1). Both moves keep the hue, so the colour still
    /// matches its badge and outline.
    /// </summary>
    public static (byte R, byte G, byte B) AsTextOn(byte r, byte g, byte b, bool lightSurface)
    {
        if (lightSurface)
        {
            const double keep = 0.6;
            return ((byte)(r * keep), (byte)(g * keep), (byte)(b * keep));
        }

        const double lift = 0.35;
        return (Lift(r), Lift(g), Lift(b));

        static byte Lift(byte c) => (byte)(c + (255 - c) * lift);
    }
}
