using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class CoverPageTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R9 - Executive Cover Page Gallery)
    // =========================================================================

    [Fact]
    public async Task T1_01_Cover_Page_Metadata_Emitted_In_Zero_Margin_Section()
    {
        var md = @":::cover-page theme=""modern""
title: Project Hyperion System Design
subtitle: Enterprise OpenXML Pipeline
author: Dr. Jane Doe
organization: MarkSmith Technologies
date: 2026-08-23
abstract: High-performance document processing system.
:::

# Section 1: Introduction

Body content begins on page 2.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Project Hyperion System Design", docXml);
            Assert.Contains("Enterprise OpenXML Pipeline", docXml);
            Assert.Contains("Dr. Jane Doe", docXml);
            Assert.Contains("MarkSmith Technologies", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Cover_Page_Emits_TitlePg_And_SectionBreak()
    {
        var md = @":::cover-page
title: Architecture Whitepaper
author: Chief Architect
:::

# Main Body
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:sectPr", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Body_Section_Restarts_Page_Numbering_At_Page_1()
    {
        var md = @":::cover-page
title: Annual Report
date: 2026-08-23
:::

# Executive Overview

First body page.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:pgNumType", docXml);
            Assert.Contains("w:start=\"1\"", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_Document_Variables_Written_In_Settings_Xml()
    {
        var md = @":::cover-page
title: Cloud Migration Blueprint
author: DevOps Lead
organization: Acme Global
:::

# Executive Summary
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml");
            Assert.NotNull(settingsXml);
            Assert.Contains("<w:docVars>", settingsXml);
            Assert.Contains("Cloud Migration Blueprint", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_05_Html_Preview_Renders_Cover_Hero_Card_And_PageBreak()
    {
        var md = @":::cover-page theme=""corporate""
title: Strategy 2030
subtitle: Global Expansion
author: Board of Directors
:::

# Roadmap
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("cover-page", html);
        Assert.Contains("Strategy 2030", html);
        Assert.Contains("Global Expansion", html);
        Assert.Contains("Board of Directors", html);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R9)
    // =========================================================================

    [Fact]
    public async Task T2_01_Minimal_Cover_Page_Only_Title()
    {
        var md = @":::cover-page
title: Minimalist Document
:::

# Content
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Minimalist Document", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Cover_Page_With_Special_Xml_Characters()
    {
        var md = @":::cover-page
title: R&D & Innovation <Year 2026> ""Proprietary""
author: Smith & Wesson & Co.
organization: AT&T / O'Reilly Media
:::

# Overview
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("R&amp;D &amp; Innovation &lt;Year 2026&gt;", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Theory]
    [InlineData("modern")]
    [InlineData("corporate")]
    [InlineData("classic")]
    [InlineData("minimal")]
    [InlineData("bold")]
    public async Task T2_03_Cover_Page_Themes(string theme)
    {
        var md = $@":::cover-page theme=""{theme}""
title: Theme Evaluation
author: QA Team
:::

# Chapter 1
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains(theme, html, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Cover_Page_With_Long_Abstract()
    {
        var abstractText = "This whitepaper examines the architectural trade-offs in high-throughput document transformation systems. We demonstrate how streaming SAX emitters achieve constant-space memory overhead while maintaining strict ECMA-376 schema conformance.";
        var md = $@":::cover-page
title: High-Throughput OpenXML Architecture
abstract: {abstractText}
:::

# Introduction
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("streaming SAX emitters", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Cover_Page_Followed_By_Immediate_Headings_And_Body()
    {
        var md = @":::cover-page
title: Fast Track Report
:::
# Heading 1
Paragraph directly following cover.
## Subheading
More text.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Fast Track Report", docXml);
            Assert.Contains("Paragraph directly following cover.", docXml);
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
    public async Task T3_01_Cover_Page_With_Watermark_And_Footers()
    {
        var md = @":::cover-page
title: Confidential Audit
:::

:::watermark ""AUDIT IN PROGRESS""

# Section 1: Findings

Body text with watermark.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:headerReference", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Cover_Page_With_TableOfContents_And_LineNumbers()
    {
        var md = @":::cover-page
title: Legal Treatise
author: Senior Counsel
:::

:::line-numbers count-by=5

# Table of Authorities

1. United States Code Title 35
2. Federal Rules of Civil Procedure
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
}
