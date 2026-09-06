using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Core.Services;
using MarkSmith.Mcp.Tools;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class Gemini38AuditEnhancementsTests : IDisposable
{
    private readonly string _tempDir;

    public Gemini38AuditEnhancementsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MarkSmith_GeminiAudit_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task PatchMarkdownTool_Returns_Content_In_Memory()
    {
        var tool = new PatchMarkdownTool();
        var json = JsonSerializer.Serialize(new
        {
            markdown = "# Original Header\n\nOriginal text.",
            operations = new[]
            {
                new
                {
                    op = "search_replace",
                    target_content = "Original text.",
                    replacement_content = "Updated text."
                }
            }
        });
        using var doc = JsonDocument.Parse(json);
        var res = await tool.ExecuteAsync(doc.RootElement);

        Assert.False(res.IsError);
        Assert.NotEmpty(res.Content);

        using var dataDoc = JsonDocument.Parse(res.Content[0].Text);
        Assert.True(dataDoc.RootElement.TryGetProperty("content", out var contentProp));
        string updatedMarkdown = contentProp.GetString()!;
        Assert.Contains("Updated text.", updatedMarkdown);
        Assert.DoesNotContain("Original text.", updatedMarkdown);
    }

    [Fact]
    public async Task ValidateMarkdownTool_Accepts_Content_Alias()
    {
        var tool = new ValidateMarkdownTool();
        var json = JsonSerializer.Serialize(new
        {
            content = "# Valid Document\n\nThis is canonical markdown."
        });
        using var doc = JsonDocument.Parse(json);
        var res = await tool.ExecuteAsync(doc.RootElement);

        Assert.False(res.IsError);
        Assert.NotEmpty(res.Content);
        using var dataDoc = JsonDocument.Parse(res.Content[0].Text);
        Assert.True(dataDoc.RootElement.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task RenderMarkdownTool_Accepts_Content_Alias()
    {
        var tool = new RenderMarkdownTool();
        var json = JsonSerializer.Serialize(new
        {
            content = "# Title\n\nParagraph text."
        });
        using var doc = JsonDocument.Parse(json);
        var res = await tool.ExecuteAsync(doc.RootElement);

        Assert.False(res.IsError);
        Assert.NotEmpty(res.Content);
        using var dataDoc = JsonDocument.Parse(res.Content[0].Text);
        Assert.True(dataDoc.RootElement.TryGetProperty("status", out var statusProp));
        Assert.Equal("success", statusProp.GetString());
        Assert.True(dataDoc.RootElement.TryGetProperty("output_path", out var outProp));
        Assert.True(File.Exists(outProp.GetString()!));
    }

    [Fact]
    public void MarkdownValidationService_Accepts_Recent_Containers()
    {
        var validator = new MarkdownValidationService();
        string md = @"
:::metrics
- **99.9%** System Uptime
- **< 150ms** P99 Latency
:::

:::cover-page
title: Systems Architecture
author: Engineer
:::

:::watermark ""CONFIDENTIAL""
:::

:::dropcap
This paragraph has an editorial drop capital.
:::

:::line-numbers
Line numbering active here.
:::

:::index count=2
:::
";
        var report = validator.Validate(md);
        var unknownContainerIssues = report.Issues.FindAll(i => i.RuleId == "UNKNOWN_CONTAINER_DIRECTIVE");
        Assert.Empty(unknownContainerIssues);
    }

    [Fact]
    public void MarkdownValidationService_Does_Not_Flag_Inline_Code_Dollar_Or_JSON_Braces()
    {
        var validator = new MarkdownValidationService();
        string md = @"# Server Configuration

Use `$env:CONNECTION_STRING` or `$PORT` to configure.
Price is `$100` per month.

Here is a sample payload:
```json
{
  ""service"": ""marksmith"",
  ""enabled"": true
}
```

And raw curly braces in text without LaTeX: {key: value}.
";
        var report = validator.Validate(md);
        var dollarIssues = report.Issues.FindAll(i => i.RuleId == "UNPAIRED_DOLLAR_DELIMITER");
        var braceIssues = report.Issues.FindAll(i => i.RuleId == "UNBALANCED_LATEX_BRACES");

        Assert.Empty(dollarIssues);
        Assert.Empty(braceIssues);
    }

    [Fact]
    public void LlmSourceService_Strips_Thought_Blocks_Without_Leaking_Internal_Reasoning()
    {
        var service = new LlmSourceService();
        string inputWithThought = @"<thought>
Here is my private internal reasoning about the architecture.
We should recommend PostgreSQL over SQLite for high concurrency.
</thought>

# Architecture Decision Record

We recommend PostgreSQL for the high-concurrency datastore.";

        var classification = service.Classify(inputWithThought);
        var (cleaned, repairs) = service.RepairArtifacts(inputWithThought, classification);

        Assert.DoesNotContain("<thought>", cleaned);
        Assert.DoesNotContain("</thought>", cleaned);
        Assert.DoesNotContain("Here is my private internal reasoning", cleaned);
        Assert.DoesNotContain("recommend PostgreSQL over SQLite", cleaned);
        Assert.Contains("# Architecture Decision Record", cleaned);
        Assert.Contains("We recommend PostgreSQL for the high-concurrency datastore.", cleaned);
    }

    [Fact]
    public async Task StreamingDocxExportService_ExportAsync_Produces_Valid_Docx()
    {
        var service = new StreamingDocxExportService();
        string md = @"# Streaming Document

This is a test of the streaming SAX OpenXML export service.

- Item A
- Item B

| Column 1 | Column 2 |
|---|---|
| Value 1 | Value 2 |
";
        string targetPath = Path.Combine(_tempDir, "streaming_test.docx");
        await service.ExportAsync(md, targetPath, new AppSettings());

        Assert.True(File.Exists(targetPath));
        Assert.True(new FileInfo(targetPath).Length > 0);
    }

    [Fact]
    public async Task DiffDocxTool_Diffs_Documents_Without_Exceptions()
    {
        var docx1 = Path.Combine(_tempDir, "diff1.docx");
        var docx2 = Path.Combine(_tempDir, "diff2.docx");

        var docxService = new DocxExportService();
        await docxService.ExportAsync("# Header 1\n\nInitial paragraph.", docx1, new AppSettings());
        await docxService.ExportAsync("# Header 1\n\nModified paragraph.\n\nAdded paragraph.", docx2, new AppSettings());

        var tool = new DiffDocxTool();
        var json = JsonSerializer.Serialize(new
        {
            old_docx_path = docx1,
            new_docx_path = docx2
        });
        using var doc = JsonDocument.Parse(json);
        var res = await tool.ExecuteAsync(doc.RootElement);

        Assert.False(res.IsError);
        Assert.NotEmpty(res.Content);
        using var dataDoc = JsonDocument.Parse(res.Content[0].Text);
        Assert.True(dataDoc.RootElement.TryGetProperty("addedBlocksCount", out var addedProp));
        Assert.True(dataDoc.RootElement.TryGetProperty("paragraphCountChange", out _));
    }
}
