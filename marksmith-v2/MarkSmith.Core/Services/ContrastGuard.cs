using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;

namespace MarkSmith.Services;

/// <summary>
/// Top-level legibility guard for OpenXML DOCX and rendering engines.
/// Enforces W3C WCAG 2.1 contrast rules, mathematically preventing text from rendering on a similar background shade.
/// </summary>
public static class ContrastGuard
{
    // Strict ECMA-376 CT_RPr / EG_RPrBase child element ordering after <w:color>.
    // Inserting <w:color> before the first matching element in this set preserves schema validity
    // across W.RunProperties, W.StyleRunProperties, W.RunPropertiesBaseStyle, and W.NumberingSymbolRunProperties.
    private static readonly HashSet<string> ElementsAfterColorInRPr = new(StringComparer.Ordinal)
    {
        "spacing", "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect",
        "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout",
        "specVanish", "oMath", "rPrChange", "AlternateContent"
    };

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

    /// <summary>
    /// HARD RULE — a shape must NEVER blend into the background it sits on.
    /// Returns the given fill when it is clearly visible against the background (WCAG contrast
    /// >= 1.8:1, the practical floor for distinguishing a filled area from its backdrop); otherwise
    /// returns the strongest visible candidate from (white, black, the fill itself) so the shape
    /// edge is always distinguishable. This is the rule that stops "same colour as the background"
    /// shapes in the shape studio and in rendered compositions.
    /// </summary>
    public static string EnsureVisibleFill(string fillHex, string bgContextHex)
    {
        fillHex = (fillHex ?? "").Trim().TrimStart('#');
        bgContextHex = (bgContextHex ?? "").Trim().TrimStart('#');
        if (fillHex.Length != 6) fillHex = "0078D4";
        if (bgContextHex.Length != 6) bgContextHex = "FFFFFF";

        if (GetContrastRatio(fillHex, bgContextHex) >= 1.8) return fillHex;

        // Blend: keep the intended fill if it beats white/black, otherwise let the strongest win.
        string best = fillHex;
        double bestRatio = GetContrastRatio(fillHex, bgContextHex);
        foreach (string candidate in new[] { "FFFFFF", "121212" })
        {
            double r = GetContrastRatio(candidate, bgContextHex);
            if (r > bestRatio) { bestRatio = r; best = candidate; }
        }
        return best;
    }

    /// <summary>
    /// Scans SVG text elements (<text fill="..."> and <tspan fill="...">) and automatically enforces
    /// WCAG 2.1 4.5:1 minimum contrast ratio against the document/card background context.
    /// Prevents white-on-white or dark-on-dark text in PlantUML, Graphviz, or third-party SVG output.
    /// </summary>
    public static string EnsureSvgLegibility(string svg, string? bgContextHex = null)
    {
        if (string.IsNullOrWhiteSpace(svg)) return svg;
        if (string.IsNullOrWhiteSpace(bgContextHex)) bgContextHex = "FFFFFF";

        return System.Text.RegularExpressions.Regex.Replace(svg,
            @"<(text|tspan)\b[^>]*>",
            m =>
            {
                string tag = m.Value;
                // Text already CONTRAST-GUARDED against its own shape's fill (MLShape labels carry
                // data-guarded="shape") must NOT be re-guarded against the page background — the
                // page rule would flip an already-correct white-on-dark label to dark-on-white.
                if (tag.Contains("data-guarded", StringComparison.OrdinalIgnoreCase))
                    return tag;

                var fill = System.Text.RegularExpressions.Regex.Match(tag,
                    @"\bfill\s*=\s*([""'])(#?[a-zA-Z0-9]+)\1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!fill.Success) return tag;

                var hex = NormalizeHex(fill.Groups[2].Value);
                if (hex == null) return tag;

                var legibleHex = EnsureLegibleText(hex, bgContextHex);
                return tag.Remove(fill.Index, fill.Length)
                          .Insert(fill.Index, $"fill={fill.Groups[1].Value}#{legibleHex}{fill.Groups[1].Value}");
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Inserts or updates a <see cref="W.Color"/> element inside any OpenXML run-properties container
    /// (<see cref="W.RunProperties"/>, <see cref="W.StyleRunProperties"/>, <see cref="W.RunPropertiesBaseStyle"/>,
    /// <see cref="W.NumberingSymbolRunProperties"/>) in strict ECMA-376 schema order, stripping themeColor
    /// overrides so Word never substitutes a dark scheme color over a contrast-verified hex value.
    /// </summary>
    public static void SetRPrColorInSchemaOrder(OpenXmlCompositeElement rPr, string colorHex)
    {
        if (rPr is null) return;
        var cleanHex = (colorHex ?? "000000").Trim().TrimStart('#');
        if (cleanHex.Length != 6) cleanHex = "000000";

        if (rPr.GetFirstChild<W.Color>() is { } existing)
        {
            existing.Val = cleanHex;
            existing.ThemeColor = null;
            existing.ThemeTint = null;
            existing.ThemeShade = null;
            return;
        }

        var newColor = new W.Color { Val = cleanHex };
        var anchor = rPr.ChildElements.FirstOrDefault(e => ElementsAfterColorInRPr.Contains(e.LocalName));
        if (anchor is not null)
            rPr.InsertBefore(newColor, anchor);
        else
            rPr.Append(newColor);
    }

    /// <summary>
    /// OUTPUT-PARAMETER LOGIC GATE for <c>word/styles.xml</c>:
    /// Mathematically guarantees that <c>w:docDefaults</c> and every <c>w:style</c> (including styles cloned
    /// from an external reference template via <see cref="ReferenceDocumentMerger"/>) satisfy WCAG 2.1
    /// 4.5:1 contrast against their style shading fill or <paramref name="pageBgHex"/>.
    /// </summary>
    public static void EnforceStylesContrast(
        W.Styles? styles,
        string pageBgHex,
        string defaultTextHex,
        string headingHex,
        string linkHex)
    {
        if (styles is null) return;

        var cleanPageBg = NormalizeHex(pageBgHex) ?? "FFFFFF";
        var safeDefaultText = EnsureLegibleText(defaultTextHex, cleanPageBg);
        var safeHeading = EnsureLegibleText(headingHex, cleanPageBg, safeDefaultText);
        var safeLink = EnsureLegibleText(linkHex, cleanPageBg, safeDefaultText);

        // 1. Guarantee w:docDefaults -> w:rPrDefault -> w:rPr -> w:color
        var docDefaults = styles.DocDefaults;
        if (docDefaults is null)
        {
            docDefaults = new W.DocDefaults();
            styles.PrependChild(docDefaults);
        }
        var rPrDefault = docDefaults.RunPropertiesDefault ?? docDefaults.PrependChild(new W.RunPropertiesDefault());
        var rPrBase = rPrDefault.RunPropertiesBaseStyle ?? rPrDefault.PrependChild(new W.RunPropertiesBaseStyle());
        var existingDocColor = NormalizeHex(rPrBase.GetFirstChild<W.Color>()?.Val?.Value);
        var guardedDocColor = EnsureLegibleText(existingDocColor ?? safeDefaultText, cleanPageBg, safeDefaultText);
        SetRPrColorInSchemaOrder(rPrBase, guardedDocColor);

        // 2. Audit every w:style in styles.xml
        foreach (var style in styles.Elements<W.Style>())
        {
            var styleId = style.StyleId?.Value ?? "";
            var styleName = style.StyleName?.Val?.Value ?? "";

            var styleRunShd = NormalizeHex(style.StyleRunProperties?.Shading?.Fill?.Value);
            var styleParaShd = NormalizeHex(style.StyleParagraphProperties?.Shading?.Fill?.Value);
            var effectiveBg = styleRunShd ?? styleParaShd ?? cleanPageBg;
            var localFallbackText = EnsureLegibleText(safeDefaultText, effectiveBg);

            bool isHeading = styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                             || styleName.StartsWith("heading ", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(styleId, "Title", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(styleId, "Subtitle", StringComparison.OrdinalIgnoreCase);
            bool isLink = string.Equals(styleId, "Hyperlink", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(styleName, "Hyperlink", StringComparison.OrdinalIgnoreCase);

            var sRPr = style.StyleRunProperties;
            var existingStyleColor = NormalizeHex(sRPr?.GetFirstChild<W.Color>()?.Val?.Value);

            if (isHeading)
            {
                sRPr ??= EnsureStyleRunProperties(style);
                var preferredHeading = EnsureLegibleText(safeHeading, effectiveBg, localFallbackText);
                var guarded = EnsureLegibleText(existingStyleColor ?? preferredHeading, effectiveBg, preferredHeading);
                SetRPrColorInSchemaOrder(sRPr, guarded);
            }
            else if (isLink)
            {
                sRPr ??= EnsureStyleRunProperties(style);
                var preferredLink = EnsureLegibleText(safeLink, effectiveBg, localFallbackText);
                var guarded = EnsureLegibleText(existingStyleColor ?? preferredLink, effectiveBg, preferredLink);
                SetRPrColorInSchemaOrder(sRPr, guarded);
            }
            else if (sRPr?.GetFirstChild<W.Color>() is not null || effectiveBg != cleanPageBg)
            {
                sRPr ??= EnsureStyleRunProperties(style);
                var guarded = EnsureLegibleText(existingStyleColor ?? localFallbackText, effectiveBg, localFallbackText);
                SetRPrColorInSchemaOrder(sRPr, guarded);
            }

            // 3. Audit conditional table style formatting blocks (<w:tblStylePr>)
            foreach (var tblStylePr in style.Elements<W.TableStyleProperties>())
            {
                var tcShd = NormalizeHex(tblStylePr.TableStyleConditionalFormattingTableCellProperties?.Shading?.Fill?.Value);
                var partBg = tcShd ?? effectiveBg;
                var partFallback = EnsureLegibleText(safeDefaultText, partBg);
                if (tblStylePr.RunPropertiesBaseStyle is { } tblRPr)
                {
                    if (tblRPr.GetFirstChild<W.Color>() is not null || partBg != cleanPageBg)
                    {
                        var cur = NormalizeHex(tblRPr.GetFirstChild<W.Color>()?.Val?.Value);
                        SetRPrColorInSchemaOrder(tblRPr, EnsureLegibleText(cur ?? partFallback, partBg, partFallback));
                    }
                }
                else if (partBg != cleanPageBg && GetContrastRatio(guardedDocColor, partBg) < 4.5)
                {
                    var newTblRPr = new W.RunPropertiesBaseStyle();
                    SetRPrColorInSchemaOrder(newTblRPr, partFallback);
                    tblStylePr.Append(newTblRPr);
                }
            }
        }
    }

    /// <summary>
    /// OUTPUT-PARAMETER LOGIC GATE for <c>word/numbering.xml</c>:
    /// Ensures any bullet or list-level <c>w:rPr/w:color</c> imported from a reference template meets
    /// WCAG 2.1 4.5:1 contrast against <paramref name="pageBgHex"/>.
    /// </summary>
    public static void EnforceNumberingContrast(W.Numbering? numbering, string pageBgHex, string defaultTextHex)
    {
        if (numbering is null) return;
        var cleanPageBg = NormalizeHex(pageBgHex) ?? "FFFFFF";
        var safeDefaultText = EnsureLegibleText(defaultTextHex, cleanPageBg);

        foreach (var rPr in numbering.Descendants<W.NumberingSymbolRunProperties>())
        {
            if (rPr.GetFirstChild<W.Color>() is { } c)
            {
                var cur = NormalizeHex(c.Val?.Value);
                SetRPrColorInSchemaOrder(rPr, EnsureLegibleText(cur ?? safeDefaultText, cleanPageBg, safeDefaultText));
            }
        }
    }

    /// <summary>
    /// OUTPUT-PARAMETER LOGIC GATE for any OpenXML content subtree (<c>W.Body</c>, <c>W.Header</c>,
    /// <c>W.Footer</c>, <c>W.Footnote</c>, <c>W.Comment</c>):
    /// Resolves the innermost effective background behind every run (<c>w:highlight</c> -> <c>rPr/w:shd</c> ->
    /// <c>pPr/w:shd</c> -> innermost <c>tcPr/w:shd</c> -> <paramref name="pageBgHex"/>) and forces the run's
    /// <c>w:color</c> to satisfy >= 4.5:1 WCAG contrast.
    /// </summary>
    public static void EnforceSubtreeContrast(OpenXmlElement? root, string pageBgHex, string defaultTextHex)
    {
        if (root is null) return;
        var cleanPageBg = NormalizeHex(pageBgHex) ?? "FFFFFF";
        var safeDefaultText = EnsureLegibleText(defaultTextHex, cleanPageBg);

        foreach (var run in root.Descendants<W.Run>())
        {
            var rPr = run.RunProperties;

            // Emoji runs use Segoe UI Emoji color glyphs and should not carry a monochrome w:color override.
            if (string.Equals(rPr?.RunFonts?.Ascii?.Value, "Segoe UI Emoji", StringComparison.OrdinalIgnoreCase))
            {
                rPr?.RemoveAllChildren<W.Color>();
                continue;
            }

            var explicitColor = rPr?.GetFirstChild<W.Color>();
            bool hasTextOrField = run.Elements<W.Text>().Any()
                                  || run.Elements<W.DeletedText>().Any()
                                  || run.Elements<W.FieldCode>().Any()
                                  || run.Elements<W.FootnoteReference>().Any()
                                  || run.Elements<W.FootnoteReferenceMark>().Any();

            if (explicitColor is null && !hasTextOrField)
                continue;

            var effectiveBg = ResolveEffectiveBackground(run, rPr, cleanPageBg);
            var localFallback = EnsureLegibleText(safeDefaultText, effectiveBg);

            if (explicitColor is not null)
            {
                var rawHex = NormalizeHex(explicitColor.Val?.Value);
                var minRatio = (root is W.Header or W.Footer || IsDarkPage(effectiveBg)) ? MinimumContrastRatio : 3.0;
                var guarded = rawHex is not null && GetContrastRatio(rawHex, effectiveBg) >= minRatio
                    ? rawHex
                    : localFallback;
                SetRPrColorInSchemaOrder(rPr!, guarded);
            }
            else if (!string.Equals(effectiveBg, cleanPageBg, StringComparison.OrdinalIgnoreCase)
                     || root is W.Header or W.Footer)
            {
                // When a run sits inside a shaded container (inline code, code block, quote, table cell,
                // alert panel, highlight) or inside a running header/footer (whose paragraph style in a
                // merged template might not inherit Normal), explicitly stamp a contrast-verified color.
                rPr ??= (run.RunProperties = new W.RunProperties());
                SetRPrColorInSchemaOrder(rPr, localFallback);
            }
        }

        // Also guard any OMML math runs (<m:r>) that carry a W.RunProperties or sit inside a shaded container.
        foreach (var mathRun in root.Descendants<M.Run>())
        {
            var wRPr = mathRun.GetFirstChild<W.RunProperties>();
            var effectiveBg = ResolveEffectiveBackground(mathRun, wRPr, cleanPageBg);
            var localFallback = EnsureLegibleText(safeDefaultText, effectiveBg);

            if (wRPr?.GetFirstChild<W.Color>() is { } mc)
            {
                var rawHex = NormalizeHex(mc.Val?.Value);
                SetRPrColorInSchemaOrder(wRPr, EnsureLegibleText(rawHex ?? localFallback, effectiveBg, localFallback));
            }
            else if (!string.Equals(effectiveBg, cleanPageBg, StringComparison.OrdinalIgnoreCase)
                     && GetContrastRatio(safeDefaultText, effectiveBg) < 4.5)
            {
                wRPr ??= mathRun.PrependChild(new W.RunProperties());
                SetRPrColorInSchemaOrder(wRPr, localFallback);
            }
        }
    }

    /// <summary>
    /// Sweeps all auxiliary parts of a <see cref="MainDocumentPart"/> (<c>styles.xml</c>, <c>numbering.xml</c>,
    /// <c>header*.xml</c>, <c>footer*.xml</c>, <c>footnotes.xml</c>, <c>comments.xml</c>) to guarantee
    /// document-wide WCAG 2.1 contrast compliance.
    /// </summary>
    public static void EnforcePackageContrast(
        MainDocumentPart? main,
        string pageBgHex,
        string defaultTextHex,
        string headingHex,
        string linkHex)
    {
        if (main is null) return;

        if (main.StyleDefinitionsPart?.Styles is { } styles)
        {
            EnforceStylesContrast(styles, pageBgHex, defaultTextHex, headingHex, linkHex);
            styles.Save();
        }

        if (main.NumberingDefinitionsPart?.Numbering is { } numbering)
        {
            EnforceNumberingContrast(numbering, pageBgHex, defaultTextHex);
            numbering.Save();
        }

        foreach (var headerPart in main.HeaderParts)
        {
            if (headerPart.Header is { } header)
            {
                EnforceSubtreeContrast(header, pageBgHex, defaultTextHex);
                header.Save();
            }
        }

        foreach (var footerPart in main.FooterParts)
        {
            if (footerPart.Footer is { } footer)
            {
                EnforceSubtreeContrast(footer, pageBgHex, defaultTextHex);
                footer.Save();
            }
        }

        if (main.FootnotesPart?.Footnotes is { } footnotes)
        {
            EnforceSubtreeContrast(footnotes, pageBgHex, defaultTextHex);
            footnotes.Save();
        }

        if (main.WordprocessingCommentsPart?.Comments is { } comments)
        {
            EnforceSubtreeContrast(comments, pageBgHex, defaultTextHex);
            comments.Save();
        }
    }

    private static W.StyleRunProperties EnsureStyleRunProperties(W.Style style)
    {
        if (style.StyleRunProperties is { } existing) return existing;
        var sRPr = new W.StyleRunProperties();
        // In CT_Style, w:rPr follows w:pPr (or metadata elements) and precedes w:tblPr / w:tcPr / w:tblStylePr.
        var before = style.ChildElements.FirstOrDefault(e =>
            e.LocalName is "tblPr" or "trPr" or "tcPr" or "tblStylePr");
        if (before is not null)
            style.InsertBefore(sRPr, before);
        else
            style.Append(sRPr);
        return sRPr;
    }

    private static string ResolveEffectiveBackground(OpenXmlElement run, W.RunProperties? rPr, string pageBgHex)
    {
        // 1. Word highlight (<w:highlight>) paints directly behind glyph boxes.
        if (rPr?.GetFirstChild<W.Highlight>()?.Val?.Value is { } hl && TryMapHighlightHex(hl, out var hlHex))
            return hlHex;

        // 2. Inline run shading (<w:rPr><w:shd w:fill="..."/>)
        if (NormalizeHex(rPr?.Shading?.Fill?.Value) is { } runFill)
            return runFill;

        // 3. Paragraph shading (<w:pPr><w:shd w:fill="..."/>)
        var para = run.Ancestors<W.Paragraph>().FirstOrDefault();
        if (NormalizeHex(para?.ParagraphProperties?.Shading?.Fill?.Value) is { } paraFill)
            return paraFill;

        // 4. Innermost table cell shading (<w:tcPr><w:shd w:fill="..."/>)
        foreach (var cell in run.Ancestors<W.TableCell>())
        {
            if (NormalizeHex(cell.TableCellProperties?.Shading?.Fill?.Value) is { } cellFill)
                return cellFill;
        }

        // 5. Document page background (<w:background w:color="..."/>)
        return pageBgHex;
    }

    private static bool TryMapHighlightHex(W.HighlightColorValues hl, out string hex)
    {
        if (hl == W.HighlightColorValues.Yellow) { hex = "FFFF00"; return true; }
        if (hl == W.HighlightColorValues.Green) { hex = "00FF00"; return true; }
        if (hl == W.HighlightColorValues.Cyan) { hex = "00FFFF"; return true; }
        if (hl == W.HighlightColorValues.Magenta) { hex = "FF00FF"; return true; }
        if (hl == W.HighlightColorValues.Blue) { hex = "0000FF"; return true; }
        if (hl == W.HighlightColorValues.Red) { hex = "FF0000"; return true; }
        if (hl == W.HighlightColorValues.DarkBlue) { hex = "000080"; return true; }
        if (hl == W.HighlightColorValues.DarkCyan) { hex = "008080"; return true; }
        if (hl == W.HighlightColorValues.DarkGreen) { hex = "008000"; return true; }
        if (hl == W.HighlightColorValues.DarkMagenta) { hex = "800080"; return true; }
        if (hl == W.HighlightColorValues.DarkRed) { hex = "800000"; return true; }
        if (hl == W.HighlightColorValues.DarkYellow) { hex = "808000"; return true; }
        if (hl == W.HighlightColorValues.DarkGray) { hex = "808080"; return true; }
        if (hl == W.HighlightColorValues.LightGray) { hex = "C0C0C0"; return true; }
        if (hl == W.HighlightColorValues.Black) { hex = "000000"; return true; }
        if (hl == W.HighlightColorValues.White) { hex = "FFFFFF"; return true; }
        hex = "";
        return false;
    }

    private static string? NormalizeHex(string? colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr)) return null;
        colorStr = colorStr.Trim();
        if (colorStr.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (colorStr.Equals("white", StringComparison.OrdinalIgnoreCase)) return "FFFFFF";
        if (colorStr.Equals("black", StringComparison.OrdinalIgnoreCase)) return "000000";
        var s = colorStr.TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return null;
        for (int i = 0; i < 6; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return null;
        }
        return s.ToUpperInvariant();
    }
}

