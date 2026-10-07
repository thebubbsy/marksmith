using MarkSmith.Models;

namespace MarkSmith.Services.Email;

/// <summary>The colours an email is drawn with. Always a light page: mail clients that force dark
/// mode invert a light message cleanly, but a dark-themed message comes out as mud (or, in classic
/// Outlook, as dark text on the reader's white window). The document theme still carries through
/// its accents (headings, links, table header) wherever they read well on white.</summary>
public sealed record EmailPalette(
    string Page, string Text, string Heading, string Accent, string Muted,
    string Border, string CodeBackground, string CodeText, string HeaderFill, string QuoteBar)
{
    public static EmailPalette Clean { get; } = new(
        Page: "#ffffff", Text: "#1f2328", Heading: "#1f2328", Accent: "#0b5cad", Muted: "#59636e",
        Border: "#d0d7de", CodeBackground: "#f6f8fa", CodeText: "#1f2328", HeaderFill: "#f3f5f7",
        QuoteBar: "#d0d7de");

    /// <summary>Takes the theme's colours that pass contrast on a white page and falls back to the
    /// clean palette for the rest.</summary>
    public static EmailPalette From(ThemeDefinition? theme)
    {
        if (theme is null) return Clean;
        var c = Clean;
        string Readable(string candidate, string fallback, double min) =>
            IsHex(candidate) && ContrastGuard.GetContrastRatio(Norm(candidate), c.Page) >= min ? Norm(candidate) : fallback;

        var light = !theme.IsDarkPage;
        return c with
        {
            Text = Readable(theme.Text, c.Text, 7.0),
            Heading = Readable(theme.Heading, c.Heading, 4.5),
            Accent = Readable(theme.Primary, c.Accent, 4.5),
            // Panel colours only make sense when they came from a light page.
            Border = light && IsHex(theme.Border) && ContrastGuard.GetContrastRatio(Norm(theme.Border), c.Page) is >= 1.2 and <= 4.0
                ? Norm(theme.Border) : c.Border,
            CodeBackground = light && IsHex(theme.Code) && ThemeDefinition.IsLight(theme.Code) ? Norm(theme.Code) : c.CodeBackground,
            HeaderFill = light && IsHex(theme.Secondary) && ThemeDefinition.IsLight(theme.Secondary) ? Norm(theme.Secondary) : c.HeaderFill,
        };
    }

    /// <summary>The theme Mermaid diagrams are drawn with for an email: a white canvas (the harvest
    /// paints its background with the node fill) and dark text in the email's own colours, so a
    /// Dracula document doesn't drop a dark slab, or a grey box, into a white message.</summary>
    public ThemeDefinition DiagramTheme() =>
        new("Email", Background: Page, Text: Text, Heading: Heading, Code: CodeBackground,
            Border: Border, Primary: Text, Secondary: CodeBackground, Line: Muted);

    private static bool IsHex(string? s)
    {
        var t = (s ?? "").Trim().TrimStart('#');
        return (t.Length == 6 || t.Length == 3) && t.All(Uri.IsHexDigit);
    }

    private static string Norm(string s)
    {
        var t = s.Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        return "#" + t.ToLowerInvariant();
    }
}
