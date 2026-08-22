using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class TrackChangesAndCommentsTests
{
    // =========================================================================
    // Tier 1: Feature Coverage (R5 - Track Changes & Reviewer Comments)
    // =========================================================================

    [Fact]
    public async Task T1_01_Inline_Deletion_Maps_To_w_del_With_delText()
    {
        var md = "This is the {--outdated and obsolete--} proposal.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:delText", docXml);
            Assert.Contains("outdated and obsolete", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_02_Inline_Addition_Maps_To_w_ins_With_wt()
    {
        var md = "This is the {++modern state-of-the-art++} architecture.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("<w:t", docXml);
            Assert.Contains("modern state-of-the-art", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_03_Reviewer_Comment_Creates_CommentsXml_And_Reference()
    {
        var md = "The SLA requires 99.999% uptime.^[Alice: \"Please verify with infrastructure team.\"]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:commentRangeStart", docXml);
            Assert.Contains("<w:commentRangeEnd", docXml);
            Assert.Contains("<w:commentReference", docXml);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml");
            Assert.NotNull(commentsXml);
            Assert.Contains("Alice", commentsXml);
            Assert.Contains("Please verify with infrastructure team.", commentsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T1_04_Track_Revisions_Setting_Enabled_In_SettingsXml()
    {
        var md = "Original text {--removed--}{++added++}.^[Bob: \"Nice edit\"]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml");
            Assert.NotNull(settingsXml);
            Assert.Contains("<w:trackRevisions", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void T1_05_Html_Preview_Renders_Ins_Del_And_Comment_Anchor()
    {
        var md = "We propose {--old plan--}{++new plan++}.^[Charlie: \"Approved by leadership\"]";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("<del", html);
        Assert.Contains("<ins", html);
        Assert.Contains("old plan", html);
        Assert.Contains("new plan", html);
        Assert.Contains("Charlie", html);
    }

    // =========================================================================
    // Tier 2: Boundary & Corner Cases (R5)
    // =========================================================================

    [Fact]
    public async Task T2_01_Adjacent_Addition_And_Deletion_Syntax()
    {
        var md = "Refactored {--sync method--}{++async Task method++} seamlessly.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_02_Comment_With_Quotes_And_Special_Characters()
    {
        var md = "Configuration parameter `timeout_ms`.^[DevOps Lead: \"Must be <= 5000 & >= 100 per RFC-8259.\"]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml");
            Assert.NotNull(commentsXml);
            Assert.Contains("&lt;= 5000 &amp; &gt;= 100", commentsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_03_Multiple_Reviewer_Comments_With_Unique_Ids()
    {
        var md = @"# Multi-Reviewer Document

Paragraph one text.^[ReviewerA: ""First observation.""]

Paragraph two text.^[ReviewerB: ""Second observation.""]

Paragraph three text.^[ReviewerC: ""Third observation.""]
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("ReviewerA", commentsXml);
            Assert.Contains("ReviewerB", commentsXml);
            Assert.Contains("ReviewerC", commentsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_04_Deletion_With_Punctuation_And_Special_Marks()
    {
        var md = "Sentence one. {--Sentence two with (parentheses), \"quotes\", and dashes!--} Sentence three.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Sentence two with (parentheses)", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T2_05_Comments_Preserve_Author_And_Date()
    {
        var md = "Key metric.^[Lead Architect (2026-08-23): \"Confirmed with benchmarks.\"]";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Lead Architect", commentsXml);
            Assert.Contains("Confirmed with benchmarks.", commentsXml);
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
    public async Task T3_01_Track_Changes_Inside_Tables()
    {
        var md = @"| Component | Version | Status |
| :--- | :--- | :--- |
| Core Engine | {--1.9.4--}{++2.0.0++} | Active |
| Storage Provider | 3.2.0 | {--Pending--}{++Verified++} |
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task T3_02_Reviewer_Comments_Inside_Parallel_Columns()
    {
        var md = @":::parallel ""English"" | ""Français""
The party indemnifies the supplier.^[Legal Counsel: ""Standard warranty indemnity clause.""]
===
La partie indemnise le fournisseur.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:tbl>", docXml);
            Assert.Contains("<w:commentRangeStart", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
