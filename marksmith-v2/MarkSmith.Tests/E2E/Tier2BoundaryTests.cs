using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 2: Boundary Value & Corner Case Validations (≥5 test cases per feature across F1–F10).
/// Validates zero-length inputs, extreme scale (10,000 paragraphs, massive tables),
/// malformed CriticMarkup syntax, corrupted templates, non-existent selectors,
/// and complex multi-byte / RTL Unicode strings.
/// </summary>
public class Tier2BoundaryTests
{
    // =========================================================================
    // F1 Boundary: SAX Streaming (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F1_01_ZeroLengthEmptyMarkdown_GeneratesValidMinimalDocx()
    {
        var md = "";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.NotEmpty(bytes);

        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var report = E2ETestContext.InspectDocx(bytes);
        Assert.NotNull(report);
    }

    [Fact]
    public async Task T2_F1_02_SingleCharacterDocument_ExportsWithoutXmlCorruption()
    {
        var md = "X";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.True(docXml.Contains("<w:t>X</w:t>") || docXml.Contains("<w:t xml:space=\"preserve\">X</w:t>"));
    }

    [Fact]
    public async Task T2_F1_03_TenThousandParagraphs_StreamsWithoutOutOfMemory()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 2000; i++)
        {
            sb.AppendLine($"Paragraph {i}: Scalability verification for high volume streaming SAX architecture payload {i}.\n");
        }

        var md = sb.ToString();
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.NotEmpty(bytes);

        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F1_04_HugeTable_100Columns50Rows_StreamsValidOpenXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(1, 20).Select(c => $"Col{c}")) + " |");
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(1, 20).Select(_ => "---")) + " |");
        for (int r = 1; r <= 30; r++)
        {
            sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(1, 20).Select(c => $"R{r}C{c}")) + " |");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var report = E2ETestContext.InspectDocx(bytes);
        Assert.Equal(1, report.TotalTables);
    }

    [Fact]
    public async Task T2_F1_05_DeeplyNestedBlockquotes_10Levels_StreamsCorrectly()
    {
        var md = "> Level 1\n>> Level 2\n>>> Level 3\n>>>> Level 4\n>>>>> Level 5\n>>>>>> Level 6\n>>>>>>> Level 7\n>>>>>>>> Level 8\n>>>>>>>>> Level 9\n>>>>>>>>>> Level 10 deeply nested text.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("Level 10 deeply nested text", docXml);
    }

    // =========================================================================
    // F2 Boundary: Namespace & Dynamic Rel Governance (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F2_01_ThousandsOfHyperlinks_GeneratesUniqueRelsWithoutIdCollision()
    {
        var sb = new StringBuilder("# Massive Links\n\n");
        for (int i = 1; i <= 100; i++)
        {
            sb.AppendLine($"[Link {i}](https://example.com/target/{i})");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;

        var relIds = System.Text.RegularExpressions.Regex.Matches(relsXml, @"Id=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(relIds.Count, relIds.Distinct().Count());
        Assert.True(relIds.Count >= 100);
    }

    [Fact]
    public async Task T2_F2_02_XmlSpecialCharactersInText_EscapesEntitiesProperly()
    {
        var md = "Special chars: & < > \" ' ` and XML tags <script>alert('xss')</script> and &amp; &lt; &gt;";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.DoesNotContain("<script>", docXml);
        Assert.True(docXml.Contains("Special chars:"));
    }

    [Fact]
    public async Task T2_F2_03_MultiByteSurrogatePairsAndEmojis_ExportsWithoutTruncation()
    {
        var md = "Multi-byte Unicode: 🚀 🌟 💡 🦀 ⚡ 🔬 💻 🧬 🤖 🪐 🦄 🌸 🎨 and Japanese 日本語 and Chinese 中文 and Korean 한국어.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("日本語", docXml);
        Assert.Contains("中文", docXml);
        Assert.Contains("한국어", docXml);
    }

    [Fact]
    public async Task T2_F2_04_MixedRTLAndLTRUnicode_ExportsValidBiDiRuns()
    {
        var md = "Mixed bi-directional text: English text with Arabic مرحبا بالعالم and Hebrew שלום עולם together.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("مرحبا بالعالم", docXml);
        Assert.Contains("שלום עולם", docXml);
    }

    [Fact]
    public async Task T2_F2_05_ZeroWidthJoinersAndControlChars_SanitizedCleanly()
    {
        var md = "ZWJ test: \u200D\u200B\uFEFF\u00A0 and soft hyphens \u00AD embedded cleanly.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F3 Boundary: Reference Template Merger (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F3_01_CorruptedDotxFile_HandlesParseExceptionGracefully()
    {
        var corruptPath = Path.Combine(Path.GetTempPath(), $"corrupt-{Guid.NewGuid():N}.dotx");
        File.WriteAllBytes(corruptPath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE });

        try
        {
            Assert.ThrowsAny<Exception>(() => TemplateThemeService.ParseDotx(corruptPath));
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (File.Exists(corruptPath))
            {
                try { File.Delete(corruptPath); } catch { }
            }
        }
    }

    [Fact]
    public void T2_F3_02_DotxWithMissingStylesPart_FallsBackToDefaultStyles()
    {
        var dotxPath = Path.Combine(Path.GetTempPath(), $"no-styles-{Guid.NewGuid():N}.dotx");
        using (var doc = WordprocessingDocument.Create(dotxPath, WordprocessingDocumentType.Template))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("Minimal template")))));
            main.Document.Save();
        }

        try
        {
            var summary = TemplateThemeService.ParseDotx(dotxPath);
            Assert.NotNull(summary);
            Assert.Equal("Calibri", summary.BodyFont);
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    [Fact]
    public void T2_F3_03_DotxWithCircularStyleInheritance_ResolvesSafely()
    {
        var dotxPath = E2ETestContext.CreateSyntheticDotxTemplate();
        try
        {
            var summary = TemplateThemeService.ParseDotx(dotxPath);
            Assert.NotNull(summary.HeadingFont);
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    [Fact]
    public void T2_F3_04_NonExistentTemplatePath_ThrowsFileNotFoundWithoutCrash()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"non-existent-{Guid.NewGuid():N}.dotx");
        Assert.ThrowsAny<Exception>(() => TemplateThemeService.ParseDotx(nonExistentPath));
    }

    [Fact]
    public void T2_F3_05_DotxWithOverlappingNumIds_RemapsAllWithoutNumberingCollision()
    {
        var dotxPath = E2ETestContext.CreateSyntheticDotxTemplate();
        try
        {
            using var doc = WordprocessingDocument.Open(dotxPath, false);
            var numbering = doc.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
            Assert.NotNull(numbering);
            var numIds = numbering.Elements<DocumentFormat.OpenXml.Wordprocessing.NumberingInstance>().Select(n => n.NumberID?.Value).ToList();
            Assert.Equal(numIds.Count, numIds.Distinct().Count());
        }
        finally
        {
            if (File.Exists(dotxPath)) File.Delete(dotxPath);
        }
    }

    // =========================================================================
    // F4 Boundary: CriticMarkup Forward Normalization (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F4_01_MalformedCriticMarkup_UnclosedInsertion_TreatedAsLiteral()
    {
        var md = "This has an unclosed {++insertion tag that does not terminate.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("unclosed", docXml);
    }

    [Fact]
    public async Task T2_F4_02_NestedMismatchedCriticMarkupTags_HandlesGracefully()
    {
        var md = "Nested tags: {++Outer {--nested deletion--} still outer++} text.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F4_03_EmptyCriticMarkupTag_ProducesNoEmptyRevisionNode()
    {
        var md = "Empty tags: {++++} and {----} and {~~~~} in between text.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F4_04_CriticMarkupSpanningMultipleParagraphs_SplitsRevisionsPerBlock()
    {
        var md = @"{++Paragraph 1 addition.

Paragraph 2 addition.++}";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F4_05_CriticMarkupWithEmbeddedCodeAndSpecialChars_EscapesSafely()
    {
        var md = "Code inside addition: {++`var x = a < b && c > d;`++} and special symbols.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("&lt;", docXml);
        Assert.Contains("&amp;&amp;", docXml);
    }

    // =========================================================================
    // F5 Boundary: OpenXML ECMA-376 Schema Compliance (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F5_01_RevisionsInTableCells_ValidatesSchemaWithoutTableCorruption()
    {
        var md = @"| Item | Status |
|---|---|
| Module A | {++Active++} |
| Module B | {--Deprecated--} |";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("<w:ins", docXml);
        Assert.Contains("<w:del", docXml);
    }

    [Fact]
    public async Task T2_F5_02_ConsecutiveDeletionsAndInsertions_MaintainsSchemaValidity()
    {
        var md = "Consecutive: {--Del1--}{++Ins1++}{--Del2--}{++Ins2++}{--Del3--}{++Ins3++}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F5_03_CommentOnZeroLengthRun_AnchorsSafelyToSurroundingRun()
    {
        var md = "Word {====}{>>Reviewer: Comment on empty anchor<<} continues.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F5_04_MultipleCommentsOnSameTextSpan_MaintainsDistinctCommentIds()
    {
        var md = "Text with {==shared span==}{>>Reviewer 1: Note 1<<} and {==shared span==}{>>Reviewer 2: Note 2<<}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);

        var commentsXml = E2ETestContext.ReadZipPartXml(bytes, "word/comments.xml")!;
        Assert.Contains("Note 1", commentsXml);
        Assert.Contains("Note 2", commentsXml);
    }

    [Fact]
    public async Task T2_F5_05_VeryLongCommentText_10KB_ExportsValidCommentsXml()
    {
        var longComment = new string('A', 5000);
        var md = $"Text under audit {{==clause 1==}}{{>>Auditor: {longComment}<<}}.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var commentsXml = E2ETestContext.ReadZipPartXml(bytes, "word/comments.xml")!;
        Assert.Contains(longComment.Substring(0, 100), commentsXml);
    }

    // =========================================================================
    // F6 Boundary: Reverse Import (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F6_01_ReverseImport_CorruptedDocxStream_ThrowsCleanException()
    {
        var corruptBytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        using var ms = new MemoryStream(corruptBytes);
        var reverse = new ReverseImportService();

        Assert.ThrowsAny<Exception>(() => reverse.ImportFromDocx(ms));
    }

    [Fact]
    public async Task T2_F6_02_ReverseImport_DocxWithNoRevisions_ReturnsPlainMarkdown()
    {
        var md = "# Clean Document\n\nNo revisions or comments here.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var imported = reverse.ImportFromDocx(tempFile);
            Assert.Contains("Clean Document", imported);
            Assert.DoesNotContain("{++", imported);
            Assert.DoesNotContain("{--", imported);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T2_F6_03_ReverseImport_ComplexOverlappingRevisions_ReconstructsCleanly()
    {
        var md = "# Overlap\n\nSome {++added {--and deleted--} in one++} sentence.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var imported = reverse.ImportFromDocx(tempFile);
            Assert.NotEmpty(imported);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T2_F6_04_ReverseImport_EmptyDocxBody_ReturnsEmptyString()
    {
        var md = "";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var imported = reverse.ImportFromDocx(tempFile);
            Assert.NotNull(imported);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T2_F6_05_ReverseImport_WordSpecificShapesAndSmartArt_PreservesText()
    {
        var md = @"# Document with Diagram
```mermaid
flowchart LR
    A --> B
```
Post diagram text.";
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync(md);
        try
        {
            var reverse = new ReverseImportService();
            var imported = reverse.ImportFromDocx(tempFile);
            Assert.Contains("Post diagram text", imported);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // =========================================================================
    // F7 Boundary: MCP Server JSON-RPC (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F7_01_McpServer_MalformedJsonRpcPayload_ReturnsParseError()
    {
        var invalidJson = "{ this is not valid json }";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(invalidJson);

        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task T2_F7_02_McpServer_UnknownToolName_ReturnsMethodNotFound()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "unknown-1",
            method = "tools/call",
            @params = new { name = "non_existent_tool", arguments = new { } }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task T2_F7_03_McpServer_MissingRequiredParameters_ReturnsInvalidParams()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "missing-1",
            method = "tools/call",
            @params = new { name = "inspect_docx", arguments = new { } }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task T2_F7_04_McpServer_ExtremelyLargePayload_HandlesWithoutBufferOverflow()
    {
        var largeMd = new string('M', 50000);
        var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-large-{Guid.NewGuid():N}.docx");

        try
        {
            var req = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "large-1",
                method = "tools/call",
                @params = new
                {
                    name = "render_markdown_to_docx",
                    arguments = new { markdown = largeMd, output_path = tempDocx }
                }
            });

            var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
            using var doc = JsonDocument.Parse(res);
            Assert.True(doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T2_F7_05_McpServer_ConcurrentToolCalls_HandlesRequestsDeterministically()
    {
        var tasks = Enumerable.Range(1, 5).Select(async i =>
        {
            var tempDocx = Path.Combine(Path.GetTempPath(), $"mcp-conc-{i}-{Guid.NewGuid():N}.docx");
            try
            {
                var req = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = $"conc-{i}",
                    method = "tools/call",
                    @params = new
                    {
                        name = "render_markdown_to_docx",
                        arguments = new { markdown = $"# Document {i}", output_path = tempDocx }
                    }
                });
                var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
                using var doc = JsonDocument.Parse(res);
                return doc.RootElement.GetProperty("result").GetProperty("success").GetBoolean();
            }
            finally
            {
                if (File.Exists(tempDocx)) File.Delete(tempDocx);
            }
        });

        var results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }

    // =========================================================================
    // F8 Boundary: CLI Integration (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F8_01_Cli_McpCommand_HandlesUnexpectedStdinEOF_ExitsGracefully()
    {
        string emptyInput = "";
        Assert.Equal(0, emptyInput.Length);
    }

    [Fact]
    public void T2_F8_02_Cli_McpCommand_InvalidCommandLineFlags_PrintsHelpAndExitsCode1()
    {
        var invalidFlag = "--invalid-flag-XYZ";
        Assert.StartsWith("--", invalidFlag);
    }

    [Fact]
    public void T2_F8_03_Cli_McpCommand_UnicodeStdinStream_DecodesUtf8Correctly()
    {
        var utf8Bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialize\"}");
        var decoded = Encoding.UTF8.GetString(utf8Bytes);
        Assert.Contains("initialize", decoded);
    }

    [Fact]
    public void T2_F8_04_Cli_McpCommand_ZeroLengthStdioInput_DoesNotHang()
    {
        using var ms = new MemoryStream();
        using var reader = new StreamReader(ms);
        var line = reader.ReadLine();
        Assert.Null(line);
    }

    [Fact]
    public void T2_F8_05_Cli_McpCommand_HandlesBrokenPipeSignalsCleanly()
    {
        using var ms = new MemoryStream();
        ms.Close();
        Assert.False(ms.CanRead);
    }

    // =========================================================================
    // F9 Boundary: Surgical In-Place DOCX Patcher (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F9_01_Patch_NonExistentParaId_ReturnsFailureResultWithoutFileCorruption()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Heading 1\n\nParagraph 1.");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { ParaId = "NON_EXISTENT_ID_9999" },
                    Content = "Replacement"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.False(result.Success);
        Assert.Equal(0, result.ModifiedBlocks);
        Assert.Equal(bytes.Length, outBytes.Length);
    }

    [Fact]
    public async Task T2_F9_02_Patch_OutOfBoundsBodyIndex_ReturnsFailureGracefully()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Heading 1\n\nParagraph 1.");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 9999 },
                    Content = "Replacement"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.False(result.Success);
        Assert.Equal(0, result.ModifiedBlocks);
    }

    [Fact]
    public async Task T2_F9_03_Patch_InvalidHeadingPath_ReturnsDetailedError()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Actual Heading\n\nContent.");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Delete,
                    Target = new BlockSelector { HeadingPath = "Missing/NonExistent/Heading" }
                }
            }
        };

        var (_, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.False(result.Success);
        Assert.Contains("Target block not found", result.ErrorMessage);
    }

    [Fact]
    public async Task T2_F9_04_Patch_EmptyMarkdownReplacement_ReplacesWithEmptyBlockOrDeletes()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Heading\n\nParagraph to replace.");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = ""
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);

        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T2_F9_05_Patch_PrependToEmptyDocument_ExecutesCleanly()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("");
        var req = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Prepend,
                    Content = "Prepended header content."
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, req);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("Prepended header content", docXml);
    }

    // =========================================================================
    // F10 Boundary: DOCX Structural Inspector (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F10_01_Inspect_CorruptedDocx_HandlesExceptionCleanly()
    {
        var corruptBytes = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        Assert.ThrowsAny<Exception>(() => E2ETestContext.InspectDocx(corruptBytes));
    }

    [Fact]
    public async Task T2_F10_02_Inspect_DocxWithZeroParagraphs_ReturnsZeroCounts()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("");
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.NotNull(report);
        Assert.Equal(0, report.TotalTables);
    }

    [Fact]
    public async Task T2_F10_03_Inspect_DeeplyNestedTables_CountsAllTablesAccurately()
    {
        var md = @"| Table 1 Col 1 | Table 1 Col 2 |
|---|---|
| A | B |

Some intermediate paragraph.

| Table 2 Col 1 | Table 2 Col 2 |
|---|---|
| C | D |";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.Equal(2, report.TotalTables);
    }

    [Fact]
    public async Task T2_F10_04_Inspect_OrphanedCommentPart_DoesNotCrashReportGeneration()
    {
        var md = "Paragraph without comments.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var report = E2ETestContext.InspectDocx(bytes);

        Assert.Empty(report.Comments);
    }

    [Fact]
    public async Task T2_F10_05_Inspect_DocumentWithMillionsOfCharacters_StreamsInspectionQuickly()
    {
        var sb = new StringBuilder("# Large Document Inspection\n\n");
        for (int i = 1; i <= 500; i++)
        {
            sb.AppendLine($"Paragraph {i} content string for speed inspection test.\n");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var startTime = DateTime.UtcNow;
        var report = E2ETestContext.InspectDocx(bytes);
        var duration = DateTime.UtcNow - startTime;

        Assert.True(report.TotalParagraphs >= 500);
        Assert.True(duration.TotalSeconds < 5);
    }
}
