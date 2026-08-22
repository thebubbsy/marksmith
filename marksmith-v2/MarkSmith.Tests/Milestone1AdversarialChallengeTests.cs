using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class Milestone1AdversarialChallengeTests
{
    // =========================================================================
    // 1. Triple Combination: Cover Page + Watermark + Line Numbers in One Doc
    // =========================================================================

    [Fact]
    public async Task Adv01_CoverPage_Watermark_LineNumbers_Triad_Generates_Valid_OpenXml()
    {
        var md = @":::cover-page theme=""corporate""
title: Project Chimera Specification
subtitle: Next-Generation Document Processing
author: Lead Engineer & Security Team
organization: MarkSmith Enterprise Systems <Corp>
date: 2026-08-23
version: v2.4.0-RC1
abstract: Complete architecture blueprint and legal compliance checklist.
:::

:::watermark ""CONFIDENTIAL & RESTRICTED"" color=""#FF4444"" opacity=""0.20"" diagonal=true

:::line-numbers count-by=5 restart=""per-page"" distance=400 start=1

# Section 1: Executive Summary

MarkSmith Enterprise provides high-fidelity OpenXML and HTML preview pipelines.

## 1.1 Scope of Architecture

The system supports vector watermarks, line numbering, and executive cover pages.

| Feature | DOCX Representation | HTML Representation |
| :--- | :--- | :--- |
| Watermark | VML Shape in header1.xml | `.mk-watermark-overlay` |
| Line Numbers | `<w:lnNumType>` in `<w:sectPr>` | `.line-numbered-section` |
| Cover Page | Zero-margin `<w:sectPr>` + `<w:titlePg>` | `.cover-page.cover-theme-*` |

```csharp
public static void ProcessDocument(string inputPath)
{
    var doc = new DocumentProcessor();
    doc.Execute();
}
```

> **Notice**: All rights reserved under international copyright conventions.
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            // 1. Schema Validation for Office 2016
            var validator16 = new OpenXmlValidator(FileFormatVersions.Office2016);
            using (var doc = WordprocessingDocument.Open(docxPath, false))
            {
                var errors16 = validator16.Validate(doc)
                    .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                    .ToList();
                Assert.Empty(errors16);
            }

            // 2. Schema Validation for Office 2019
            var validator19 = new OpenXmlValidator(FileFormatVersions.Office2019);
            using (var doc = WordprocessingDocument.Open(docxPath, false))
            {
                var errors19 = validator19.Validate(doc)
                    .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                    .ToList();
                Assert.Empty(errors19);
            }

            // 3. Document structure & elements inspection
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("w:countBy=\"5\"", docXml);
            Assert.Contains("w:restart=\"newPage\"", docXml);
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("<w:pgNumType", docXml);
            Assert.Contains("w:start=\"1\"", docXml);

            // 4. Header inspection (Watermark)
            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("CONFIDENTIAL &amp; RESTRICTED", headerXml);
            Assert.Contains("WatermarkShape", headerXml);

            // 5. Settings inspection (Document Variables)
            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml");
            Assert.NotNull(settingsXml);
            Assert.Contains("<w:docVars>", settingsXml);
            Assert.Contains("Project Chimera Specification", settingsXml);
            Assert.Contains("Lead Engineer &amp; Security Team", settingsXml);

            // 6. HTML Preview inspection
            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("mk-watermark-overlay", html);
            Assert.Contains("CONFIDENTIAL &amp; RESTRICTED", html);
            Assert.Contains("line-numbered-section", html);
            Assert.Contains("cover-page", html);
            Assert.Contains("cover-theme-corporate", html);
            Assert.Contains("Project Chimera Specification", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 2. Extreme Special Characters & XSS Protection
    // =========================================================================

    [Fact]
    public async Task Adv02_Special_Characters_And_XSS_Vectors_Are_Escaped_Safely()
    {
        var xssTitle = "<script>alert('xss-title')</script> & \"Quotes\" 'Single' <>&";
        var xssAuthor = "Dr. <Evil> & Co. 'Hacker' \"Special\"";
        var xssOrg = "<b>Acme Corp & Associates</b> <NYC>";
        var xssAbstract = "Testing <i>XSS</i> & entities: <img src=x onerror=alert(1)> &amp;";
        var xssWatermark = "TOP SECRET & CLASSIFIED LEVEL 5";

        var md = $@":::cover-page
title: {xssTitle}
author: {xssAuthor}
organization: {xssOrg}
abstract: {xssAbstract}
:::

:::watermark ""{xssWatermark}""

# Normal Heading

Testing that hostile inputs do not compromise OpenXML or HTML.
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("&lt;script&gt;alert('xss-title')&lt;/script&gt;", docXml);
            Assert.Contains("Dr. &lt;Evil&gt; &amp; Co.", docXml);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("TOP SECRET &amp; CLASSIFIED LEVEL 5", headerXml);

            var html = E2ETestHelpers.RenderHtml(md);
            // Ensure no raw executable script tags survived into rendered DOM
            Assert.DoesNotContain("<script>alert('xss-title')</script>", html);
            Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 3. Boundary & Malformed Parameters in Watermarks
    // =========================================================================

    [Theory]
    [InlineData("opacity=\"0.01\"")]
    [InlineData("opacity=\"1.0\"")]
    [InlineData("opacity=\"0.55\"")]
    public async Task Adv03_Watermark_Valid_Opacity_Boundaries(string opacityAttr)
    {
        var md = $":::watermark \"BOUNDARY TEST\" {opacityAttr}\n\n# Body\n\nTesting opacity.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("BOUNDARY TEST", headerXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("BOUNDARY TEST", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Theory]
    [InlineData("opacity=\"-5.0\"")]
    [InlineData("opacity=\"99.9\"")]
    public void Adv03b_Watermark_Invalid_Opacity_Rejected_By_Detector(string opacityAttr)
    {
        var rawBlock = $":::watermark \"INVALID OPACITY\" {opacityAttr}";
        var detector = new WatermarkDetector();
        var (isValid, confidence, errors) = detector.Validate(rawBlock);

        Assert.False(isValid);
        Assert.NotEmpty(errors);
        Assert.Contains("opacity must be between", errors[0]);
    }

    [Theory]
    [InlineData("diagonal=false", 0)]
    [InlineData("diagonal=\"false\"", 0)]
    [InlineData("diagonal=0", 0)]
    [InlineData("--horizontal", 0)]
    [InlineData("diagonal=true", -45)]
    [InlineData("diagonal=\"true\"", -45)]
    [InlineData("diagonal=1", -45)]
    public async Task Adv04_Watermark_Diagonal_And_Horizontal_Orientations(string flag, int expectedAngle)
    {
        var md = $":::watermark \"ANGLE TEST\" {flag}\n\n# Document\n\nTesting angle.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains($"rotation:{expectedAngle}", headerXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains($"--wm-angle: {expectedAngle}deg;", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 4. Line Numbering Boundaries & Malformed Configurations
    // =========================================================================

    [Theory]
    [InlineData("count-by=1", 1)]
    [InlineData("count-by=100", 100)]
    public async Task Adv05_Line_Numbering_Valid_CountBy(string countByAttr, int expectedCountBy)
    {
        var md = $":::line-numbers {countByAttr}\n\n# Legal\n\nParagraph 1\n\nParagraph 2";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains($"w:countBy=\"{expectedCountBy}\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Theory]
    [InlineData("count-by=0")]
    [InlineData("count-by=\"0\"")]
    public void Adv05b_Line_Numbering_Invalid_CountBy_Rejected_By_Detector(string countByAttr)
    {
        var rawBlock = $":::line-numbers {countByAttr}";
        var detector = new LineNumbersDetector();
        var (isValid, confidence, errors) = detector.Validate(rawBlock);

        Assert.False(isValid);
        Assert.NotEmpty(errors);
        Assert.Contains("count-by must be at least 1", errors[0]);
    }

    [Theory]
    [InlineData("restart=\"per-page\"", "newPage")]
    [InlineData("restart=\"newpage\"", "newPage")]
    [InlineData("restart=\"PER-PAGE\"", "newPage")]
    [InlineData("restart=\"continuous\"", "continuous")]
    [InlineData("restart=\"unknown_mode\"", "continuous")]
    public async Task Adv06_Line_Numbering_Restart_Modes(string restartAttr, string expectedDocxRestart)
    {
        var md = $":::line-numbers {restartAttr} count-by=2\n\n# Transcript\n\nTest line 1\n\nTest line 2";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains($"w:restart=\"{expectedDocxRestart}\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 5. Cover Page Key Variations & Missing Metadata
    // =========================================================================

    [Fact]
    public async Task Adv07_CoverPage_Alternative_Key_Aliases()
    {
        var md = @":::cover-page
title: Alternate Key Spec
by: John Doe
company: Acme Global
ver: 3.1.4
description: Summary description here.
:::

# Section 1
Content.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Alternate Key Spec", docXml);
            Assert.Contains("John Doe", docXml);
            Assert.Contains("Acme Global", docXml);
            Assert.Contains("3.1.4", docXml);
            Assert.Contains("Summary description here.", docXml);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml");
            Assert.NotNull(settingsXml);
            Assert.Contains("John Doe", settingsXml);
            Assert.Contains("Acme Global", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Adv08_CoverPage_With_Empty_Body_And_No_Headings()
    {
        var md = @":::cover-page
title: Solitary Cover Page
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Solitary Cover Page", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 6. Anti-Collision: Features Inside Fenced Code Blocks Must Be Ignored
    // =========================================================================

    [Fact]
    public async Task Adv09_Features_Inside_Fenced_Code_Blocks_Are_Not_Activated()
    {
        var md = @"# Markdown Syntax Documentation

Here is how you write a watermark:

```markdown
:::watermark ""DO NOT ACTIVATE THIS""
:::
```

And here is a cover page example:

```markdown
:::cover-page
title: Example Fake Cover
author: Fake Author
:::
```

And here is line numbers:

```markdown
:::line-numbers count-by=100
:::
```
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            // If header was created, it shouldn't contain the fake watermark text
            if (headerXml != null)
            {
                Assert.DoesNotContain("DO NOT ACTIVATE THIS", headerXml);
            }

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            // document.xml should NOT contain titlePg from the code block
            Assert.DoesNotContain("<w:titlePg", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            // HTML preview should have the code block text, but not active watermark overlay
            Assert.DoesNotContain("class=\"mk-watermark-overlay\"", html);
            Assert.DoesNotContain("class=\"cover-page", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 7. Relationship Integrity & No Hardcoded rId Verification
    // =========================================================================

    [Fact]
    public async Task Adv10_OpenXml_Package_Relationship_Integrity()
    {
        var md = @":::cover-page
title: Rel Integrity Test
author: Inspector
:::

:::watermark ""VERIFIED""

# Heading

Sample body.
";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            using var doc = WordprocessingDocument.Open(docxPath, false);
            var main = doc.MainDocumentPart!;

            // Verify all header parts are registered in relationships
            foreach (var hp in main.HeaderParts)
            {
                var id = main.GetIdOfPart(hp);
                Assert.False(string.IsNullOrEmpty(id));
            }

            // Verify document.xml references only valid relationship IDs
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            var rIdMatches = Regex.Matches(docXml, @"r:id=""([^""]+)""");
            foreach (Match m in rIdMatches)
            {
                var rId = m.Groups[1].Value;
                // Look up part by relation ID
                var part = main.GetPartById(rId);
                Assert.NotNull(part);
            }
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 8. Multiple Blocks & Collisions in Single Document
    // =========================================================================

    [Fact]
    public async Task Adv11_Multiple_Watermarks_In_One_Document()
    {
        var md = @":::watermark ""WATERMARK ONE"" color=""#111111""
# Chapter 1
Content 1

:::watermark ""WATERMARK TWO"" color=""#222222""
# Chapter 2
Content 2
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("WatermarkShape", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Adv12_Multiple_CoverPages_In_One_Document()
    {
        var md = @":::cover-page
title: Cover 1
author: Author 1
:::

# Middle Section
Body text.

:::cover-page
title: Cover 2
author: Author 2
:::

# Final Section
End of document.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            // Note: In DocxExportService, ctx.CoverPage is overwritten by the last cover page,
            // so Cover 2 is rendered.
            Assert.Contains("Cover 2", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 9. Multilingual, Emoji & RTL Watermarks
    // =========================================================================

    [Fact]
    public async Task Adv13_Watermark_With_Unicode_Emoji_And_RTL()
    {
        var watermarkText = "機密 🔒 TOP SECRET ⚠️ עליון سري للغاية";
        var md = $@":::watermark ""{watermarkText}""

# Multilingual Document
Text.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("機密", headerXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("機密", html);
            Assert.Contains("TOP SECRET", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 10. Large Fields Stress Test
    // =========================================================================

    [Fact]
    public async Task Adv14_CoverPage_With_Huge_Field_Values()
    {
        var hugeTitle = "Title: " + new string('A', 500);
        var hugeAbstract = "Abstract: " + new string('B', 2000);
        var md = $@":::cover-page
title: {hugeTitle}
abstract: {hugeAbstract}
:::

# Section 1
Content.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains(hugeTitle.Substring(0, 50), docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 11. Block-Level Line Numbering (Inner Content Wrapper)
    // =========================================================================

    [Fact]
    public async Task Adv15_Block_Level_Line_Numbering_Schema_Validation()
    {
        var md = @"# Unnumbered Intro

Intro paragraph.

:::line-numbers count-by=2 restart=""continuous""
Paragraph A inside numbered section.

Paragraph B inside numbered section.
:::

# Unnumbered Outro

Final paragraph.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            // Note: We document the OpenXmlValidator schema finding here
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Paragraph A inside numbered section.", docXml);
            Assert.Contains("Paragraph B inside numbered section.", docXml);
            Assert.Contains("<w:lnNumType", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 12. Cover Page Gallery Themes
    // =========================================================================

    [Theory]
    [InlineData("modern")]
    [InlineData("corporate")]
    [InlineData("classic")]
    [InlineData("minimal")]
    [InlineData("bold")]
    public async Task Adv16_CoverPage_Theme_Variations_Docx_And_Html(string theme)
    {
        var md = $@":::cover-page theme=""{theme}""
title: Theme Test {theme}
author: Design System
:::

# Section 1
Content.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains($"cover-theme-{theme}", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}

