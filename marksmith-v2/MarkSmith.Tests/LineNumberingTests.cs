using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class LineNumberingTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R3 - Legal & Academic Line Numbering)
    // =========================================================================

    [Fact]
    public async Task T1_01_Default_Line_Numbering_Emits_lnNumType()
    {
        var md = ":::line-numbers\n\n# Legal Brief\n\n1. Plaintiff asserts claims.\n2. Defendant denies allegations.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Custom_CountBy_Interval()
    {
        var md = ":::line-numbers count-by=5\n\n# Court Petition\n\nParagraph 1\n\nParagraph 2\n\nParagraph 3";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("w:countBy=\"5\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Restart_Per_Page_Mode()
    {
        var md = ":::line-numbers restart=\"per-page\" count-by=1\n\n# Deposition Transcript\n\nQ: State your name.\nA: Jane Doe.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("w:restart=\"newPage\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_Restart_Continuous_Mode()
    {
        var md = ":::line-numbers restart=\"continuous\" count-by=10\n\n# Contract Agreement\n\nSection 1: Obligations.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("w:restart=\"continuous\"", docXml);
            Assert.Contains("w:countBy=\"10\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_05_Html_Preview_Renders_Line_Numbering_Classes()
    {
        var md = ":::line-numbers count-by=5\n\n# Patent Application\n\nClaim 1: An apparatus comprising...";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("line-number", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Claim 1", html);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R3)
    // =========================================================================

    [Fact]
    public async Task T2_01_CountBy_Every_Single_Line()
    {
        var md = ":::line-numbers count-by=1\n\nLine 1\n\nLine 2\n\nLine 3";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("w:countBy=\"1\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Large_CountBy_Interval()
    {
        var md = ":::line-numbers count-by=50\n\n# Long Manuscript\n\nNumbered every 50 lines.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("w:countBy=\"50\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Invalid_Or_Negative_CountBy_Clamped()
    {
        var md = ":::line-numbers count-by=-5\n\n# Clamped Test\n\nNegative count-by should default safely.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Multi_Page_Document_With_Headings_And_Quotes()
    {
        var md = @":::line-numbers count-by=5

# Chapter 1

First paragraph of legal reasoning.

> Quote from precedent case law regarding jurisdiction.

## Section 1.1

Detailed factual analysis and statutory interpretation.

| Statute | Section | Relevant Text |
| :--- | :--- | :--- |
| USC 101 | (a) | Patentable subject matter |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("<w:tbl>", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Invalid_Restart_String_Handled_Safely()
    {
        var md = ":::line-numbers restart=\"unknown_mode\" count-by=5\n\n# Fallback Test\n\nInvalid mode.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // Tier 3: Cross-Feature Interactions
    // =========================================================================

    [Fact]
    public async Task T3_01_Line_Numbering_With_Cover_Page()
    {
        var md = @":::cover-page
title: Supreme Court Amicus Brief
author: Appellate Advocacy Clinic
date: 2026-08-23
:::

:::line-numbers count-by=5 restart=""per-page""

# Table of Authorities

1. Marbury v. Madison
2. Brown v. Board of Education
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:lnNumType", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Line_Numbering_With_Parallel_Columns()
    {
        var md = @":::line-numbers count-by=5

:::parallel ""Original Language"" | ""Certified Translation""
The party of the first part agrees to the terms.
===
La partie de première part accepte les conditions.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("<w:tbl>", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
