using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;

namespace MdToPdf.Services;

/// <summary>
/// Extracts a corporate house-style from a Word template (.dotx) and turns it into a Marksmith
/// ThemeDefinition — without calling any external API. The OOXML is parsed locally (fonts, heading
/// colors, theme1.xml palette); a compact summary is formatted into an unambiguous prompt that
/// instructs the user's OWN web AI (via the browser extension command channel) to respond with a
/// strict JSON ThemeDefinition. The AI does the creative mapping; Marksmith stays offline, zero-token.
/// </summary>
public static partial class TemplateThemeService
{
    // ---- public model ------------------------------------------------------------------------

    /// <summary>Compact style facts extracted from a .dotx, fed into the prompt.</summary>
    public sealed record TemplateStyleSummary(
        string BodyFont,
        string HeadingFont,
        string? Heading1Color,
        string? Heading1SizePt,
        string? PrimaryAccent,
        string? SecondaryAccent,
        string? Background,
        string? HyperlinkColor);

    // ---- Part 1: local .dotx parsing ---------------------------------------------------------

    /// <summary>Opens a .dotx (or .docx) and extracts font/color facts from styles + theme parts.</summary>
    public static TemplateStyleSummary ParseDotx(string dotxPath)
    {
        using var doc = WordprocessingDocument.Open(dotxPath, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Not a valid .dotx/.docx (no main part).");

        // -- theme1.xml palette (accent1, accent2, dk1, lt1, hlink) --
        string? accent1 = null, accent2 = null, dk1 = null, lt1 = null, hlink = null;
        var themePart = main.ThemePart ?? main.GetPartsOfType<ThemePart>().FirstOrDefault();
        if (themePart is not null)
        {
            var xml = XDocument.Load(themePart.GetStream());
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var scheme = xml.Descendants(a + "clrScheme").FirstOrDefault();
            if (scheme is not null)
            {
                accent1 = ReadColor(scheme, a, "accent1");
                accent2 = ReadColor(scheme, a, "accent2");
                dk1 = ReadColor(scheme, a, "dk1");
                lt1 = ReadColor(scheme, a, "lt1");
                hlink = ReadColor(scheme, a, "hlink");
            }
        }

        // -- styles.xml: body font, heading font, heading1 color/size --
        string bodyFont = "Calibri", headingFont = "Calibri";
        string? h1Color = null, h1Size = null;
        var stylesPart = main.StyleDefinitionsPart;
        if (stylesPart is not null)
        {
            var sXml = XDocument.Load(stylesPart.GetStream());
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

            // Default document font (w:docDefaults > w:rPrDefault > w:rPr > w:rFonts ascii).
            var docDefaults = sXml.Descendants(w + "docDefaults").FirstOrDefault();
            var defaultFonts = docDefaults?.Descendants(w + "rFonts").FirstOrDefault();
            if (defaultFonts is not null)
                bodyFont = defaultFonts.Attribute(w + "ascii")?.Value ?? bodyFont;

            // Heading 1 style specifics.
            var h1Style = sXml.Descendants(w + "style")
                .FirstOrDefault(s => s.Attribute(w + "styleId")?.Value is "Heading1" or "Heading 1" or "heading 1");
            if (h1Style is not null)
            {
                var rPr = h1Style.Element(w + "rPr");
                var fonts = rPr?.Element(w + "rFonts");
                if (fonts is not null)
                    headingFont = fonts.Attribute(w + "ascii")?.Value ?? bodyFont;
                var color = rPr?.Element(w + "color");
                if (color is not null)
                    h1Color = "#" + (color.Attribute(w + "val")?.Value ?? "").TrimStart('#');
                var sz = rPr?.Element(w + "sz");
                if (sz is not null && int.TryParse(sz.Attribute(w + "val")?.Value, out var halfPts))
                    h1Size = (halfPts / 2.0).ToString("0.#");
            }
        }

        return new TemplateStyleSummary(
            BodyFont: bodyFont,
            HeadingFont: headingFont,
            Heading1Color: h1Color,
            Heading1SizePt: h1Size,
            PrimaryAccent: accent1,
            SecondaryAccent: accent2,
            Background: lt1,
            HyperlinkColor: hlink);
    }

    // ---- Part 2: prompt engineering ----------------------------------------------------------

    /// <summary>Builds an unambiguous prompt that instructs a web AI to output ONLY a ThemeDefinition JSON.</summary>
    public static string BuildPrompt(TemplateStyleSummary s)
    {
        var facts = new List<string>
        {
            $"Body Font: {s.BodyFont}",
            $"Heading Font: {s.HeadingFont}",
        };
        if (s.Heading1Color is not null) facts.Add($"Heading 1 Color: {s.Heading1Color}");
        if (s.Heading1SizePt is not null) facts.Add($"Heading 1 Size: {s.Heading1SizePt}pt");
        if (s.PrimaryAccent is not null) facts.Add($"Primary Accent: {s.PrimaryAccent}");
        if (s.SecondaryAccent is not null) facts.Add($"Secondary Accent: {s.SecondaryAccent}");
        if (s.Background is not null) facts.Add($"Page Background: {s.Background}");
        if (s.HyperlinkColor is not null) facts.Add($"Hyperlink Color: {s.HyperlinkColor}");

        var factsBlock = string.Join("\n", facts.Select(f => "  " + f));
        var jsonExample = "{\"name\":\"Corporate House Style\",\"background\":\"#FFFFFF\",\"text\":\"#1A1A1A\",\"heading\":\"#003366\",\"code\":\"#F5F5F5\",\"border\":\"#DDDDDD\",\"primary\":\"#0066CC\",\"secondary\":\"#004488\",\"line\":\"#E0E0E0\",\"fontFamily\":\"" + s.BodyFont + "\"}";

        return $"""
            Analyze this corporate document style summary and produce a matching color theme.

            Style facts:
            {factsBlock}

            Respond ONLY with a single valid JSON object. No markdown code blocks, no explanation, no extra text.
            The JSON must have EXACTLY these keys (all string values, colors as #RRGGBB hex):
            {jsonExample}

            Rules:
            - "background" must be a light page color (use the page background fact if given).
            - "text" must contrast strongly against background.
            - "heading" should match the heading color fact if given, else derive from primary accent.
            - "primary" and "secondary" should use the accent colors if given.
            - "fontFamily" must be "{s.BodyFont}".
            - Output the raw JSON object only. Nothing else.
            """;
    }

    // ---- Part 3: response parsing ------------------------------------------------------------

    [GeneratedRegex(@"```(?:json)?\s*\n?(.*?)\n?\s*```", RegexOptions.Singleline)]
    private static partial Regex CodeFenceRe();

    /// <summary>
    /// Parses the web AI's reply into a ThemeDefinition. Tolerant of code fences, prose wrappers,
    /// trailing commas, and missing fields (defaults applied). Returns null if unparseable.
    /// </summary>
    public static ThemeDefinition? ParseAiResponse(string? replyMarkdown)
    {
        if (string.IsNullOrWhiteSpace(replyMarkdown)) return null;

        var text = replyMarkdown.Trim();

        // Strip code fences if present.
        var fenceMatch = CodeFenceRe().Match(text);
        if (fenceMatch.Success) text = fenceMatch.Groups[1].Value.Trim();

        // Isolate the JSON object (first { to last }).
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = text[start..(end + 1)];

        // Tolerate trailing commas before } or ].
        json = TrailingCommaRe().Replace(json, "$1");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Get(string key, string fallback) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                    ? s : fallback;

            return new ThemeDefinition(
                Name: Get("name", "Corporate House Style"),
                Background: NormalizeHex(Get("background", "#FFFFFF")),
                Text: NormalizeHex(Get("text", "#1A1A1A")),
                Heading: NormalizeHex(Get("heading", "#003366")),
                Code: NormalizeHex(Get("code", "#F5F5F5")),
                Border: NormalizeHex(Get("border", "#DDDDDD")),
                Primary: NormalizeHex(Get("primary", "#0066CC")),
                Secondary: NormalizeHex(Get("secondary", "#004488")),
                Line: NormalizeHex(Get("line", "#E0E0E0")));
        }
        catch { return null; }
    }

    [GeneratedRegex(@",\s*([}\]])")]
    private static partial Regex TrailingCommaRe();

    // ---- Part 4: persistence -----------------------------------------------------------------

    /// <summary>Saves the parsed theme to the custom theme store and returns it.</summary>
    public static ThemeDefinition SaveTheme(ThemeDefinition theme)
    {
        CustomThemeStore.AddOrUpdate(theme);
        return theme;
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static string? ReadColor(XElement scheme, XNamespace a, string name)
    {
        var el = scheme.Element(a + name);
        if (el is null) return null;
        // The color value lives on the child element: <a:srgbClr val="0066CC"/> or <a:sysClr lastClr="000000"/>
        var child = el.Elements().FirstOrDefault();
        if (child is null) return null;
        var hex = child.Attribute("val")?.Value ?? child.Attribute("lastClr")?.Value;
        return hex is not null ? "#" + hex.TrimStart('#') : null;
    }

    private static string NormalizeHex(string color)
    {
        var c = color.Trim();
        if (!c.StartsWith('#')) c = "#" + c;
        return c;
    }
}
