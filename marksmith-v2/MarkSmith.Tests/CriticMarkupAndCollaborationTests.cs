using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class CriticMarkupAndCollaborationTests
{
    // =========================================================================
    // 1. CriticMarkup Grammar & Normalization Tests
    // =========================================================================

    [Fact]
    public void CriticMarkup_Addition_Normalizes_To_Ins()
    {
        var input = "Here is {++newly added++} text.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Equal("Here is <ins>newly added</ins> text.", normalized);
    }

    [Fact]
    public void CriticMarkup_Deletion_Normalizes_To_Del()
    {
        var input = "Here is {--obsolete--} text.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Equal("Here is <del>obsolete</del> text.", normalized);
    }

    [Fact]
    public void CriticMarkup_TildeDeletion_Normalizes_To_Del()
    {
        var input = "Here is {~~deprecated~~} text.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Equal("Here is <del>deprecated</del> text.", normalized);
    }

    [Fact]
    public void CriticMarkup_Substitution_Normalizes_To_Del_And_Ins()
    {
        var input = "Refactored {~~oldMethod~>newMethod~~} smoothly.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Equal("Refactored <del>oldMethod</del><ins>newMethod</ins> smoothly.", normalized);
    }

    [Fact]
    public void CriticMarkup_Highlight_Normalizes_To_Mark()
    {
        var input = "Important {==highlighted text==} section.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Equal("Important <mark>highlighted text</mark> section.", normalized);
    }

    [Fact]
    public void CriticMarkup_Highlight_With_Comment_Normalizes_To_Mark_And_CommentAnchor()
    {
        var input = "Review this {==critical section==}{>>Alice: Please check SLA<<} carefully.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Contains("<mark>critical section</mark>", normalized);
        Assert.Contains("class=\"ms-comment-anchor\"", normalized);
        Assert.Contains("data-author=\"Alice\"", normalized);
        Assert.Contains("data-comment=\"Please check SLA\"", normalized);
    }

    [Fact]
    public void CriticMarkup_Highlight_With_Attributed_Date_Comment()
    {
        var input = "Review this {==critical section==}{>>@Bob [2026-09-02]: Double check numbers<<} today.";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Contains("<mark>critical section</mark>", normalized);
        Assert.Contains("data-author=\"Bob\"", normalized);
        Assert.Contains("data-date=\"2026-09-02\"", normalized);
        Assert.Contains("data-comment=\"Double check numbers\"", normalized);
    }

    [Fact]
    public void CriticMarkup_Standalone_Comment_Normalizes_To_CommentAnchor()
    {
        var input = "Paragraph end.{>>Standalone comment<<}";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Contains("class=\"ms-comment-anchor\"", normalized);
        Assert.Contains("data-comment=\"Standalone comment\"", normalized);
    }

    [Fact]
    public void CriticMarkup_CodeFence_And_InlineCode_Isolation()
    {
        var input = @"Here is code:
`{++literal add++}` and `{--literal del--}`

```markdown
{++fence add++}
{~~fence old~>new~~}
```
";
        var normalized = DialectNormalizer.Apply(input);
        Assert.Contains("`{++literal add++}`", normalized);
        Assert.Contains("`{--literal del--}`", normalized);
        Assert.Contains("{++fence add++}", normalized);
        Assert.Contains("{~~fence old~>new~~}", normalized);
    }

    // =========================================================================
    // 2. OpenXML Track Changes & Comments Export Compliance
    // =========================================================================

    [Fact]
    public async Task Export_Del_Renders_WDel_With_WDelText_And_No_WT()
    {
        var md = "Proposal {--dropped clause--} approved.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var body = doc.MainDocumentPart!.Document.Body!;
            var dels = body.Descendants<W.DeletedRun>().ToList();
            Assert.Single(dels);

            var del = dels[0];
            Assert.NotEmpty(del.Descendants<W.DeletedText>());
            // ECMA-376 strict invariant: No <w:t> elements inside <w:del>
            Assert.Empty(del.Descendants<W.Text>());
            Assert.Equal("dropped clause", del.Descendants<W.DeletedText>().First().Text);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Export_Ins_Renders_WIns_With_WT()
    {
        var md = "Proposal {++added clause++} approved.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var body = doc.MainDocumentPart!.Document.Body!;
            var inss = body.Descendants<W.InsertedRun>().ToList();
            Assert.Single(inss);

            var ins = inss[0];
            Assert.NotEmpty(ins.Descendants<W.Text>());
            Assert.Equal("added clause", ins.Descendants<W.Text>().First().Text);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task Export_TrackRevisions_Enabled_In_SettingsXml()
    {
        var md = "Contract clause {--old--}{++new++}.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
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
    public async Task Export_Highlight_With_Comment_Renders_Anchors_And_CommentsXml()
    {
        var md = "The SLA target {==99.99%==}{>>Alice (2026-08-23): Verify with DevOps<<} is binding.";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            using var doc = WordprocessingDocument.Open(docxPath, false);
            var main = doc.MainDocumentPart!;
            Assert.NotNull(main.WordprocessingCommentsPart);

            var comments = main.WordprocessingCommentsPart.Comments.Elements<W.Comment>().ToList();
            Assert.Single(comments);
            Assert.Equal("Alice", comments[0].Author?.Value);
            Assert.Contains(comments[0].Descendants<W.Text>(), t => t.Text == "Verify with DevOps");

            var body = main.Document.Body!;
            Assert.Contains(body.Descendants<W.CommentRangeStart>(), crs => crs.Id == comments[0].Id);
            Assert.Contains(body.Descendants<W.CommentRangeEnd>(), cre => cre.Id == comments[0].Id);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    // =========================================================================
    // 3. Bidirectional Reverse Import with CriticMarkup (F6 / R2.2)
    // =========================================================================

    [Fact]
    public void ReverseImport_UniversalEngine_Reconstructs_CriticMarkup_Additions_And_Deletions()
    {
        var original = "This proposal has {--outdated terms--} and {++modern enhancements++}.";
        var service = new ReverseImportService();

        // Create a synthetic DOCX in memory without embedded source store
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var p = new W.Paragraph();

            p.Append(new W.Run(new W.Text("This proposal has ") { Space = SpaceProcessingModeValues.Preserve }));

            var del = new W.DeletedRun { Id = "1", Author = "Alice", Date = DateTime.UtcNow };
            del.Append(new W.Run(new W.DeletedText("outdated terms") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(del);

            p.Append(new W.Run(new W.Text(" and ") { Space = SpaceProcessingModeValues.Preserve }));

            var ins = new W.InsertedRun { Id = "2", Author = "Bob", Date = DateTime.UtcNow };
            ins.Append(new W.Run(new W.Text("modern enhancements") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(ins);

            p.Append(new W.Run(new W.Text(".")));

            main.Document.Body.Append(p);
            main.Document.Save();
        }

        stream.Position = 0;
        var markdown = service.ConvertDocxToMarkdown(stream);

        Assert.Contains("{--outdated terms--}", markdown);
        Assert.Contains("{++modern enhancements++}", markdown);
    }

    [Fact]
    public void ReverseImport_Coalesces_Adjacent_Del_And_Ins_Into_Substitution()
    {
        var service = new ReverseImportService();

        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var p = new W.Paragraph();

            p.Append(new W.Run(new W.Text("System uses ") { Space = SpaceProcessingModeValues.Preserve }));

            var del = new W.DeletedRun { Id = "1", Author = "Reviewer" };
            del.Append(new W.Run(new W.DeletedText("monolithic database") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(del);

            var ins = new W.InsertedRun { Id = "2", Author = "Reviewer" };
            ins.Append(new W.Run(new W.Text("distributed microservices") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(ins);

            p.Append(new W.Run(new W.Text(" for high availability.")));

            main.Document.Body.Append(p);
            main.Document.Save();
        }

        stream.Position = 0;
        var options = new ReverseImportOptions { CoalesceSubstitutions = true };
        var markdown = service.ConvertDocxToMarkdown(stream, options);

        Assert.Contains("{~~monolithic database~>distributed microservices~~}", markdown);
    }

    [Fact]
    public void ReverseImport_Without_CoalesceSubstitutions_Emits_Separate_Del_And_Ins()
    {
        var service = new ReverseImportService();

        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var p = new W.Paragraph();

            var del = new W.DeletedRun { Id = "1" };
            del.Append(new W.Run(new W.DeletedText("old") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(del);

            var ins = new W.InsertedRun { Id = "2" };
            ins.Append(new W.Run(new W.Text("new") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(ins);

            main.Document.Body.Append(p);
            main.Document.Save();
        }

        stream.Position = 0;
        var options = new ReverseImportOptions { CoalesceSubstitutions = false };
        var markdown = service.ConvertDocxToMarkdown(stream, options);

        Assert.Contains("{--old--}{++new++}", markdown);
    }

    [Fact]
    public void ReverseImport_Reconstructs_Anchored_Highlight_And_CriticComment()
    {
        var service = new ReverseImportService();

        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());

            var commentsPart = main.AddNewPart<WordprocessingCommentsPart>();
            commentsPart.Comments = new W.Comments();
            var comment = new W.Comment
            {
                Id = "10",
                Author = "Alice",
                Date = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc)
            };
            comment.Append(new W.Paragraph(new W.Run(new W.Text("Verify SLA requirements"))));
            commentsPart.Comments.Append(comment);
            commentsPart.Comments.Save();

            var p = new W.Paragraph();
            p.Append(new W.Run(new W.Text("The ") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(new W.CommentRangeStart { Id = "10" });
            p.Append(new W.Run(
                new W.RunProperties(new W.Highlight { Val = W.HighlightColorValues.Yellow }),
                new W.Text("uptime target") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(new W.CommentRangeEnd { Id = "10" });
            p.Append(new W.Run(new W.CommentReference { Id = "10" }));
            p.Append(new W.Run(new W.Text(" is critical.")));

            main.Document.Body.Append(p);
            main.Document.Save();
        }

        stream.Position = 0;
        var markdown = service.ConvertDocxToMarkdown(stream);

        Assert.Contains("{==uptime target==}{>>Alice (2026-08-23): Verify SLA requirements<<}", markdown);
    }

    [Fact]
    public void ReverseImport_Options_PreserveRevisions_False_Drops_Deletions_And_Unwraps_Insertions()
    {
        var service = new ReverseImportService();

        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var p = new W.Paragraph();

            p.Append(new W.Run(new W.Text("Text ") { Space = SpaceProcessingModeValues.Preserve }));

            var del = new W.DeletedRun { Id = "1" };
            del.Append(new W.Run(new W.DeletedText("obsolete ") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(del);

            var ins = new W.InsertedRun { Id = "2" };
            ins.Append(new W.Run(new W.Text("modern") { Space = SpaceProcessingModeValues.Preserve }));
            p.Append(ins);

            main.Document.Body.Append(p);
            main.Document.Save();
        }

        stream.Position = 0;
        var options = new ReverseImportOptions { PreserveRevisionsAsCriticMarkup = false };
        var markdown = service.ConvertDocxToMarkdown(stream, options);

        Assert.DoesNotContain("obsolete", markdown);
        Assert.DoesNotContain("{++", markdown);
        Assert.Contains("Text modern", markdown);
    }

    [Fact]
    public void ReverseImport_Options_PreserveComments_False_Drops_Comments()
    {
        var service = new ReverseImportService();

        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());

            var commentsPart = main.AddNewPart<WordprocessingCommentsPart>();
            commentsPart.Comments = new W.Comments();
            var comment = new W.Comment { Id = "1", Author = "Bob" };
            comment.Append(new W.Paragraph(new W.Run(new W.Text("Private feedback"))));
            commentsPart.Comments.Append(comment);
            commentsPart.Comments.Save();

            var p = new W.Paragraph();
            p.Append(new W.CommentRangeStart { Id = "1" });
            p.Append(new W.Run(new W.Text("Protected prose")));
            p.Append(new W.CommentRangeEnd { Id = "1" });
            p.Append(new W.Run(new W.CommentReference { Id = "1" }));

            main.Document.Body.Append(p);
            main.Document.Save();
        }

        stream.Position = 0;
        var options = new ReverseImportOptions { PreserveCommentsAsCriticMarkup = false };
        var markdown = service.ConvertDocxToMarkdown(stream, options);

        Assert.Contains("Protected prose", markdown);
        Assert.DoesNotContain("Private feedback", markdown);
        Assert.DoesNotContain("Bob", markdown);
    }

    [Fact]
    public void ReverseImport_Preserves_Revisions_Inside_Tables()
    {
        var service = new ReverseImportService();

        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());

            var table = new W.Table();
            var row1 = new W.TableRow();
            row1.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Item")))));
            row1.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Status")))));
            table.Append(row1);

            var row2 = new W.TableRow();
            row2.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Engine")))));

            var cell2Para = new W.Paragraph();
            var del = new W.DeletedRun { Id = "1" };
            del.Append(new W.Run(new W.DeletedText("v1.0") { Space = SpaceProcessingModeValues.Preserve }));
            cell2Para.Append(del);

            var ins = new W.InsertedRun { Id = "2" };
            ins.Append(new W.Run(new W.Text("v2.0") { Space = SpaceProcessingModeValues.Preserve }));
            cell2Para.Append(ins);

            row2.Append(new W.TableCell(cell2Para));
            table.Append(row2);

            main.Document.Body.Append(table);
            main.Document.Save();
        }

        stream.Position = 0;
        var options = new ReverseImportOptions { CoalesceSubstitutions = true };
        var markdown = service.ConvertDocxToMarkdown(stream, options);

        Assert.Contains("{~~v1.0~>v2.0~~}", markdown);
    }

    // =========================================================================
    // 4. End-to-End Round-Trip Fidelity Tests
    // =========================================================================

    [Fact]
    public async Task RoundTrip_Lossless_EmbeddedSource_Preserves_CriticMarkup()
    {
        var md = @"# Document Revision Review

This is {--an obsolete--}{++a modern++} architecture.

We {--deprecated the legacy API--}.

We {++introduced the streaming pipeline++}.

Refactored {~~synchronous IO~>asynchronous streaming~~} seamlessly.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            var service = new ReverseImportService();
            var reimported = service.ConvertDocxToMarkdown(docxPath);

            Assert.Contains("{--an obsolete--}{++a modern++}", reimported);
            Assert.Contains("{--deprecated the legacy API--}", reimported);
            Assert.Contains("{++introduced the streaming pipeline++}", reimported);
            Assert.Contains("{~~synchronous IO~>asynchronous streaming~~}", reimported);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }

    [Fact]
    public async Task RoundTrip_UniversalEngine_Reconstructs_CriticMarkup_From_OpenXml_Revisions()
    {
        var md = @"# Document Revision Review

This is {--an obsolete--}{++a modern++} architecture.

We {--deprecated the legacy API--}.

We {++introduced the streaming pipeline++}.
";
        var docxPath = await E2ETestHelpers.ExportDocxToTempFileAsync(md);
        try
        {
            var errors = E2ETestHelpers.ValidateDocx(docxPath);
            Assert.Empty(errors);

            // Strip the embedded customXml source store to force Tier 2 Universal Engine execution
            using (var doc = WordprocessingDocument.Open(docxPath, true))
            {
                var customXmlParts = doc.MainDocumentPart!.CustomXmlParts.ToList();
                foreach (var cxp in customXmlParts)
                {
                    doc.MainDocumentPart.DeletePart(cxp);
                }
                doc.MainDocumentPart.Document.Save();
            }

            var service = new ReverseImportService();
            var reimported = service.ConvertDocxToMarkdown(docxPath, new ReverseImportOptions { CoalesceSubstitutions = true });

            Assert.Contains("{~~an obsolete~>a modern~~}", reimported);
            Assert.Contains("{--deprecated the legacy API--}", reimported);
            Assert.Contains("{++introduced the streaming pipeline++}", reimported);
        }
        finally
        {
            if (File.Exists(docxPath)) File.Delete(docxPath);
        }
    }
}
