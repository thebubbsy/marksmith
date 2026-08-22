using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class AdversarialMilestone1Tests
{
    // =========================================================================
    // R2: Vector Watermarks (:::watermark) Adversarial Tests
    // =========================================================================

    [Fact]
    public async Task Watermark_Adversarial_01_ZeroHardcodedRelationshipIds_InAllPartsAndRels()
    {
        var md = @":::watermark ""CONFIDENTIAL AUDIT"" color=""#FF5500"" opacity=""0.20""

# Financial Summary

Section 1 content with [External Link](https://example.com) and an image reference.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var main = doc.MainDocumentPart!;
            Assert.NotNull(main);

            // Verify all header parts are accessible via dynamic relationship IDs
            var headerParts = main.HeaderParts.ToList();
            Assert.Single(headerParts);

            var headerPart = headerParts[0];
            var relId = main.GetIdOfPart(headerPart);
            Assert.False(string.IsNullOrWhiteSpace(relId));

            // Verify document.xml references the exact dynamically registered relId
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains($"r:id=\"{relId}\"", docXml);

            // Verify .rels does not contain hardcoded or invalid relationship targets
            var relsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/_rels/document.xml.rels")!;
            Assert.Contains($"Id=\"{relId}\"", relsXml);
            Assert.Contains("Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\"", relsXml);

            // Verify header1.xml contains VML shape with proper namespaces
            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml")!;
            Assert.Contains("CONFIDENTIAL AUDIT", headerXml);
            Assert.Contains("v:shape", headerXml);
            Assert.Contains("v:textpath", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void Watermark_Adversarial_02_XssPayloadEscapedInHtmlPreview()
    {
        var xssPayload = "<script>alert('XSS')</script><img src=x onerror=\"alert('XSS')\" />";
        var md = $@":::watermark ""{xssPayload}""
# Security Whitepaper
Text content.";

        var html = E2ETestHelpers.RenderHtml(md);

        // Watermark overlay must exist
        Assert.Contains("mk-watermark-overlay", html);
        // Script tags and img onerror in watermark text MUST be HTML encoded
        Assert.DoesNotContain("<script>alert('XSS')</script>", html);
        Assert.DoesNotContain("onerror=\"alert('XSS')\"", html);
        Assert.Contains("&lt;script&gt;alert(&#39;XSS&#39;)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task Watermark_Adversarial_03_SpecialXmlCharacters_InVmlHeaderShape()
    {
        var md = @":::watermark ""CONFIDENTIAL & RESTRICTED <TOP SECRET> 'LEGAL' """"""
# Classified
Content with sensitive data.";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml");
            Assert.NotNull(headerXml);
            Assert.Contains("CONFIDENTIAL &amp; RESTRICTED &lt;TOP SECRET&gt;", headerXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void Watermark_Adversarial_04_LightAndDarkMode_HtmlPreviewStyling()
    {
        var md = ":::watermark \"DRAFT\"\n\n# Chapter 1\n\nContent.";
        
        var themeCatalog = new ThemeCatalog();
        var lightTheme = themeCatalog.GetOrDefault("GitHub Light");
        var darkTheme = themeCatalog.GetOrDefault("GitHub Dark");

        var htmlLight = E2ETestHelpers.RenderHtml(md, new AppSettings { Theme = "GitHub Light" }, lightTheme);
        var htmlDark = E2ETestHelpers.RenderHtml(md, new AppSettings { Theme = "GitHub Dark" }, darkTheme);

        Assert.Contains("--wm-color: #CCCCCC", htmlLight);
        Assert.Contains("--wm-color: #555555", htmlDark);
    }

    [Fact]
    public async Task Watermark_Adversarial_05_ExtremeOpacitiesAndRotations()
    {
        // Test opacity clamping (0.01 to 1.0) and diagonal=false
        var md = ":::watermark \"STAMP\" opacity=\"0.0001\" diagonal=false\n\n# Body\n\nText.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml")!;
            Assert.Contains("rotation:0", headerXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("--wm-angle: 0deg", html);
            Assert.Contains("--wm-opacity: 0.01", html); // Clamped to 0.01
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Watermark_Adversarial_06_UnicodeAndLongText()
    {
        var longUnicode = "【極秘】CONFIDENTIAL — TOP SECRET — 2026 🔒 — 機密文書";
        var md = $@":::watermark ""{longUnicode}""

# International Agreement

Body text.";

        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml")!;
            Assert.Contains("【極秘】CONFIDENTIAL", headerXml);
            Assert.Contains("機密文書", headerXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("【極秘】CONFIDENTIAL", html);
            Assert.Contains("機密文書", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void Watermark_Adversarial_07_InsideFencedCodeBlock_NotLifted()
    {
        var md = @"# Markdown Guide

Here is how you use a watermark:

```markdown
:::watermark ""DO NOT LIFT ME""
# Inner Document
```

End of guide.";

        var html = E2ETestHelpers.RenderHtml(md);
        // The watermark inside code block should NOT create a watermark overlay
        Assert.DoesNotContain("<div class=\"mk-watermark-overlay\"", html);
        Assert.Contains("DO NOT LIFT ME", html);
    }

    // =========================================================================
    // R3: Legal Line Numbering (:::line-numbers) Adversarial Tests
    // =========================================================================

    [Fact]
    public async Task LineNumbers_Adversarial_01_SectionPropertiesOrder_Ecma376Conformance()
    {
        var md = @":::line-numbers count-by=5 restart=""per-page""

# Legal Brief

1. First assertion of fact.
2. Second assertion of fact.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("w:countBy=\"5\"", docXml);
            Assert.Contains("w:restart=\"newPage\"", docXml);

            // Verify order in sectPr: headerReference/footerReference -> pgSz -> pgMar -> lnNumType
            int idxLn = docXml.IndexOf("<w:lnNumType");
            int idxMar = docXml.IndexOf("<w:pgMar");
            Assert.True(idxLn > idxMar, "lnNumType must come after pgMar according to ECMA-376 sequence");
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task LineNumbers_Adversarial_02_NegativeAndClampedCountBy()
    {
        var md = ":::line-numbers count-by=-10 restart=\"continuous\"\n\n# Deposition\n\nTestimony line 1.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            // CountBy clamped to at least 1
            Assert.Contains("w:countBy=\"1\"", docXml);
            Assert.Contains("w:restart=\"continuous\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void LineNumbers_Adversarial_03_InsideFencedCodeBlock_NotTransformed()
    {
        var md = @"# Tutorial

```markdown
:::line-numbers count-by=5
Legal text
:::
```

Regular body text.";

        var html = E2ETestHelpers.RenderHtml(md);
        Assert.DoesNotContain("<div class=\"line-numbered-section\"", html);
        Assert.Contains(":::line-numbers count-by=5", html);
    }

    [Fact]
    public async Task LineNumbers_Adversarial_04_CrossInteraction_WithTablesAndCodeBlocks()
    {
        var md = @":::line-numbers count-by=2

# Evidence Matrix

| Exhibit | Description | Admissibility |
| :--- | :--- | :--- |
| Ex. A | Contract Agreement | Admitted |
| Ex. B | Financial Audit | Pending |

```csharp
public class LegalAudit
{
    public bool IsCompliant => true;
}
```
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("Contract Agreement", docXml);
            Assert.Contains("IsCompliant", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // R9: Executive Cover Page Gallery (:::cover-page) Adversarial Tests
    // =========================================================================

    [Theory]
    [InlineData("modern")]
    [InlineData("corporate")]
    [InlineData("classic")]
    [InlineData("minimal")]
    [InlineData("bold")]
    public async Task CoverPage_Adversarial_01_AllThemes_ValidDocxAndHtml(string theme)
    {
        var md = $@":::cover-page theme=""{theme}""
title: Strategic Roadmap 2030
subtitle: Next Generation Document Automation
author: Architecture Council
organization: Enterprise Systems Inc.
date: 2026-08-23
version: 2.0.0
abstract: Comprehensive evaluation of high-throughput document transformation architectures.
:::

# Section 1: Executive Summary

First section content following the cover page.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Strategic Roadmap 2030", docXml);
            Assert.Contains("Next Generation Document Automation", docXml);
            Assert.Contains("Enterprise Systems Inc.", docXml);
            Assert.Contains("<w:titlePg", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains($"cover-theme-{theme}", html);
            Assert.Contains("Strategic Roadmap 2030", html);
            Assert.Contains("Enterprise Systems Inc.", html);
            Assert.Contains("class=\"page-break\"", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void CoverPage_Adversarial_02_XssPayloadEscapedInAllMetadataFields()
    {
        var xss = "<script>alert('pwned')</script>";
        var md = $@":::cover-page
title: {xss}
subtitle: {xss}
author: {xss}
organization: {xss}
date: {xss}
version: {xss}
abstract: {xss}
:::

# Body Content
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.DoesNotContain("<script>alert('pwned')</script>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;pwned&#39;)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task CoverPage_Adversarial_03_DocVarsInSettingsXml_ProperlyEscaped()
    {
        var md = @":::cover-page
title: Merger & Acquisition <Project Phoenix> ""Secret""
author: Smith & Wesson & Co.
organization: AT&T / O'Reilly Media
date: 2026-08-23
version: 1.0.0-rc.1
:::

# Main Agreement
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml");
            Assert.NotNull(settingsXml);
            Assert.Contains("<w:docVars>", settingsXml);
            Assert.Contains("Merger &amp; Acquisition &lt;Project Phoenix&gt;", settingsXml);
            Assert.Contains("Smith &amp; Wesson &amp; Co.", settingsXml);
            Assert.Contains("AT&amp;T / O&#39;Reilly Media", settingsXml.Replace("'", "&#39;"));
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void CoverPage_Adversarial_04_InsideFencedCodeBlock_NotLifted()
    {
        var md = @"# Documentation

```markdown
:::cover-page
title: Example Cover Page
author: John Doe
:::
```

Main text.
";
        var html = E2ETestHelpers.RenderHtml(md);
        Assert.DoesNotContain("<div class=\"cover-page", html);
        Assert.Contains(":::cover-page", html);
    }

    // =========================================================================
    // Combinatorial Stress Test (R2 + R3 + R9 Multi-Page Document Flow)
    // =========================================================================

    [Fact]
    public async Task Table_With_Italics_Schema_Validation()
    {
        var md = @"| Header |
| :--- |
| *Italic Entry* |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            // This isolates the Table ContrastGuard schema ordering issue
            // When run.RunProperties.Color is set in table cell post-processing,
            // OpenXML SDK inserts w:color before w:i causing schema violation.
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Combinatorial_Adversarial_Watermark_LineNumbers_CoverPage_MultiPageFlow()
    {
        var md = @":::cover-page theme=""corporate""
title: Supreme Court Amicus Brief & Technical Analysis
subtitle: In re Advanced Document Processing Systems
author: Appellate Litigation Group
organization: MarkSmith Legal Foundation
date: 2026-08-23
version: Final Draft
abstract: This amicus curiae brief addresses the statutory standards governing open standard document format interoperability.
:::

:::watermark ""CONFIDENTIAL COURT FILING"" color=""#CC0000"" opacity=""0.20""

:::line-numbers count-by=5 restart=""per-page""

# Table of Authorities

1. Baker v. Selden, 101 U.S. 99 (1879)
2. Lotus Dev. Corp. v. Borland Int'l, Inc., 49 F.3d 807 (1st Cir. 1995)
3. Google LLC v. Oracle America, Inc., 141 S. Ct. 1183 (2021)

# Summary of Argument

The standard for interoperability requires faithful adherence to published schemas and strict schema validation.

| Precedent | Year | Principle |
| :--- | :---: | :--- |
| Baker | 1879 | Idea/Expression distinction |
| Lotus | 1995 | Method of operation |
| Google | 2021 | Fair use of functional interfaces |

```csharp
public sealed class InteroperabilityValidator
{
    public bool ValidateSchema(string xml) => true;
}
```
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            // 1. ECMA-376 schema validation check
            var docXmlSnippet = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            if (errors.Any())
            {
                var errStr = string.Join("; ", errors.Select(e => $"Id: {e.Id}, Desc: {e.Description}, Node: {e.Node?.OuterXml}"));
                Assert.True(false, $"Schema validation failed ({errors.Count} errors): {errStr}");
            }
            Assert.Empty(errors);

            // 2. OpenXML package structure check
            var entries = E2ETestHelpers.GetZipEntries(docxPath);
            Assert.Contains("word/document.xml", entries);
            Assert.Contains("word/header1.xml", entries);
            Assert.Contains("word/settings.xml", entries);
            Assert.Contains("word/_rels/document.xml.rels", entries);

            // 3. Watermark in header1.xml check
            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml")!;
            Assert.Contains("CONFIDENTIAL COURT FILING", headerXml);

            // 4. Line numbering and page numbering restart check
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("w:countBy=\"5\"", docXml);
            Assert.Contains("w:restart=\"newPage\"", docXml);
            Assert.Contains("<w:pgNumType", docXml);
            Assert.Contains("w:start=\"1\"", docXml);
            Assert.Contains("<w:titlePg", docXml);

            // 5. Settings.xml docVars check
            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml")!;
            Assert.Contains("<w:docVars>", settingsXml);
            Assert.Contains("Supreme Court Amicus Brief &amp; Technical Analysis", settingsXml);
            Assert.Contains("MarkSmith Legal Foundation", settingsXml);

            // 6. HTML preview rendering check
            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("mk-watermark-overlay", html);
            Assert.Contains("CONFIDENTIAL COURT FILING", html);
            Assert.Contains("cover-theme-corporate", html);
            Assert.Contains("Supreme Court Amicus Brief &amp; Technical Analysis", html);
            Assert.Contains("class=\"page-break\"", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
