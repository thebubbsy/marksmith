using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.Core.Services;
using MarkSmith.Mcp.Server;
using MarkSmith.Mcp.Tools;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class McpAndPatchingTests : IDisposable
{
    private readonly string _tempDir;

    public McpAndPatchingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MarkSmith_M3Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private string CreateSampleDocx(string fileName, string markdownContent)
    {
        string path = Path.Combine(_tempDir, fileName);
        var exporter = new DocxExportService();
        var settings = new AppSettings { Theme = "GitHub Light" };
        exporter.ExportAsync(markdownContent, path, settings).GetAwaiter().GetResult();
        return path;
    }

    // =========================================================================
    // 1. DocxInspector Tests
    // =========================================================================

    [Fact]
    public void DocxInspector_InspectsDocumentMetadataAndSections()
    {
        string md = "# Document Title\n\nThis is introductory body text.\n\n## Section 1\n\nSection paragraph.";
        string docxPath = CreateSampleDocx("inspect_meta.docx", md);

        var inspector = new DocxInspector();
        var report = inspector.Inspect(docxPath);

        Assert.NotNull(report);
        Assert.True(report.TotalParagraphs >= 3);
        Assert.NotEmpty(report.Sections);
        Assert.True(report.Sections[0].PageWidth > 0);
        Assert.True(report.Sections[0].PageHeight > 0);
        Assert.NotEmpty(report.Blocks);
    }

    [Fact]
    public void DocxInspector_ExtractsHeadingsAndHierarchyPaths()
    {
        string md = "# Top Level\n\nBody 1\n\n## Sub Level\n\nBody 2\n\n### Deep Level\n\nBody 3";
        string docxPath = CreateSampleDocx("inspect_headings.docx", md);

        var inspector = new DocxInspector();
        var report = inspector.Inspect(docxPath);

        var headingBlocks = report.Blocks.Where(b => b.HeadingLevel.HasValue).ToList();
        Assert.True(headingBlocks.Count >= 3);

        var deepBlock = report.Blocks.FirstOrDefault(b => b.Text.Contains("Body 3"));
        Assert.NotNull(deepBlock);
        Assert.NotNull(deepBlock.HeadingPath);
        Assert.Contains("Top Level", deepBlock.HeadingPath);
        Assert.Contains("Sub Level", deepBlock.HeadingPath);
    }

    [Fact]
    public void DocxInspector_ExtractsTablesWithCells()
    {
        string md = "# Table Test\n\n| Col A | Col B |\n|---|---|\n| Cell 1 | Cell 2 |\n| Cell 3 | Cell 4 |";
        string docxPath = CreateSampleDocx("inspect_table.docx", md);

        var inspector = new DocxInspector();
        var report = inspector.Inspect(docxPath);

        Assert.True(report.TotalTables >= 1);
        var tableBlock = report.Blocks.FirstOrDefault(b => b.TableInfo != null);
        Assert.NotNull(tableBlock);
        Assert.NotNull(tableBlock.TableInfo);
        Assert.True(tableBlock.TableInfo.RowCount >= 2);
        Assert.True(tableBlock.TableInfo.ColumnCount >= 2);
    }

    [Fact]
    public void DocxInspector_InspectsCommentsAndRevisions()
    {
        string md = "# Comment Test\n\nParagraph to comment on.";
        string docxPath = CreateSampleDocx("inspect_comments.docx", md);

        // Add a comment and revision using patcher
        var patcher = new InPlaceDocxPatcher();
        var inspectBefore = new DocxInspector().Inspect(docxPath);
        var firstPara = inspectBefore.Blocks.First(b => !string.IsNullOrEmpty(b.ParaId));

        patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { ParaId = firstPara.ParaId },
                    Comment = "This is a reviewer comment.",
                    Author = "Reviewer Agent"
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Replaced text under track changes.",
                    TrackChanges = true,
                    Author = "Editor Agent"
                }
            }
        });

        var report = new DocxInspector().Inspect(docxPath);
        Assert.True(report.TotalComments >= 1);
        Assert.Contains(report.Comments, c => c.CommentText.Contains("reviewer comment"));
        Assert.True(report.TotalRevisions >= 1);
    }

    [Fact]
    public void DocxInspector_StreamOverloadWorks()
    {
        string md = "# Stream Test\n\nParagraph in memory.";
        string docxPath = CreateSampleDocx("inspect_stream.docx", md);

        using var stream = File.OpenRead(docxPath);
        var inspector = new DocxInspector();
        var report = inspector.Inspect(stream);

        Assert.NotNull(report);
        Assert.True(report.TotalParagraphs >= 2);
    }

    // =========================================================================
    // 2. InPlaceDocxPatcher Tests
    // =========================================================================

    [Fact]
    public void InPlaceDocxPatcher_ReplaceByParaId_PreservesFormatting()
    {
        string md = "# Main Header\n\nOriginal paragraph text that will be modified.\n\nFinal paragraph.";
        string docxPath = CreateSampleDocx("patch_paraid.docx", md);

        var inspector = new DocxInspector();
        var report = inspector.Inspect(docxPath);
        var targetBlock = report.Blocks.First(b => b.Text.Contains("Original paragraph text"));
        Assert.NotNull(targetBlock.ParaId);

        var patcher = new InPlaceDocxPatcher();
        var result = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { ParaId = targetBlock.ParaId },
                    Content = "Surgically patched paragraph with **bold** text.",
                    PreserveFormatting = true
                }
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.OperationsApplied);

        var updatedReport = inspector.Inspect(docxPath);
        Assert.Contains(updatedReport.Blocks, b => b.Text.Contains("Surgically patched paragraph with bold text"));

        // ECMA-376 schema validation
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        var errors = validator.Validate(doc).Where(e => !e.Description.Contains("attribute is not declared")).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void InPlaceDocxPatcher_InsertBeforeAndInsertAfter()
    {
        string md = "# Anchor\n\nMiddle Target Paragraph.\n\nEnd Paragraph.";
        string docxPath = CreateSampleDocx("patch_insert.docx", md);

        var inspector = new DocxInspector();
        var report = inspector.Inspect(docxPath);
        var middleBlock = report.Blocks.First(b => b.Text.Contains("Middle Target"));

        var patcher = new InPlaceDocxPatcher();
        var result = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.InsertBefore,
                    Target = new BlockSelector { ParaId = middleBlock.ParaId },
                    Content = "Inserted Before Paragraph."
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.InsertAfter,
                    Target = new BlockSelector { ParaId = middleBlock.ParaId },
                    Content = "Inserted After Paragraph."
                }
            }
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.OperationsApplied);

        var updatedReport = inspector.Inspect(docxPath);
        Assert.Contains(updatedReport.Blocks, b => b.Text.Contains("Inserted Before"));
        Assert.Contains(updatedReport.Blocks, b => b.Text.Contains("Inserted After"));

        // Verify unique paraIds
        var paraIds = updatedReport.Blocks.Select(b => b.ParaId).Where(id => !string.IsNullOrEmpty(id)).ToList();
        Assert.Equal(paraIds.Count, paraIds.Distinct().Count());
    }

    [Fact]
    public void InPlaceDocxPatcher_DeleteBlock()
    {
        string md = "# Keep 1\n\nDelete Me\n\nKeep 2";
        string docxPath = CreateSampleDocx("patch_delete.docx", md);

        var inspector = new DocxInspector();
        var report = inspector.Inspect(docxPath);
        var deleteBlock = report.Blocks.First(b => b.Text.Contains("Delete Me"));

        var patcher = new InPlaceDocxPatcher();
        var result = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Delete,
                    Target = new BlockSelector { ParaId = deleteBlock.ParaId }
                }
            }
        });

        Assert.True(result.Success);

        var updatedReport = inspector.Inspect(docxPath);
        Assert.DoesNotContain(updatedReport.Blocks, b => b.Text.Contains("Delete Me"));
        Assert.Contains(updatedReport.Blocks, b => b.Text.Contains("Keep 1"));
        Assert.Contains(updatedReport.Blocks, b => b.Text.Contains("Keep 2"));
    }

    [Fact]
    public void InPlaceDocxPatcher_AppendAndPrepend()
    {
        string md = "Middle Content";
        string docxPath = CreateSampleDocx("patch_append_prepend.docx", md);

        var patcher = new InPlaceDocxPatcher();
        var result = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Prepend,
                    Content = "# Header At Top"
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Append,
                    Content = "Footer At Bottom"
                }
            }
        });

        Assert.True(result.Success);

        var report = new DocxInspector().Inspect(docxPath);
        Assert.Contains(report.Blocks.First().Text, "Header At Top");
        Assert.Contains(report.Blocks.Last().Text, "Footer At Bottom");
    }

    [Fact]
    public void InPlaceDocxPatcher_AddCommentAndAcceptRevisions()
    {
        string md = "# Document Title\n\nParagraph for review.";
        string docxPath = CreateSampleDocx("patch_comment_rev.docx", md);

        var patcher = new InPlaceDocxPatcher();
        var inspect1 = new DocxInspector().Inspect(docxPath);
        var target = inspect1.Blocks.First(b => b.Text.Contains("Paragraph for review"));

        // 1. Add comment and replace with track changes
        var res1 = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { ParaId = target.ParaId },
                    Comment = "Please reword this paragraph.",
                    Author = "Auditor"
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { ParaId = target.ParaId },
                    Content = "Reworded revision text.",
                    TrackChanges = true,
                    Author = "Auditor"
                }
            }
        });

        Assert.True(res1.Success);

        var inspect2 = new DocxInspector().Inspect(docxPath);
        Assert.NotEmpty(inspect2.Comments);
        Assert.NotEmpty(inspect2.Revisions);

        // 2. Accept all revisions
        var res2 = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AcceptRevision,
                    Target = new BlockSelector() // whole document
                }
            }
        });

        Assert.True(res2.Success);

        var inspect3 = new DocxInspector().Inspect(docxPath);
        Assert.Empty(inspect3.Revisions);
        Assert.Contains(inspect3.Blocks, b => b.Text.Contains("Reworded revision text"));
    }

    [Fact]
    public void InPlaceDocxPatcher_TargetByHeadingPathAndTableCell()
    {
        string md = "# Heading 1\n\nText 1\n\n## Heading 2\n\nText 2\n\n| Table H1 | Table H2 |\n|---|---|\n| Cell A | Cell B |";
        string docxPath = CreateSampleDocx("patch_selectors.docx", md);

        var patcher = new InPlaceDocxPatcher();
        var result = patcher.ApplyPatch(docxPath, new DocxPatchRequest
        {
            DocxPath = docxPath,
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { HeadingPath = "Heading 2" },
                    Content = "## Updated Heading 2 Title"
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector
                    {
                        TableCell = new TableCellSelector { TableIndex = 0, Row = 1, Col = 1 }
                    },
                    Content = "Modified Cell B"
                }
            }
        });

        Assert.True(result.Success);

        var report = new DocxInspector().Inspect(docxPath);
        Assert.Contains(report.Blocks, b => b.Text.Contains("Updated Heading 2 Title"));
        var tbl = report.Blocks.First(b => b.TableInfo != null);
        Assert.Contains(tbl.TableInfo!.Cells, c => c.Row == 1 && c.Column == 1 && c.Text.Contains("Modified Cell B"));
    }

    [Fact]
    public void InPlaceDocxPatcher_StreamOverloadAppliesPatch()
    {
        string md = "# Stream Source\n\nOriginal Text";
        string docxPath = CreateSampleDocx("patch_stream.docx", md);

        using var inStream = File.OpenRead(docxPath);
        using var outStream = new MemoryStream();

        var patcher = new InPlaceDocxPatcher();
        var result = patcher.ApplyPatch(inStream, outStream, new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Replaced In Memory Stream"
                }
            }
        });

        Assert.True(result.Success);
        Assert.True(outStream.Length > 0);

        outStream.Position = 0;
        var report = new DocxInspector().Inspect(outStream);
        Assert.Contains(report.Blocks, b => b.Text.Contains("Replaced In Memory Stream"));
    }

    // =========================================================================
    // 3. MarkSmith.Mcp Protocol & Tools Tests
    // =========================================================================

    [Fact]
    public async Task McpServer_InitializeHandshake()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test-client\",\"version\":\"1.0\"}}}";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt64());

        var result = root.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("marksmith-mcp", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("3.0.0", result.GetProperty("serverInfo").GetProperty("version").GetString());
    }

    [Fact]
    public async Task McpServer_Ping()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":\"ping-1\",\"method\":\"ping\"}";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("ping-1", root.GetProperty("id").GetString());
        Assert.True(root.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task McpServer_ToolsList_Returns4ToolsWithValidSchemas()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        var tools = root.GetProperty("result").GetProperty("tools");
        Assert.Equal(4, tools.GetArrayLength());

        var toolNames = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("render_markdown_to_docx", toolNames);
        Assert.Contains("inspect_docx", toolNames);
        Assert.Contains("patch_docx", toolNames);
        Assert.Contains("convert_docx_to_markdown", toolNames);
    }

    [Fact]
    public async Task McpServer_ToolCall_FullWorkflowRoundTrip()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();

        // 1. render_markdown_to_docx
        string outDocx = Path.Combine(_tempDir, "mcp_rendered.docx");
        string renderReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 10,
            method = "tools/call",
            @params = new
            {
                name = "render_markdown_to_docx",
                arguments = new
                {
                    markdown = "# MCP Document\n\nInitial paragraph rendered by MCP tool.\n\n## Section 2\n\nSection text.",
                    output_path = outDocx,
                    theme = "GitHub Light"
                }
            }
        });

        string? renderResp = await dispatcher.DispatchAsync(renderReq);
        Assert.NotNull(renderResp);
        Assert.True(File.Exists(outDocx));

        // 2. inspect_docx
        string inspectReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "tools/call",
            @params = new
            {
                name = "inspect_docx",
                arguments = new
                {
                    docx_path = outDocx
                }
            }
        });

        string? inspectResp = await dispatcher.DispatchAsync(inspectReq);
        Assert.NotNull(inspectResp);
        using var inspectDoc = JsonDocument.Parse(inspectResp);
        var contentArray = inspectDoc.RootElement.GetProperty("result").GetProperty("content");
        string inspectText = contentArray[0].GetProperty("text").GetString()!;
        using var reportDoc = JsonDocument.Parse(inspectText);
        var blocks = reportDoc.RootElement.GetProperty("report").GetProperty("blocks");
        Assert.True(blocks.GetArrayLength() >= 3);

        string targetParaId = blocks[1].GetProperty("paraId").GetString()!;

        // 3. patch_docx
        string patchReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 12,
            method = "tools/call",
            @params = new
            {
                name = "patch_docx",
                arguments = new
                {
                    docx_path = outDocx,
                    operations = new[]
                    {
                        new
                        {
                            op = "replace",
                            target = new { para_id = targetParaId },
                            content = "Surgically patched paragraph from MCP agent."
                        }
                    }
                }
            }
        });

        string? patchResp = await dispatcher.DispatchAsync(patchReq);
        Assert.NotNull(patchResp);
        Assert.Contains("success", patchResp);

        // 4. convert_docx_to_markdown
        string convertReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 13,
            method = "tools/call",
            @params = new
            {
                name = "convert_docx_to_markdown",
                arguments = new
                {
                    docx_path = outDocx
                }
            }
        });

        string? convertResp = await dispatcher.DispatchAsync(convertReq);
        Assert.NotNull(convertResp);
        Assert.Contains("Surgically patched paragraph from MCP agent", convertResp);
    }

    [Fact]
    public async Task McpServer_InvalidMethod_ReturnsError32601()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"non_existent_method\"}";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        Assert.Equal(-32601, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task McpServer_MalformedJson_ReturnsError32700()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{malformed json string";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        Assert.Equal(-32700, root.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task McpServer_NotificationsReturnNullResponse()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string notification = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

        string? response = await dispatcher.DispatchAsync(notification);
        Assert.Null(response);
    }
}
