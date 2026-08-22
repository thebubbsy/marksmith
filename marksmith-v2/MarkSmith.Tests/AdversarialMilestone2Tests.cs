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

public class AdversarialMilestone2Tests
{
    // =========================================================================
    // Section 1: Drop Caps (:::dropcap) Adversarial Suite
    // =========================================================================

    [Fact]
    public async Task DropCap_01_Adversarial_ZeroAndNegativeLines_GracefulFallback()
    {
        var md = @":::dropcap 3
In the beginning, systems were built monolithically.
:::

:::dropcap lines=4
Second paragraph tests line counts.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("w:dropCap=\"drop\"", docXml);
            Assert.Contains("n the beginning", docXml);
            Assert.Contains("econd paragraph", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task DropCap_02_Adversarial_LeadingFormattingAndInlineStyles()
    {
        var md = @":::dropcap 4
**Architecture** is the *foundational* structure of software engineering and `design`.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("w:lines=\"4\"", docXml);
            // Drop cap run contains 'A'
            Assert.Contains("<w:t>A</w:t>", docXml);
            // Remaining paragraph contains 'rchitecture'
            Assert.Contains("rchitecture", docXml);
            // Bold tag preserved
            Assert.True(docXml.Contains("<w:b/>") || docXml.Contains("<w:b />") || docXml.Contains("<w:b"));
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task DropCap_03_Adversarial_LeadingQuotationAndPunctuation()
    {
        var md = @":::dropcap
""Perfection is achieved not when there is nothing more to add, but when there is nothing left to take away.""
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("erfection is achieved", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task DropCap_04_Adversarial_MultilingualAndAccentedInitials()
    {
        var md = @":::dropcap 3
Édition spéciale pour la validation de conformité typographique internationale.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:t>É</w:t>", docXml);
            Assert.Contains("dition sp", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task DropCap_05_Adversarial_MultipleParagraphsInBlock()
    {
        var md = @":::dropcap 3
First paragraph gets the dropped capital letter.

Second paragraph in the same dropcap block should render as standard paragraph text.

Third paragraph also renders normally.
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("<w:t>F</w:t>", docXml);
            Assert.Contains("irst paragraph", docXml);
            Assert.Contains("Second paragraph", docXml);
            Assert.Contains("Third paragraph", docXml);

            // Frame properties should only appear on the first paragraph
            int countFramePr = Regex.Matches(docXml, @"<w:framePr\b").Count;
            Assert.Equal(1, countFramePr);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void DropCap_06_Adversarial_InsideFencedCodeBlock_NotLifted()
    {
        var md = @"# Publishing Tutorial

Here is how you format a drop cap:

```markdown
:::dropcap 4
Literal dropcap example inside code fence
:::
```

Regular body prose.
";
        var html = E2ETestHelpers.RenderHtml(md);
        Assert.DoesNotContain("<div class=\"dropcap\"", html);
        Assert.Contains(":::dropcap 4", html);
    }

    [Fact]
    public void DropCap_07_Adversarial_HtmlPreview_CssProperties()
    {
        var md = @":::dropcap lines=5
Sophisticated typography enhances long-form reading comprehension.
:::
";
        var html = E2ETestHelpers.RenderHtml(md);
        Assert.Contains("class=\"dropcap\"", html);
        Assert.Contains("--dropcap-lines: 5", html);
        Assert.Contains("Sophisticated typography", html);
    }

    // =========================================================================
    // Section 2: Track Changes & Comments Adversarial Suite
    // =========================================================================

    [Fact]
    public async Task TrackChanges_01_Adversarial_DynamicCommentsPart_NoHardcodedRels()
    {
        var md = @"# Document with Revisions

The distributed consensus algorithm^[Leslie Lamport: ""Paxos guarantees safety under asynchronous network conditions.""] was verified.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var main = doc.MainDocumentPart!;
            Assert.NotNull(main);
            Assert.NotNull(main.WordprocessingCommentsPart);

            var commentRelId = main.GetIdOfPart(main.WordprocessingCommentsPart);
            Assert.False(string.IsNullOrWhiteSpace(commentRelId));

            // Verify .rels contains dynamic relationship to comments.xml
            var relsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/_rels/document.xml.rels")!;
            Assert.Contains($"Id=\"{commentRelId}\"", relsXml);
            Assert.Contains("relationships/comments", relsXml);

            // Verify comments.xml content
            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Leslie Lamport", commentsXml);
            Assert.Contains("Paxos guarantees safety", commentsXml);
            Assert.Contains("w:initials=\"LL\"", commentsXml);

            // Verify document.xml references
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:commentRangeStart", docXml);
            Assert.Contains("<w:commentRangeEnd", docXml);
            Assert.Contains("<w:commentReference", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task TrackChanges_02_Adversarial_CommentsWithSpecialXmlChars_Escaped()
    {
        var md = @"Security policy evaluation.^[Security Officer: ""Condition A < B && C > D with 'single' & \""double\"" quotes.""]
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Condition A &lt; B &amp;&amp; C &gt; D", commentsXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("Condition A &lt; B &amp;&amp; C &gt; D", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task TrackChanges_03_Adversarial_UnclosedAndMalformedCriticMarkup()
    {
        var md = @"This has {--unclosed deletion text without closing marker.
This has {++unclosed addition text.
This has ^[unclosed comment text without closing bracket.
And a normal sentence afterwards.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            // Unclosed markers should not crash exporter or break OpenXML schema
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("normal sentence afterwards", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task TrackChanges_04_Adversarial_RevisionsAndCommentsInTables()
    {
        var md = @"| Protocol | State | Notes |
| :--- | :--- | :--- |
| TLS 1.2 | {--Deprecated--} | {--Legacy support only--} |
| TLS 1.3 | {++Standard++} | Modern^[Security Team: ""Mandatory for all public endpoints.""] |
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
            Assert.Contains("<w:commentRangeStart", docXml);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Security Team", commentsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void TrackChanges_05_Adversarial_CodeFenceIsolation()
    {
        var md = @"# Source Code Review

```csharp
public class MathUtil
{
    public int Add(int a, int b) => {--a - b--}{++a + b++};
    // ^[CodeReviewer: ""Fixed subtraction bug""]
}
```

Outside prose {--buggy code--}{++fixed code++}.
";
        var html = E2ETestHelpers.RenderHtml(md);

        // Inside code block: {-- and {++ and ^[ must be literal text, not HTML del/ins/comment tags
        Assert.Contains("{--a - b--}", html);
        Assert.Contains("{++a + b++}", html);
        Assert.Contains("^[CodeReviewer:", html);

        // Outside code block: properly transformed
        Assert.Contains("<del", html);
        Assert.Contains("<ins", html);
        Assert.Contains("buggy code", html);
        Assert.Contains("fixed code", html);
    }

    [Fact]
    public async Task TrackChanges_06_Adversarial_MultiReviewerThread_UniqueCommentIds()
    {
        var md = @"# Multi-Reviewer Collaboration

Point 1.^[Alice (2026-08-20): ""Alice comment.""]
Point 2.^[Bob (2026-08-21): ""Bob comment.""]
Point 3.^[Charlie (2026-08-22): ""Charlie comment.""]
Point 4.^[David.E.Smith: ""David comment.""]
Point 5.^[Eve_Admin: ""Eve comment.""]
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Alice", commentsXml);
            Assert.Contains("Bob", commentsXml);
            Assert.Contains("Charlie", commentsXml);
            Assert.Contains("David.E.Smith", commentsXml);
            Assert.Contains("Eve_Admin", commentsXml);

            // Verify all 5 comment IDs are distinct
            var commentIdMatches = Regex.Matches(commentsXml, @"<w:comment\b[^>]*\bw:id=""(\d+)""");
            Assert.Equal(5, commentIdMatches.Count);
            var distinctIds = commentIdMatches.Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.Equal(5, distinctIds.Count);

            // Verify document.xml has matching commentReferences for all IDs
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            foreach (var id in distinctIds)
            {
                Assert.Contains($"<w:commentReference w:id=\"{id}\"", docXml);
                Assert.Contains($"<w:commentRangeStart w:id=\"{id}\"", docXml);
                Assert.Contains($"<w:commentRangeEnd w:id=\"{id}\"", docXml);
            }
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task TrackChanges_07_Adversarial_SettingsXml_TrackRevisionsEnabled()
    {
        var md = "Normal text with {--removed--}{++inserted++} content.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml")!;
            Assert.Contains("<w:trackRevisions", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // Section 3: Concordance & Subject Index (:::index) Adversarial Suite
    // =========================================================================

    [Fact]
    public async Task Index_01_Adversarial_MultiLevelIndexEntries()
    {
        var md = @"# Distributed Systems

Database systems^[index: ""Storage:Relational:PostgreSQL""] manage persistent records.
Key-value stores^[index: ""Storage:NoSQL:Redis""] provide in-memory caching.
Message brokers^[index: ""Messaging:Kafka""] enable event streams.

:::index columns=3
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("Storage:Relational:PostgreSQL", docXml);
            Assert.Contains("Storage:NoSQL:Redis", docXml);
            Assert.Contains("Messaging:Kafka", docXml);
            Assert.Contains("<w:fldSimple", docXml);
            Assert.Contains("INDEX \\c &quot;3&quot;", docXml);

            // HTML preview verification
            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("class=\"ms-index-block\"", html);
            Assert.Contains("Storage", html);
            Assert.Contains("Messaging", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Index_02_Adversarial_IndexTermsWithSpecialXmlChars()
    {
        var md = @"Programming concepts:
- Systems programming^[index: ""C++ & Rust <Low-Level>""]
- Web development^[index: ""HTML & DOM & WebAssembly""]

:::index
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("C++ &amp; Rust &lt;Low-Level&gt;", docXml);
            Assert.Contains("HTML &amp; DOM &amp; WebAssembly", docXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Index_03_Adversarial_IndexBlockWithCustomColumns()
    {
        var md = @"# Index Columns Test

Term A^[index: ""Alpha""]. Term B^[index: ""Beta""]. Term C^[index: ""Gamma""].

:::index count=4
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("INDEX \\c &quot;4&quot;", docXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("class=\"ms-index-block\"", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Index_04_Adversarial_IndexWithNoEntries_Or_EntriesWithNoIndexBlock()
    {
        // Scenario A: :::index block exists, but 0 index terms in doc
        var mdA = @"# Document Without Index Terms

Just ordinary paragraphs.

:::index
:::
";
        var docxPathA = await E2ETestHelpers.ExportDocxToTempFileAsync(mdA);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPathA);
            Assert.Empty(errors);

            var htmlA = E2ETestHelpers.RenderHtml(mdA);
            Assert.Contains("ms-index-block", htmlA);
        }
        finally
        {
            if (File.Exists(docxPathA)) File.Delete(docxPathA);
        }

        // Scenario B: Index terms exist, but no :::index block
        var mdB = @"# Document With Index Terms Only

Concept A^[index: ""Concept A""] and Concept B^[index: ""Concept B""].
";
        var docxPathB = await E2ETestHelpers.ExportDocxToTempFileAsync(mdB);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPathB);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPathB, "word/document.xml")!;
            Assert.Contains("XE &quot;Concept A&quot;", docXml);
            Assert.Contains("XE &quot;Concept B&quot;", docXml);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPathB, "word/settings.xml")!;
            Assert.Contains("<w:updateFields", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPathB)) File.Delete(docxPathB);
        }
    }

    [Fact]
    public async Task Index_05_Adversarial_SettingsXml_UpdateFieldsEnabled()
    {
        var md = "Important concept^[index: \"Telemetry\"].\n\n:::index\n:::";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml")!;
            Assert.Contains("<w:updateFields", settingsXml);
            Assert.Contains("w:val=\"true\"", settingsXml);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public void Index_06_Adversarial_CodeFenceIsolation()
    {
        var md = @"# Markdown Indexing Guide

```markdown
Here is an index term: ^[index: ""CodeFenceHiddenTerm""]
:::index
:::
```

Prose text^[index: ""VisibleTerm""].
";
        var html = E2ETestHelpers.RenderHtml(md);

        // CodeFenceHiddenTerm should remain inside code fence and not be lifted as index anchor
        Assert.DoesNotContain("data-index=\"CodeFenceHiddenTerm\"", html);
        Assert.Contains("data-index=\"VisibleTerm\"", html);
    }

    [Fact]
    public void Index_07_Adversarial_HtmlPreview_AlphabeticalGroupingAndSubentries()
    {
        var md = @"# Encyclopedia

Biology^[index: ""Biology:Genetics""] and Zoology^[index: ""Zoology:Mammals""].
Algorithms^[index: ""Algorithms:Sorting""] and Analysis^[index: ""Algorithms:Complexity""].

:::index
:::
";
        var html = E2ETestHelpers.RenderHtml(md);

        Assert.Contains("class=\"ms-index-letter\">A</div>", html);
        Assert.Contains("class=\"ms-index-letter\">B</div>", html);
        Assert.Contains("class=\"ms-index-letter\">Z</div>", html);
        Assert.Contains("<strong>Algorithms</strong>", html);
        Assert.Contains("class=\"ms-index-subentry\">Sorting</div>", html);
        Assert.Contains("class=\"ms-index-subentry\">Complexity</div>", html);
    }

    // =========================================================================
    // Section 4: Cross-Milestone Combinations & Full Document Stress Tests
    // =========================================================================

    [Fact]
    public async Task Combinatorial_01_Adversarial_DropCap_With_Revisions_Comments_Index_AllInOne()
    {
        var md = @":::dropcap 4
**Foundational** principles {--were historically ignored--}{++are now rigorously enforced++}^[Principal Reviewer (2026-08-23): ""Ensure compliance with ISO-27001.""] in distributed architectures^[index: ""Architecture:Foundational Principles""].
:::

# Next Section

Continuing discussion.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("<w:commentRangeStart", docXml);
            Assert.Contains("<w:commentReference", docXml);
            Assert.Contains("Architecture:Foundational Principles", docXml);

            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Principal Reviewer", commentsXml);
            Assert.Contains("Ensure compliance with ISO-27001.", commentsXml);

            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("dropcap", html);
            Assert.Contains("<del", html);
            Assert.Contains("<ins", html);
            Assert.Contains("Principal Reviewer", html);
            Assert.Contains("ms-index-anchor", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Combinatorial_02_Adversarial_FullEditorialPublication()
    {
        var md = @":::cover-page theme=""modern""
title: Treatise on High-Assurance Document Engineering
subtitle: Principles, Schemas, and Verification Vectors
author: System Architecture Council
organization: Enterprise Standards Bureau
date: 2026-08-23
version: 2.0.0
abstract: This treatise provides formal verification of OpenXML ECMA-376 and HTML5 multi-dialect publishing pipelines.
:::

:::watermark ""OFFICIAL STANDARD"" color=""#003366"" opacity=""0.15""

:::line-numbers count-by=5 restart=""per-page""

:::dropcap 4
**In** modern enterprise document systems, reliability is {--an afterthought--}{++the primary invariant++}^[Chief Auditor (2026-08-23): ""Audit trail verification required.""] across all publishing tiers^[index: ""Governance:Reliability Invariant""].
:::

# Section 1: Security & Verification

Verification of {--untyped macros--}{++strictly typed OpenXML schemas++} eliminates file corruption risks^[index: ""Security:Schema Verification""].

| Benchmark Target | Legacy Latency | Optimized Latency | Status |
| :--- | :--- | :--- | :--- |
| XML Serialization | {--120ms--} | {++18ms++} | {++Verified++} |
| Memory Footprint | {--64MB--} | {++4.2MB++} | {++Optimal++} |

Verification confirmed by system benchmarks^[index: ""Testing:OpenXmlValidator""].

```csharp
public sealed class EmpiricalChallenger
{
    public bool ValidateM2Features() => true;
}
```

:::index columns=2
:::
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            // 1. ECMA-376 schema validation check
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            if (errors.Any())
            {
                var errStr = string.Join("; ", errors.Select(e => $"Id: {e.Id}, Desc: {e.Description}, Node: {e.Node?.OuterXml}"));
                Assert.True(false, $"Schema validation failed ({errors.Count} errors): {errStr}");
            }
            Assert.Empty(errors);

            // 2. OpenXML package entry verification
            var entries = E2ETestHelpers.GetZipEntries(docxPath);
            Assert.Contains("word/document.xml", entries);
            Assert.Contains("word/header1.xml", entries);
            Assert.Contains("word/comments.xml", entries);
            Assert.Contains("word/settings.xml", entries);
            Assert.Contains("word/_rels/document.xml.rels", entries);

            // 3. Document.xml content verification
            var docXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/document.xml")!;
            Assert.Contains("<w:titlePg", docXml);
            Assert.Contains("<w:headerReference", docXml);
            Assert.Contains("<w:lnNumType", docXml);
            Assert.Contains("<w:framePr", docXml);
            Assert.Contains("<w:del", docXml);
            Assert.Contains("<w:ins", docXml);
            Assert.Contains("<w:commentRangeStart", docXml);
            Assert.Contains("<w:commentReference", docXml);
            Assert.Contains("XE &quot;Governance:Reliability Invariant&quot;", docXml);
            Assert.Contains("INDEX \\c &quot;2&quot;", docXml);

            // 4. Header1.xml watermark check
            var headerXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/header1.xml")!;
            Assert.Contains("OFFICIAL STANDARD", headerXml);

            // 5. Comments.xml check
            var commentsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/comments.xml")!;
            Assert.Contains("Chief Auditor", commentsXml);
            Assert.Contains("Audit trail verification required.", commentsXml);

            // 6. Settings.xml trackRevisions and updateFields check
            var settingsXml = E2ETestHelpers.ReadZipEntry(docxPath, "word/settings.xml")!;
            Assert.Contains("<w:trackRevisions", settingsXml);
            Assert.Contains("<w:updateFields", settingsXml);

            // 7. HTML preview check
            var html = E2ETestHelpers.RenderHtml(md);
            Assert.Contains("mk-watermark-overlay", html);
            Assert.Contains("OFFICIAL STANDARD", html);
            Assert.Contains("cover-theme-modern", html);
            Assert.Contains("Treatise on High-Assurance Document Engineering", html);
            Assert.Contains("dropcap", html);
            Assert.Contains("<del", html);
            Assert.Contains("<ins", html);
            Assert.Contains("Chief Auditor", html);
            Assert.Contains("ms-index-block", html);
            Assert.Contains("Governance", html);
            Assert.Contains("Reliability Invariant", html);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
