using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Core.Services;
using MarkSmith.Mcp.Prompts;
using MarkSmith.Mcp.Resources;
using MarkSmith.Mcp.Server;
using MarkSmith.Mcp.Tools;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class Gemini38AwarenessTests : IDisposable
{
    private readonly string _tempDir;

    public Gemini38AwarenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MarkSmith_Gemini38Tests_" + Guid.NewGuid().ToString("N"));
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

    // =========================================================================
    // 1. MCP Prompts (prompts/list and prompts/get)
    // =========================================================================

    [Fact]
    public async Task McpServer_PromptsList_Returns3PromptsWithValidArguments()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":101,\"method\":\"prompts/list\"}";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var prompts = doc.RootElement.GetProperty("result").GetProperty("prompts");
        Assert.Equal(3, prompts.GetArrayLength());

        var promptNames = prompts.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("author_document_gemini_3_8", promptNames);
        Assert.Contains("three_block_cycle_gemini_3_8", promptNames);
        Assert.Contains("review_and_patch_gemini_3_8", promptNames);
    }

    [Fact]
    public async Task McpServer_PromptsGet_AuthorDocumentGemini38_ReturnsCanonicalInstructions()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 102,
            method = "prompts/get",
            @params = new
            {
                name = "author_document_gemini_3_8",
                arguments = new
                {
                    topic = "Quantum Computing Architecture",
                    target_audience = "Enterprise Engineers",
                    include_visuals = true,
                    tone = "Technical"
                }
            }
        });

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var messages = doc.RootElement.GetProperty("result").GetProperty("messages");
        Assert.NotEmpty(messages.EnumerateArray());

        string contentText = messages[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Contains("Quantum Computing Architecture", contentText);
        Assert.Contains("Enterprise Engineers", contentText);
        Assert.Contains("MD_ENGINE_GOVERNANCE.md", contentText);
        Assert.Contains(":::smartart", contentText);
        Assert.Contains(":::chart", contentText);
    }

    [Fact]
    public async Task McpServer_PromptsGet_ThreeBlockCycleGemini38_EnforcesSection7Cadence()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 103,
            method = "prompts/get",
            @params = new
            {
                name = "three_block_cycle_gemini_3_8",
                arguments = new
                {
                    goal = "Implement OpenXML Multi-Column Section Layout",
                    current_block = "Block2"
                }
            }
        });

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var messages = doc.RootElement.GetProperty("result").GetProperty("messages");
        string text = messages[0].GetProperty("content").GetProperty("text").GetString()!;

        Assert.Contains("Block 1 (0–15 min)", text);
        Assert.Contains("Block 2 (15–30 min)", text);
        Assert.Contains("Block 3 (30–45 min)", text);
        Assert.Contains("Block 4 — Execution Phase", text);
        Assert.Contains("Non-generation block", text);
    }

    // =========================================================================
    // 2. MCP Resources (resources/list and resources/read)
    // =========================================================================

    [Fact]
    public async Task McpServer_ResourcesList_Returns3Resources()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = "{\"jsonrpc\":\"2.0\",\"id\":201,\"method\":\"resources/list\"}";

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var resources = doc.RootElement.GetProperty("result").GetProperty("resources");
        Assert.Equal(3, resources.GetArrayLength());

        var uris = resources.EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToList();
        Assert.Contains("marksmith://governance/syntax-contract", uris);
        Assert.Contains("marksmith://templates/catalog", uris);
        Assert.Contains("marksmith://schemas/patch-spec", uris);
    }

    [Fact]
    public async Task McpServer_ResourcesRead_SyntaxContract_ReturnsGovernanceDoc()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 202,
            method = "resources/read",
            @params = new
            {
                uri = "marksmith://governance/syntax-contract"
            }
        });

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var contents = doc.RootElement.GetProperty("result").GetProperty("contents");
        string text = contents[0].GetProperty("text").GetString()!;

        Assert.NotEmpty(text);
        Assert.Contains("Governance", text);
    }

    [Fact]
    public async Task McpServer_ResourcesRead_TemplatesCatalog_ReturnsJson()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 203,
            method = "resources/read",
            @params = new
            {
                uri = "marksmith://templates/catalog"
            }
        });

        string? response = await dispatcher.DispatchAsync(request);
        Assert.NotNull(response);

        using var doc = JsonDocument.Parse(response);
        var contents = doc.RootElement.GetProperty("result").GetProperty("contents");
        string json = contents[0].GetProperty("text").GetString()!;

        using var catalogDoc = JsonDocument.Parse(json);
        var themes = catalogDoc.RootElement.GetProperty("themes");
        Assert.True(themes.GetArrayLength() >= 4);
    }

    // =========================================================================
    // 3. New MCP Tools (patch_markdown, validate_markdown, diff_markdown, diff_docx)
    // =========================================================================

    [Fact]
    public async Task PatchMarkdownTool_LosslessSearchReplaceAndCriticMarkup()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();

        string initialMd = "# Document Header\n\nOriginal text content to be replaced.\n\n## Section 2\n\nMore text.";
        string mdPath = Path.Combine(_tempDir, "test_patch.md");
        await File.WriteAllTextAsync(mdPath, initialMd);

        // 1. Search and replace
        string patchReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 301,
            method = "tools/call",
            @params = new
            {
                name = "patch_markdown",
                arguments = new
                {
                    input_path = mdPath,
                    operations = new[]
                    {
                        new
                        {
                            op = "search_replace",
                            target_content = "Original text content to be replaced.",
                            replacement_content = "Surgically updated text content."
                        }
                    }
                }
            }
        });

        string? patchResp = await dispatcher.DispatchAsync(patchReq);
        Assert.NotNull(patchResp);
        Assert.Contains("true", patchResp);

        string updatedMd = await File.ReadAllTextAsync(mdPath);
        Assert.Contains("Surgically updated text content", updatedMd);
        Assert.DoesNotContain("Original text content to be replaced", updatedMd);

        // 2. CriticMarkup acceptance
        string criticMd = "# Review\n\nThis is {--old text--}{++new shiny text++} with a comment{>>review note<<}.";
        var patchService = new MarkdownPatchService();
        string accepted = MarkdownPatchService.AcceptCriticMarkup(criticMd);
        Assert.Equal("# Review\n\nThis is new shiny text with a comment.", accepted);

        string rejected = MarkdownPatchService.RejectCriticMarkup(criticMd);
        Assert.Equal("# Review\n\nThis is old text with a comment.", rejected);
    }

    [Fact]
    public async Task ValidateMarkdownTool_DetectsUnclosedContainersAndSecurityViolations()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();

        string invalidMd = "# Broken Doc\n\n:::smartart\n- Node 1\n- Node 2\n\nParagraph text without closing container.\n\n<script>alert('xss');</script>";

        string validateReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 302,
            method = "tools/call",
            @params = new
            {
                name = "validate_markdown",
                arguments = new
                {
                    markdown = invalidMd
                }
            }
        });

        string? validateResp = await dispatcher.DispatchAsync(validateReq);
        Assert.NotNull(validateResp);

        using var doc = JsonDocument.Parse(validateResp);
        var content = doc.RootElement.GetProperty("result").GetProperty("content");
        string json = content[0].GetProperty("text").GetString()!;
        using var reportDoc = JsonDocument.Parse(json);

        Assert.False(reportDoc.RootElement.GetProperty("isValid").GetBoolean());
        Assert.True(reportDoc.RootElement.GetProperty("errorsCount").GetInt32() >= 2);
    }

    [Fact]
    public async Task DiffMarkdownTool_ComputesDifferences()
    {
        var dispatcher = McpServer.CreateDefaultDispatcher();

        string diffReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 303,
            method = "tools/call",
            @params = new
            {
                name = "diff_markdown",
                arguments = new
                {
                    old_content = "Line 1\nLine 2\nLine 3",
                    new_content = "Line 1\nLine 2 Modified\nLine 3\nLine 4 Added"
                }
            }
        });

        string? diffResp = await dispatcher.DispatchAsync(diffReq);
        Assert.NotNull(diffResp);

        using var doc = JsonDocument.Parse(diffResp);
        var content = doc.RootElement.GetProperty("result").GetProperty("content");
        string json = content[0].GetProperty("text").GetString()!;
        using var resDoc = JsonDocument.Parse(json);

        Assert.True(resDoc.RootElement.GetProperty("hasChanges").GetBoolean());
        Assert.True(resDoc.RootElement.GetProperty("insertedCount").GetInt32() >= 1);
    }

    // =========================================================================
    // 4. Gemini 3.8 Heuristic Classification & Normalization
    // =========================================================================

    [Fact]
    public void LlmSourceService_ClassifiesGemini38AndStripsThoughtTokens()
    {
        var service = new LlmSourceService();
        string rawText = "<thought>\nLet's analyze the problem and design a clean solution.\n</thought>\n\n# Architecture\n\nGemini 3.8 response content.\n\nGemini can make mistakes.";

        var classification = service.Classify(rawText);
        Assert.Equal(LlmSource.Gemini, classification.Source);
        Assert.True(classification.Confidence >= 50);

        var (cleaned, fixes) = service.RepairArtifacts(rawText, classification);
        Assert.DoesNotContain("<thought>", cleaned);
        Assert.DoesNotContain("</thought>", cleaned);
        Assert.Contains("# Architecture", cleaned);
    }

    [Fact]
    public void ProviderDialectNormalizer_StripsReasoningBlocksAndNormalizesMermaid()
    {
        string rawText = "<thought>\nMulti-step reasoning chain here.\n</thought>\n\n```code snippet\ngraph TD\n    A --> B\n```";

        string normalized = ProviderDialectNormalizer.Normalize(rawText, "gemini-3.8");

        Assert.DoesNotContain("<thought>", normalized);
        Assert.DoesNotContain("Multi-step reasoning chain", normalized);
        Assert.Contains("```mermaid", normalized);
        Assert.Contains("A --> B", normalized);
    }
}
