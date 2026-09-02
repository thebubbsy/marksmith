using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using MarkSmith.Models;

namespace MarkSmith.Services;

/// <summary>
/// Reference Template Engine for merging Word Templates (.dotx) and Standard Documents (.docx)
/// into the OpenXML export pipeline without ID collisions, schema violations, or package corruption.
/// Extracts and merges:
/// 1. Styles (word/styles.xml) - docDefaults, custom styles, heading hierarchy
/// 2. Numbering (word/numbering.xml) - collision-free abstractNumId and numId remapping
/// 3. Section geometry & margins (w:sectPr) - page size, orientation, margins, columns, borders
/// 4. Running headers & footers (word/header*.xml, word/footer*.xml) - with cloned media & dynamic relationship IDs
/// 5. Font table (word/fontTable.xml) - embedded font metadata
/// </summary>
public static class ReferenceDocumentMerger
{
    public sealed class MergedReferenceResult
    {
        public bool Applied { get; init; }
        public int NextNumId { get; init; } = 2;
        public int BulletNumId { get; init; } = 1;
        public int BulletAbstractNumId { get; init; } = 0;
        public int OrderedAbstractNumId { get; init; } = 1;
        public W.SectionProperties? InheritedSectionProperties { get; init; }
        public List<W.HeaderReference> HeaderReferences { get; } = new();
        public List<W.FooterReference> FooterReferences { get; } = new();
        public string? ExtractedBrandFont { get; init; }
    }

    /// <summary>
    /// Checks if a given path is a valid .dotx or .docx reference document.
    /// </summary>
    public static bool IsValidReferenceDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".dotx", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".docx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Merges styles, numbering, font tables, headers, footers, and geometry from a reference document (.dotx or .docx).
    /// </summary>
    public static MergedReferenceResult MergeReference(
        MainDocumentPart targetMain,
        string referencePath,
        ThemeDefinition theme,
        AppSettings settings)
    {
        var result = new MergedReferenceResult();
        if (!IsValidReferenceDocument(referencePath))
            return result;

        try
        {
            using var refDoc = WordprocessingDocument.Open(referencePath, false);
            var refMain = refDoc.MainDocumentPart;
            if (refMain is null) return result;

            // 1. Font Table (word/fontTable.xml)
            MergeFontTable(targetMain, refMain);

            // 2. Styles (word/styles.xml)
            var extractedFont = MergeStyles(targetMain, refMain, theme, settings);

            // 3. Numbering (word/numbering.xml) with collision-free ID remapping
            var (nextNumId, bulletNumId, bulletAbstractId, orderedAbstractId) = MergeNumbering(targetMain, refMain);

            // 4. Section Properties & Headers / Footers
            var (sectPr, hdrRefs, ftrRefs) = MergeSectionAndHeadersFooters(targetMain, refMain, referencePath);

            return new MergedReferenceResult
            {
                Applied = true,
                NextNumId = nextNumId,
                BulletNumId = bulletNumId,
                BulletAbstractNumId = bulletAbstractId,
                OrderedAbstractNumId = orderedAbstractId,
                InheritedSectionProperties = sectPr,
                ExtractedBrandFont = extractedFont
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReferenceDocumentMerger] Warning: Failed to merge reference document '{referencePath}': {ex.Message}");
            return result;
        }
    }

    private static void MergeFontTable(MainDocumentPart targetMain, MainDocumentPart refMain)
    {
        if (refMain.FontTablePart is null) return;
        try
        {
            var targetFontTable = targetMain.FontTablePart ?? targetMain.AddNewPart<FontTablePart>();
            using var stream = refMain.FontTablePart.GetStream(FileMode.Open, FileAccess.Read);
            targetFontTable.FeedData(stream);
        }
        catch
        {
            // Non-critical: continue if font table cannot be cloned
        }
    }

    private static string? MergeStyles(
        MainDocumentPart targetMain,
        MainDocumentPart refMain,
        ThemeDefinition theme,
        AppSettings settings)
    {
        string? extractedBodyFont = null;
        if (refMain.StyleDefinitionsPart?.Styles is not { } refStyles || !refStyles.ChildElements.Any())
        {
            return null;
        }

        var targetStylesPart = targetMain.StyleDefinitionsPart ?? targetMain.AddNewPart<StyleDefinitionsPart>();
        var targetStyles = new W.Styles();

        // Clone docDefaults
        if (refStyles.DocDefaults is { } docDefaults)
        {
            var clonedDocDefaults = (W.DocDefaults)docDefaults.CloneNode(true);
            targetStyles.Append(clonedDocDefaults);

            var rFonts = clonedDocDefaults.RunPropertiesDefault?.RunPropertiesBaseStyle?.RunFonts;
            if (rFonts?.Ascii?.Value is { Length: > 0 } fontName)
            {
                extractedBodyFont = fontName;
            }
        }

        // Copy all styles from reference document
        foreach (var style in refStyles.Elements<W.Style>())
        {
            var clonedStyle = (W.Style)style.CloneNode(true);
            targetStyles.Append(clonedStyle);
        }

        // Ensure MarkSmith required semantic styles exist
        EnsureCoreSemanticStyles(targetStyles, theme, settings, extractedBodyFont);

        targetStylesPart.Styles = targetStyles;
        targetStylesPart.Styles.Save();
        return extractedBodyFont;
    }

    private static void EnsureCoreSemanticStyles(
        W.Styles styles,
        ThemeDefinition theme,
        AppSettings settings,
        string? baseFont)
    {
        var font = baseFont ?? settings.BrandFontFamily ?? "Segoe UI Variable";
        var defaultText = ContrastGuard.EnsureLegibleText(theme.Text, theme.Background);
        var headingColor = ContrastGuard.EnsureLegibleText(theme.Heading, theme.Background);

        // Ensure docDefaults exists if not present
        if (styles.DocDefaults is null)
        {
            styles.Append(new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    new W.RunFonts { Ascii = font, HighAnsi = font, EastAsia = font, ComplexScript = font },
                    new W.Color { Val = defaultText },
                    new W.Kern { Val = 16u },
                    new W.FontSize { Val = "22" })),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                    new W.SpacingBetweenLines { After = "160", Line = "259", LineRule = W.LineSpacingRuleValues.Auto }))));
        }

        // Ensure Normal style
        if (!styles.Elements<W.Style>().Any(s => s.StyleId?.Value is "Normal" or "normal"))
        {
            styles.Append(new W.Style(new W.StyleName { Val = "Normal" })
            {
                Type = W.StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            });
        }

        // Ensure Headings 1 through 6
        for (var level = 1; level <= 6; level++)
        {
            var styleId = $"Heading{level}";
            var existing = styles.Elements<W.Style>().FirstOrDefault(s =>
                string.Equals(s.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.StyleName?.Val?.Value, $"heading {level}", StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                var hStyle = new W.Style(new W.StyleName { Val = $"heading {level}" })
                {
                    Type = W.StyleValues.Paragraph,
                    StyleId = styleId
                };
                var pPr = new W.StyleParagraphProperties();
                pPr.Append(new W.KeepNext());
                pPr.Append(new W.OutlineLevel { Val = level - 1 });
                pPr.Append(new W.SpacingBetweenLines { Before = "240", After = "120" });

                var rPr = new W.StyleRunProperties();
                rPr.Append(new W.Bold());
                rPr.Append(new W.Color { Val = headingColor });
                rPr.Append(new W.FontSize { Val = level switch { 1 => "36", 2 => "30", 3 => "26", 4 => "24", _ => "22" } });

                hStyle.Append(pPr);
                hStyle.Append(rPr);
                styles.Append(hStyle);
            }
            else
            {
                // Ensure outline level is set for TOC and navigation
                var pPr = existing.StyleParagraphProperties ?? existing.AppendChild(new W.StyleParagraphProperties());
                if (pPr.OutlineLevel is null)
                {
                    pPr.Append(new W.OutlineLevel { Val = level - 1 });
                }
            }
        }

        // Ensure Hyperlink style
        if (!styles.Elements<W.Style>().Any(s => s.StyleId?.Value is "Hyperlink" or "hyperlink"))
        {
            styles.Append(new W.Style(new W.StyleName { Val = "Hyperlink" })
            {
                Type = W.StyleValues.Character,
                StyleId = "Hyperlink"
            });
        }
    }

    private static (int NextNumId, int BulletNumId, int BulletAbstractId, int OrderedAbstractId) MergeNumbering(
        MainDocumentPart targetMain,
        MainDocumentPart refMain)
    {
        var targetNumPart = targetMain.AddNewPart<NumberingDefinitionsPart>();
        var targetNumbering = new W.Numbering();

        int maxAbstractId = -1;
        int maxNumId = 0;

        if (refMain.NumberingDefinitionsPart?.Numbering is { } refNumbering)
        {
            // Scan max IDs in reference numbering
            foreach (var abs in refNumbering.Elements<W.AbstractNum>())
            {
                if (abs.AbstractNumberId?.Value is { } id && id > maxAbstractId)
                    maxAbstractId = id;
                targetNumbering.Append((W.AbstractNum)abs.CloneNode(true));
            }

            foreach (var num in refNumbering.Elements<W.NumberingInstance>())
            {
                if (num.NumberID?.Value is { } id && id > maxNumId)
                    maxNumId = id;
                targetNumbering.Append((W.NumberingInstance)num.CloneNode(true));
            }
        }

        // Collision-free allocation for MarkSmith dynamic lists
        int bulletAbstractId = maxAbstractId + 1;
        int orderedAbstractId = maxAbstractId + 2;
        int bulletNumId = maxNumId + 1;
        int orderedNumId = maxNumId + 2;
        int nextNumId = maxNumId + 3;

        // Build default bullet abstract definition
        var bullet = new W.AbstractNum(new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel })
        {
            AbstractNumberId = bulletAbstractId
        };
        string[] glyphs = { "•", "○", "▪" };
        for (var i = 0; i < 9; i++)
        {
            bullet.Append(new W.Level(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = W.NumberFormatValues.Bullet },
                new W.LevelText { Val = glyphs[i % 3] },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(new W.Indentation
                {
                    Left = ((i + 1) * 720).ToString(), Hanging = "360"
                }))
            { LevelIndex = i });
        }

        // Build default ordered abstract definition
        var ordered = new W.AbstractNum(new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel })
        {
            AbstractNumberId = orderedAbstractId
        };
        for (var i = 0; i < 9; i++)
        {
            ordered.Append(new W.Level(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = W.NumberFormatValues.Decimal },
                new W.LevelText { Val = $"%{i + 1}." },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(new W.Indentation
                {
                    Left = ((i + 1) * 720).ToString(), Hanging = "360"
                }))
            { LevelIndex = i });
        }

        targetNumbering.Append(bullet);
        targetNumbering.Append(ordered);
        targetNumbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = bulletAbstractId }) { NumberID = bulletNumId });
        targetNumbering.Append(new W.NumberingInstance(new W.AbstractNumId { Val = orderedAbstractId }) { NumberID = orderedNumId });

        targetNumPart.Numbering = targetNumbering;
        targetNumPart.Numbering.Save();

        return (nextNumId, bulletNumId, bulletAbstractId, orderedAbstractId);
    }

    private static (W.SectionProperties SectPr, List<W.HeaderReference> Headers, List<W.FooterReference> Footers) MergeSectionAndHeadersFooters(
        MainDocumentPart targetMain,
        MainDocumentPart refMain,
        string referencePath)
    {
        var targetSectPr = new W.SectionProperties();
        var headerRefs = new List<W.HeaderReference>();
        var footerRefs = new List<W.FooterReference>();

        var refBody = refMain.Document?.Body;
        var refSectPr = refBody?.Elements<W.SectionProperties>().LastOrDefault();
        if (refSectPr is null)
            return (targetSectPr, headerRefs, footerRefs);

        // 1. Clone Headers FIRST (strict ECMA-376 schema order)
        foreach (var hdrRef in refSectPr.Elements<W.HeaderReference>())
        {
            if (hdrRef.Id?.Value is { Length: > 0 } rId && refMain.GetPartById(rId) is HeaderPart srcHdr)
            {
                var targetHdr = targetMain.AddNewPart<HeaderPart>();
                using (var stream = srcHdr.GetStream(FileMode.Open, FileAccess.Read))
                {
                    targetHdr.FeedData(stream);
                }
                ClonePartImagesAndRelationships(srcHdr, targetHdr);
                var newRelId = targetMain.GetIdOfPart(targetHdr);
                var newHdrRef = new W.HeaderReference { Type = hdrRef.Type, Id = newRelId };
                targetSectPr.Append(newHdrRef);
                headerRefs.Add(newHdrRef);
            }
        }

        // 2. Clone Footers SECOND (strict ECMA-376 schema order)
        foreach (var ftrRef in refSectPr.Elements<W.FooterReference>())
        {
            if (ftrRef.Id?.Value is { Length: > 0 } rId && refMain.GetPartById(rId) is FooterPart srcFtr)
            {
                var targetFtr = targetMain.AddNewPart<FooterPart>();
                using (var stream = srcFtr.GetStream(FileMode.Open, FileAccess.Read))
                {
                    targetFtr.FeedData(stream);
                }
                ClonePartImagesAndRelationships(srcFtr, targetFtr);
                var newRelId = targetMain.GetIdOfPart(targetFtr);
                var newFtrRef = new W.FooterReference { Type = ftrRef.Type, Id = newRelId };
                targetSectPr.Append(newFtrRef);
                footerRefs.Add(newFtrRef);
            }
        }

        // 3. Clone Geometry in schema order: PageSize -> PageMargin -> PageBorders -> LineNumberType -> Columns -> TitlePage
        if (refSectPr.Elements<W.PageSize>().FirstOrDefault() is { } pgSz)
            targetSectPr.Append((W.PageSize)pgSz.CloneNode(true));

        if (refSectPr.Elements<W.PageMargin>().FirstOrDefault() is { } pgMar)
            targetSectPr.Append((W.PageMargin)pgMar.CloneNode(true));

        if (refSectPr.Elements<W.PageBorders>().FirstOrDefault() is { } borders)
            targetSectPr.Append((W.PageBorders)borders.CloneNode(true));

        if (refSectPr.Elements<W.LineNumberType>().FirstOrDefault() is { } lnNum)
            targetSectPr.Append((W.LineNumberType)lnNum.CloneNode(true));

        if (refSectPr.Elements<W.Columns>().FirstOrDefault() is { } cols)
            targetSectPr.Append((W.Columns)cols.CloneNode(true));

        if (refSectPr.Elements<W.TitlePage>().FirstOrDefault() is { } titlePg)
            targetSectPr.Append((W.TitlePage)titlePg.CloneNode(true));

        return (targetSectPr, headerRefs, footerRefs);
    }

    private static void ClonePartImagesAndRelationships(OpenXmlPart srcPart, OpenXmlPart targetPart)
    {
        foreach (var pair in srcPart.Parts.Where(p => p.OpenXmlPart is ImagePart))
        {
            var imagePart = (ImagePart)pair.OpenXmlPart;
            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            var newImage = targetPart.AddNewPart<ImagePart>(imagePart.ContentType, pair.RelationshipId);
            newImage.FeedData(stream);
        }

        foreach (var extRel in srcPart.ExternalRelationships)
        {
            targetPart.AddExternalRelationship(extRel.RelationshipType, extRel.Uri, extRel.Id);
        }
    }
}
