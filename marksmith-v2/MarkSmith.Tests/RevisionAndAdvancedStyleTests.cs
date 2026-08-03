using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Core.Tests;

public class RevisionAndAdvancedStyleTests
{
    private static WordprocessingDocument OpenGeneratedDoc(string md, out string path, AppSettings? settings = null)
    {
        path = Path.Combine(Path.GetTempPath(), $"mk-rev-test-{Guid.NewGuid():N}.docx");
        new DocxExportService().ExportAsync(md, path, settings ?? new AppSettings()).GetAwaiter().GetResult();
        return WordprocessingDocument.Open(path, false);
    }

    [Fact]
    public void Del_Renders_WDel_With_WDelText_And_No_WT_Inside()
    {
        using var doc = OpenGeneratedDoc("<del author=\"Marksmith AI\">outdated text</del>", out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var dels = body.Descendants<W.DeletedRun>().ToList();
            Assert.Single(dels);

            var del = dels[0];
            Assert.Equal("Marksmith AI", del.Author?.Value);
            Assert.False(string.IsNullOrEmpty(del.Id?.Value));
            Assert.NotNull(del.Date?.Value);

            var delTexts = del.Descendants<W.DeletedText>().ToList();
            Assert.Single(delTexts);
            Assert.Equal("outdated text", delTexts[0].Text);

            // MANDATORY SCHEMA REQUIREMENT: No standard <w:t> tags allowed inside <w:del>
            var textTags = del.Descendants<W.Text>().ToList();
            Assert.Empty(textTags);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Ins_Renders_WIns_With_WT_And_Author_Date_Id()
    {
        using var doc = OpenGeneratedDoc("<ins author=\"Test Author\">inserted text</ins>", out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var inss = body.Descendants<W.InsertedRun>().ToList();
            Assert.Single(inss);

            var ins = inss[0];
            Assert.Equal("Test Author", ins.Author?.Value);
            Assert.False(string.IsNullOrEmpty(ins.Id?.Value));
            Assert.NotNull(ins.Date?.Value);

            var texts = ins.Descendants<W.Text>().ToList();
            Assert.Single(texts);
            Assert.Equal("inserted text", texts[0].Text);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void CriticMarkup_Normalization_And_Revision_Rendering()
    {
        var md = "Original text {~~old text~>~new text~~} and {++added text++} and {~~deleted text~~} and {==highlighted==}.";
        using var doc = OpenGeneratedDoc(md, out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var dels = body.Descendants<W.DeletedRun>().ToList();
            var inss = body.Descendants<W.InsertedRun>().ToList();
            var hls = body.Descendants<W.Highlight>().ToList();

            Assert.True(dels.Count >= 2, $"Expected at least 2 deletions, found {dels.Count}");
            Assert.True(inss.Count >= 2, $"Expected at least 2 insertions, found {inss.Count}");
            Assert.NotEmpty(hls);

            Assert.Contains(dels, d => d.Descendants<W.DeletedText>().Any(t => t.Text == "old text"));
            Assert.Contains(dels, d => d.Descendants<W.DeletedText>().Any(t => t.Text == "deleted text"));
            Assert.Contains(inss, i => i.Descendants<W.Text>().Any(t => t.Text == "new text"));
            Assert.Contains(inss, i => i.Descendants<W.Text>().Any(t => t.Text == "added text"));
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void AdvancedUnderline_Wave_Red_Renders_WUnderline_Attributes()
    {
        var md = "<u style=\"text-decoration: underline wave #FF0000\">wavy red underline</u>";
        using var doc = OpenGeneratedDoc(md, out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var underlines = body.Descendants<W.Underline>().ToList();
            Assert.NotEmpty(underlines);

            var u = underlines.FirstOrDefault(x => x.Val?.Value == W.UnderlineValues.Wave);
            Assert.NotNull(u);
            Assert.Equal("FF0000", u.Color?.Value);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Highlight_Preset_Color_Cyan_Renders_WHighlight_Val_Cyan()
    {
        var md = "<mark color=\"cyan\">cyan highlight</mark>";
        using var doc = OpenGeneratedDoc(md, out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var highlights = body.Descendants<W.Highlight>().ToList();
            Assert.NotEmpty(highlights);

            var hl = highlights.FirstOrDefault(x => x.Val?.Value == W.HighlightColorValues.Cyan);
            Assert.NotNull(hl);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Character_Shading_CustomHex_Color_Renders_WShd_Fill()
    {
        var md = "<span style=\"background-color: #E6F3FF\">custom shaded text</span>";
        using var doc = OpenGeneratedDoc(md, out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var shadings = body.Descendants<W.Shading>().Where(s => s.Fill?.Value != "auto").ToList();
            Assert.NotEmpty(shadings);

            var shd = shadings.FirstOrDefault(s => s.Fill?.Value == "E6F3FF");
            Assert.NotNull(shd);
            Assert.Equal(W.ShadingPatternValues.Clear, shd.Val?.Value);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void TrackChanges_DocumentSettings_Has_TrackRevisions()
    {
        // Track Changes is opt-in (default off); enable it to verify the setting is then emitted.
        using var doc = OpenGeneratedDoc("Sample text", out var path, new AppSettings { TrackChanges = true });
        try
        {
            var settings = doc.MainDocumentPart?.DocumentSettingsPart?.Settings;
            Assert.NotNull(settings);
            var trackRevisions = settings.Descendants<W.TrackRevisions>().FirstOrDefault();
            Assert.NotNull(trackRevisions);
            Assert.True(trackRevisions.Val?.Value ?? false);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Revisions_And_Advanced_Styles_Pass_ECMA376_OpenXml_Validation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-val-rev-{Guid.NewGuid():N}.docx");
        try
        {
            var md = @"# Revision & Visual Styles Test

Here is text with <ins author=""Marksmith AI"">inserted text</ins> and <del author=""Marksmith AI"">deleted text</del>.

CriticMarkup substitution: {~~legacy system~>~cloud native cluster~~}.

Advanced Underlines:
- <u style=""text-decoration: wave #FF0000"">Red wavy underline</u>
- <u style=""text-decoration: dotted #008000"">Green dotted underline</u>
- <u style=""text-decoration: double #0000FF"">Blue double underline</u>

Preset Highlights & Shading:
- <mark color=""cyan"">Cyan highlight</mark>
- <mark color=""magenta"">Magenta highlight</mark>
- <span style=""background-color: #E6F3FF"">Soft Blue Character Shading</span>
";
            new DocxExportService().ExportAsync(md, path, new AppSettings()).GetAwaiter().GetResult();

            using var wordDoc = WordprocessingDocument.Open(path, false);
            var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
            var errors = validator.Validate(wordDoc)
                .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                .ToList();

            if (errors.Count > 0)
            {
                var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
                Assert.Fail($"OpenXML ECMA-376 Validation failed with {errors.Count} errors:\n{msg}");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void CriticMarkup_Deletion_Before_Substitution_On_Same_Line_Produces_Correct_Html()
    {
        var input = "{~~del1~~} and {~~old~>~new~~}";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Equal("<del>del1</del> and <del>old</del><ins>new</ins>", normalized);

        using var doc = OpenGeneratedDoc(input, out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var dels = body.Descendants<W.DeletedRun>().ToList();
            var inss = body.Descendants<W.InsertedRun>().ToList();

            Assert.Equal(2, dels.Count);
            Assert.Single(inss);
            Assert.Contains(dels, d => d.Descendants<W.DeletedText>().Any(t => t.Text == "del1"));
            Assert.Contains(dels, d => d.Descendants<W.DeletedText>().Any(t => t.Text == "old"));
            Assert.Contains(inss, i => i.Descendants<W.Text>().Any(t => t.Text == "new"));
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Ins_With_Empty_Author_Defaults_To_Marksmith_AI()
    {
        using var doc = OpenGeneratedDoc("<ins author=\"\">text</ins>", out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var inss = body.Descendants<W.InsertedRun>().ToList();
            Assert.Single(inss);

            var ins = inss[0];
            Assert.Equal("Marksmith AI", ins.Author?.Value);
            Assert.NotEqual("\"\"", ins.Author?.Value);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void CriticMarkup_Comments_Are_Stripped()
    {
        var input = "Text before {>>this is a comment<<} text after.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.DoesNotContain("this is a comment", normalized);

        using var doc = OpenGeneratedDoc(input, out var path);
        try
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            var text = body.InnerText;
            Assert.DoesNotContain("this is a comment", text);
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Generate_Sample_Track_Changes_And_Styles_Docx()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        var outputDir = Path.Combine(repoRoot, "test_outputs");
        if (!Directory.Exists(outputDir))
        {
            outputDir = @"C:\Users\Tony\.gemini\antigravity\scratch\marksmith\test_outputs";
        }
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.GetFullPath(Path.Combine(outputDir, "sample_track_changes_and_styles.docx"));

        var markdown = @"# Comprehensive Track Changes & Advanced Styles Sample

This document demonstrates native Word revision tracking, CriticMarkup processing, alternative underline styles, character highlights, and 24-bit RGB character background shading in Marksmith AI.

## 1. Native Word Track Changes (Revisions)
- Tracked Insertion: <ins author=""Marksmith AI"" date=""2026-07-23T06:00:00Z"">Newly inserted clause</ins>
- Tracked Deletion: <del author=""Marksmith AI"" date=""2026-07-23T06:00:00Z"">Outdated legacy clause</del>

## 2. CriticMarkup Elements
- CriticMarkup Insertion: {++CriticMarkup inserted content++}
- CriticMarkup Deletion: {~~CriticMarkup deleted content~~}
- CriticMarkup Highlight: {==CriticMarkup highlighted text==}
- CriticMarkup Substitution: {~~old deprecated feature~>~new modern implementation~~}

## 3. Alternative Underline Styles & Colors
- Double wave underline: <u style=""text-decoration: wavyDouble #FF0000"">Red double-wave underline text</u>
- Dotted underline: <u style=""text-decoration: dotted #008000"">Green dotted underline text</u>
- Custom hex color underline: <u style=""text-decoration: single #FF0000"">Custom red underline text</u>

## 4. Predefined OpenXML Character Highlights
- Cyan highlight: <mark color=""cyan"">Cyan highlighted text</mark>
- Yellow highlight: <mark color=""yellow"">Yellow highlighted text</mark>

## 5. 24-bit RGB Character Background Shading
- Custom RGB Shading #FF5733: <span style=""background-color: #FF5733"">Coral background text</span>
- Custom RGB Shading #3399FF: <span style=""background-color: #3399FF"">Sky blue background text</span>
";

        new DocxExportService().ExportAsync(markdown, outputPath, new AppSettings { TrackChanges = true }).GetAwaiter().GetResult();

        Assert.True(File.Exists(outputPath), $"Sample .docx not found at {outputPath}");

        using var doc = WordprocessingDocument.Open(outputPath, false);

        // 1. Verify TrackRevisions setting (<w:trackRevisions w:val="true"/>)
        var settings = doc.MainDocumentPart?.DocumentSettingsPart?.Settings;
        Assert.NotNull(settings);
        var trackRevisions = settings.Descendants<W.TrackRevisions>().FirstOrDefault();
        Assert.NotNull(trackRevisions);
        Assert.True(trackRevisions.Val?.Value ?? false, "TrackRevisions must be enabled");

        var body = doc.MainDocumentPart!.Document.Body!;

        // 2. Verify Native Tracked Deletions & Insertions
        var dels = body.Descendants<W.DeletedRun>().ToList();
        var inss = body.Descendants<W.InsertedRun>().ToList();
        Assert.NotEmpty(dels);
        Assert.NotEmpty(inss);

        var sampleIns = inss.FirstOrDefault(i => i.Author?.Value == "Marksmith AI");
        Assert.NotNull(sampleIns);
        Assert.NotNull(sampleIns.Date?.Value);
        Assert.True(sampleIns.Descendants<W.Text>().Any(), "<w:ins> must wrap <w:t>");

        var sampleDel = dels.FirstOrDefault(d => d.Author?.Value == "Marksmith AI");
        Assert.NotNull(sampleDel);
        Assert.NotNull(sampleDel.Date?.Value);
        Assert.True(sampleDel.Descendants<W.DeletedText>().Any(), "<w:del> must wrap <w:delText>");
        Assert.Empty(sampleDel.Descendants<W.Text>());

        // 3. Verify CriticismMarkup elements presence in OpenXML structure
        Assert.Contains(dels, d => d.Descendants<W.DeletedText>().Any(t => t.Text.Contains("CriticMarkup deleted content") || t.Text.Contains("old deprecated feature")));
        Assert.Contains(inss, i => i.Descendants<W.Text>().Any(t => t.Text.Contains("CriticMarkup inserted content") || t.Text.Contains("new modern implementation")));

        // 4. Verify Underline styles
        var underlines = body.Descendants<W.Underline>().ToList();
        Assert.Contains(underlines, u => u.Val?.Value == W.UnderlineValues.WavyDouble && u.Color?.Value == "FF0000");
        Assert.Contains(underlines, u => u.Val?.Value == W.UnderlineValues.Dotted);
        Assert.Contains(underlines, u => u.Color?.Value == "FF0000");

        // 5. Verify Highlights
        var highlights = body.Descendants<W.Highlight>().ToList();
        Assert.Contains(highlights, h => h.Val?.Value == W.HighlightColorValues.Cyan);
        Assert.Contains(highlights, h => h.Val?.Value == W.HighlightColorValues.Yellow);

        // 6. Verify 24-bit RGB Character Background Shading
        var shadings = body.Descendants<W.Shading>().Where(s => s.Fill?.Value != "auto").ToList();
        Assert.Contains(shadings, s => s.Fill?.Value == "FF5733" && s.Val?.Value == W.ShadingPatternValues.Clear);
        Assert.Contains(shadings, s => s.Fill?.Value == "3399FF" && s.Val?.Value == W.ShadingPatternValues.Clear);

        // 7. Verify 0 OpenXML Validation Errors
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        var errors = validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
            .ToList();

        if (errors.Count > 0)
        {
            var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
            Assert.Fail($"OpenXML Validation failed for sample_track_changes_and_styles.docx with {errors.Count} errors:\n{msg}");
        }
    }
}


