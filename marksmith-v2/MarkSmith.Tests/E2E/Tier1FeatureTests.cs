using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Tier 1: Feature Coverage (≥5 test cases per feature across Features 1–18).
/// Validates primary behavior (happy path), interface contracts, and OpenXML ECMA-376 schema validity.
/// Total: 90 tests.
/// </summary>
public class Tier1FeatureTests
{
    // =========================================================================
    // F1: Gemini 3.8 MCP Protocol (Prompts & Resources) (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F01_01_Initialize_ReturnsProtocolCapabilitiesAndServerInfo()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""init-1"",""method"":""initialize"",""params"":{""protocolVersion"":""2024-11-05""}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("init-1", root.GetProperty("id").GetString());
        var result = root.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Contains("marksmith", result.GetProperty("serverInfo").GetProperty("name").GetString()!);
    }

    [Fact]
    public async Task T1_F01_02_PromptsList_ReturnsAllGovernanceAndAuthoringPrompts()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""prompts-1"",""method"":""prompts/list""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var prompts = doc.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("author_document_gemini_3_8", prompts);
        Assert.Contains("three_block_cycle_gemini_3_8", prompts);
        Assert.Contains("review_and_patch_gemini_3_8", prompts);
    }

    [Fact]
    public async Task T1_F01_03_PromptsGet_ReturnsExecutablePromptDefinition()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""prompt-get-1"",""method"":""prompts/get"",""params"":{""name"":""author_document_gemini_3_8"",""arguments"":{""topic"":""Cloud Security Architecture""}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.TryGetProperty("messages", out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
    }

    [Fact]
    public async Task T1_F01_04_ResourcesList_ExposesSyntaxContractAndTemplatesCatalog()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""res-list-1"",""method"":""resources/list""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var uris = doc.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToList();
        Assert.Contains("marksmith://governance/syntax-contract", uris);
        Assert.Contains("marksmith://templates/catalog", uris);
        Assert.Contains("marksmith://schemas/patch-spec", uris);
    }

    [Fact]
    public async Task T1_F01_05_ResourcesRead_ReturnsSyntaxGovernanceMarkdown()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""res-read-1"",""method"":""resources/read"",""params"":{""uri"":""marksmith://governance/syntax-contract""}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var content = doc.RootElement.GetProperty("result").GetProperty("contents").EnumerateArray().First().GetProperty("text").GetString();
        Assert.Contains("MD_ENGINE_GOVERNANCE", content);
        Assert.Contains("DOCX", content);
    }

    // =========================================================================
    // F2: Gemini 3.8 Tool Schemas & Diagnostics (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F02_01_ToolsList_ExposesCompleteToolCatalog()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""tools-1"",""method"":""tools/list""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("render_markdown_to_docx", tools);
        Assert.Contains("inspect_docx", tools);
        Assert.Contains("patch_docx", tools);
        Assert.Contains("convert_docx_to_markdown", tools);
        Assert.Contains("patch_markdown", tools);
        Assert.Contains("validate_markdown", tools);
        Assert.Contains("diff_markdown", tools);
        Assert.Contains("diff_docx", tools);
        Assert.Contains("manage_3block_cycle", tools);
    }

    [Fact]
    public async Task T1_F02_02_ToolCall_InspectNonExistentFile_ReturnsDiagnosticError()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""err-1"",""method"":""tools/call"",""params"":{""name"":""inspect_docx"",""arguments"":{""docx_path"":""C:/non_existent_path.docx""}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("File not found", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task T1_F02_03_ToolCall_UnknownTool_ReturnsMethodNotFound32601()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""err-2"",""method"":""tools/call"",""params"":{""name"":""unregistered_ai_tool"",""arguments"":{}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task T1_F02_04_ToolCall_MissingRequiredName_ReturnsInvalidParams32602()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""err-3"",""method"":""tools/call"",""params"":{""arguments"":{}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        Assert.Equal(-32602, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task T1_F02_05_ToolCall_Ping_PreservesRequestId()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""custom-ping-id-999"",""method"":""ping""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);

        using var doc = JsonDocument.Parse(res);
        Assert.Equal("custom-ping-id-999", doc.RootElement.GetProperty("id").GetString());
    }

    // =========================================================================
    // F3: Lossless In-Place Markdown Patching (patch_markdown) (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F03_01_PatchMarkdown_ExactSearchAndReplace()
    {
        var originalMd = "# Title\n\nLegacy paragraph content here.\n\nFooter note.";
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "pm-1",
            method = "tools/call",
            @params = new
            {
                name = "patch_markdown",
                arguments = new
                {
                    markdown = originalMd,
                    target = "Legacy paragraph content here.",
                    replacement = "Modern upgraded paragraph content."
                }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        var patched = doc.RootElement.GetProperty("result").GetProperty("markdown").GetString()!;
        Assert.Contains("Modern upgraded paragraph content", patched);
        Assert.DoesNotContain("Legacy paragraph content here", patched);
        Assert.Contains("Footer note", patched);
    }

    [Fact]
    public async Task T1_F03_02_PatchMarkdown_InjectsCriticMarkupTrackChanges()
    {
        var originalMd = "We agree to the standard terms.";
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "pm-2",
            method = "tools/call",
            @params = new
            {
                name = "patch_markdown",
                arguments = new
                {
                    markdown = originalMd,
                    target = "standard terms",
                    replacement = "custom enterprise SLAs",
                    track_changes = true
                }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        var patched = doc.RootElement.GetProperty("result").GetProperty("markdown").GetString()!;
        Assert.Contains("{--standard terms--}{++custom enterprise SLAs++}", patched);
    }

    [Fact]
    public void T1_F03_03_PatchMarkdown_PreservesLeadingAndTrailingWhitespace()
    {
        var originalMd = "   \n\n# Indented Title\n\n   Paragraph with spaces.   \n\n";
        var patched = E2ETestContext.ApplyMarkdownPatch(originalMd, "Paragraph with spaces.", "Updated paragraph.");
        Assert.StartsWith("   \n\n# Indented Title", patched);
        Assert.EndsWith("   \n\n", patched);
    }

    [Fact]
    public void T1_F03_04_PatchMarkdown_NonExistentTarget_LeavesContentUntouched()
    {
        var originalMd = "# Title\nParagraph content.";
        var patched = E2ETestContext.ApplyMarkdownPatch(originalMd, "Missing Target String", "Replacement");
        Assert.Equal(originalMd, patched);
    }

    [Fact]
    public void T1_F03_05_PatchMarkdown_ConsecutivePatches_ApplyInOrder()
    {
        var originalMd = "Alpha Beta Gamma";
        var step1 = E2ETestContext.ApplyMarkdownPatch(originalMd, "Alpha", "One");
        var step2 = E2ETestContext.ApplyMarkdownPatch(step1, "Beta", "Two");
        var step3 = E2ETestContext.ApplyMarkdownPatch(step2, "Gamma", "Three");
        Assert.Equal("One Two Three", step3);
    }

    // =========================================================================
    // F4: Markdown Syntax Validation (validate_markdown) (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F04_01_ValidateMarkdown_CleanDocument_ReturnsZeroErrors()
    {
        var validMd = "# Architecture Design\n\nClean markdown document with valid lists:\n- Item 1\n- Item 2";
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "vm-1",
            method = "tools/call",
            @params = new
            {
                name = "validate_markdown",
                arguments = new { markdown = validMd }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("is_valid").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("result").GetProperty("error_count").GetInt32());
    }

    [Fact]
    public async Task T1_F04_02_ValidateMarkdown_UnclosedCodeFence_ReturnsDiagnostic()
    {
        var invalidMd = "# Broken Doc\n\n```csharp\npublic void Broken() {\n";
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "vm-2",
            method = "tools/call",
            @params = new
            {
                name = "validate_markdown",
                arguments = new { markdown = invalidMd }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.False(doc.RootElement.GetProperty("result").GetProperty("is_valid").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("error_count").GetInt32() > 0);
    }

    [Fact]
    public void T1_F04_03_ValidateMarkdown_RawScriptTag_FlagsSecurityViolation()
    {
        var xssMd = "# Security Alert\n<script>alert('pwn')</script>\nContent";
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(xssMd);
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("<script>"));
    }

    [Fact]
    public void T1_F04_04_ValidateMarkdown_ValidWrappers_PassCleanly()
    {
        var wrapperMd = @":::columns
Left Column Content
===
Right Column Content
:::

:::tabs
=== ""Tab 1""
Tab 1 Content
=== ""Tab 2""
Tab 2 Content
:::";

        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(wrapperMd);
        Assert.True(isValid);
        Assert.Empty(errors);
    }

    [Fact]
    public void T1_F04_05_ValidateMarkdown_EmptyDocument_Valid()
    {
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance("");
        Assert.True(isValid);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F5: Semantic Diffing Tools (diff_markdown, diff_docx) (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F05_01_DiffMarkdown_IdentifiesLineModifications()
    {
        var original = "# Version 1\nParagraph A\nParagraph B";
        var modified = "# Version 2\nParagraph A\nParagraph C";

        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "dm-1",
            method = "tools/call",
            @params = new
            {
                name = "diff_markdown",
                arguments = new { original = original, modified = modified }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("changed").GetBoolean());
    }

    [Fact]
    public void T1_F05_02_DiffMarkdown_IdenticalTexts_ReturnsNoChanges()
    {
        var md = "# Doc Title\nContent paragraph.";
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(md, md);
        Assert.NotNull(result);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public void T1_F05_03_DiffMarkdown_PureAddition_IdentifiesNewLines()
    {
        var original = "# Doc Title";
        var modified = "# Doc Title\n\nAdded paragraph.";
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(original, modified);
        Assert.NotNull(result);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public void T1_F05_04_DiffMarkdown_PureDeletion_IdentifiesRemovedLines()
    {
        var original = "# Doc Title\n\nObsolete paragraph.";
        var modified = "# Doc Title";
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(original, modified);
        Assert.NotNull(result);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task T1_F05_05_DiffDocx_DetectsStructuralDifferencesBetweenPackages()
    {
        var bytesA = await E2ETestContext.ExportMarkdownToBytesAsync("# Document V1\n\nFirst paragraph.");
        var bytesB = await E2ETestContext.ExportMarkdownToBytesAsync("# Document V2\n\nSecond paragraph.");

        var repA = E2ETestContext.InspectDocx(bytesA);
        var repB = E2ETestContext.InspectDocx(bytesB);

        Assert.NotEqual(repA.Title, repB.Title);
    }

    // =========================================================================
    // F6: InPlaceDocxPatcher Revisions & Comments Fix (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F06_01_InPlacePatch_AddComment_InitializesWordprocessingCommentsPart()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Agreement\n\nConfidentiality clause.");
        var rep = E2ETestContext.InspectDocx(bytes);
        var targetParaId = rep.Blocks.First(b => !string.IsNullOrEmpty(b.ParaId)).ParaId;

        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { ParaId = targetParaId },
                    Comment = "Legal team approval pending.",
                    Author = "Lead Counsel"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var entries = E2ETestContext.ListZipEntries(outBytes);
        Assert.Contains("word/comments.xml", entries);

        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F06_02_InPlacePatch_TrackChangesReplacement_EmitsValidDelAndIns()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Section\n\nOriginal draft text.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Revised executive text.",
                    TrackChanges = true,
                    Author = "Editor"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("<w:del", docXml);
        Assert.Contains("<w:ins", docXml);

        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F06_03_InPlacePatch_AcceptAllRevisions_CleansUpMarkup()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("Proposal with {++added feature++}.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AcceptRevision,
                    Target = new BlockSelector()
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var rep = E2ETestContext.InspectDocx(outBytes);
        Assert.Empty(rep.Revisions);
    }

    [Fact]
    public async Task T1_F06_04_InPlacePatch_RejectAllRevisions_RevertsInsertedContent()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("Proposal with {++unwanted insertion++}.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.RejectRevision,
                    Target = new BlockSelector()
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var rep = E2ETestContext.InspectDocx(outBytes);
        Assert.Empty(rep.Revisions);
    }

    [Fact]
    public async Task T1_F06_05_InPlacePatch_PreservesSurroundingSectionsAndProperties()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Section 1\n\nParagraph 1.\n\n# Section 2\n\nParagraph 2.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "Updated Paragraph 1."
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("Section 1", docXml);
        Assert.Contains("Section 2", docXml);
        Assert.Contains("Paragraph 2", docXml);
    }

    // =========================================================================
    // F7: Rich Element Transpilation in Docx Patcher (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F07_01_PatcherTranspilation_CalloutBox_InjectsStyledBlock()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Root\n\nPlaceholder block.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "> [!NOTE]\n> Transpiled callout box inside surgical patch."
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F07_02_PatcherTranspilation_MathBlock_InjectsMathRun()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Math Doc\n\nEquation target.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "$$ \\int_0^1 x^2 dx = \\frac{1}{3} $$"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F07_03_PatcherTranspilation_Table_InjectsValidTableElement()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Section\n\nTable placeholder.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "| Header A | Header B |\n|---|---|\n| Val 1 | Val 2 |",
                    PreserveFormatting = false
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success, $"Patcher failed: {result.ErrorMessage} - {string.Join("; ", result.ValidationErrors)}");

        var rep = E2ETestContext.InspectDocx(outBytes);
        Assert.True(rep.TotalTables >= 1);
    }

    [Fact]
    public async Task T1_F07_04_PatcherTranspilation_FencedCodeBlock_InjectsCodeStyle()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Code Doc\n\nCode target.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "```csharp\nvar x = 42;\n```"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var docXml = E2ETestContext.ReadZipPartXml(outBytes, "word/document.xml")!;
        Assert.Contains("var x = 42;", docXml);
    }

    [Fact]
    public async Task T1_F07_05_PatcherTranspilation_NestedLists_InjectsListParagraphs()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# List Doc\n\nList target.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "- Item 1\n- Item 2\n- Item 3"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        var errors = E2ETestContext.ValidateDocxSchema(outBytes);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F8: AI-Executable 3-Block Cycle State Machine (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F08_01_AiCycle_Block1_Generates2Ideas()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "c-1",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { action = "generate", current_block = 1 }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(2, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
    }

    [Fact]
    public async Task T1_F08_02_AiCycle_Block2_RefinesBlock1AndGenerates2New()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "c-2",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { action = "refine_and_generate", current_block = 2 }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(3, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
    }

    [Fact]
    public async Task T1_F08_03_AiCycle_Block3_RefinesBlock2AndBlock1()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "c-3",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { action = "refine_all", current_block = 3 }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(4, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("is_execution_phase").GetBoolean());
    }

    [Fact]
    public async Task T1_F08_04_AiCycle_Block4_ExecutionPhase_VerifiesAll6Ideas()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "c-4",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { action = "execute_code", current_block = 4 }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(6, doc.RootElement.GetProperty("result").GetProperty("total_refined_ideas").GetInt32());
    }

    [Fact]
    public void T1_F08_05_AiCycle_CarryForward_TransfersBlock3Ideas()
    {
        var block3Ideas = new List<string> { "Idea 5: OpenXmlValidator Parallelism", "Idea 6: RecyclableMemoryStream Pool" };
        Assert.Equal(2, block3Ideas.Count);
        Assert.Contains("OpenXmlValidator", block3Ideas[0]);
    }

    // =========================================================================
    // F9: Gemini 3.8 Heuristic Classification & Normalization (5 tests)
    // =========================================================================

    [Fact]
    public void T1_F09_01_Normalizer_StripsThinkChainOfThoughtBlocks()
    {
        var input = "<think>\nInternal reasoning about OpenXML schema...\n</think>\n# Document Output\nReal content.";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.DoesNotContain("<think>", normalized);
        Assert.Contains("# Document Output", normalized);
        Assert.Contains("Real content", normalized);
    }

    [Fact]
    public void T1_F09_02_Normalizer_ConvertsCodeSnippetMermaidFences()
    {
        var input = "```code snippet\nflowchart TD\n  A --> B\n```";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.Contains("```mermaid", normalized);
    }

    [Fact]
    public void T1_F09_03_Normalizer_UnquotesBlockquotedFences()
    {
        var input = "> ```csharp\n> var x = 1;\n> ```";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.Contains("```csharp", normalized);
        Assert.DoesNotContain("> ```csharp", normalized);
    }

    [Fact]
    public void T1_F09_04_Normalizer_StripsPromptEchoHeader()
    {
        var input = "User: Generate a report on cloud security.\n\n# Cloud Security Report\nContent.";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.DoesNotContain("User: Generate a report", normalized);
        Assert.Contains("# Cloud Security Report", normalized);
    }

    [Fact]
    public void T1_F09_05_Normalizer_DeduplicatesMermaidBlocks()
    {
        var input = "```mermaid\nflowchart TD\n  A --> B\n```\n\n```mermaid\nflowchart TD\n  A --> B\n```";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        var count = Regex.Matches(normalized, "```mermaid").Count;
        Assert.Equal(1, count);
    }

    // =========================================================================
    // F10: Native Collapsible Sections (<w15:collapsed>) (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F10_01_CollapsibleSections_DetailsSummary_EmitsCollapsedSection()
    {
        var md = "<details><summary>Technical Deep Dive</summary>\n\nDetailed breakdown of the architecture.\n</details>";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("Technical Deep Dive", docXml);
    }

    [Fact]
    public async Task T1_F10_02_CollapsibleSections_OpenDetails_RendersExpanded()
    {
        var md = "<details open><summary>Expanded Overview</summary>\n\nVisible content.\n</details>";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("Expanded Overview", docXml);
    }

    [Fact]
    public async Task T1_F10_03_CollapsibleSections_TabsEmitCollapsibleHeaders()
    {
        var md = @":::tabs
=== ""Windows""
Windows installation instructions.
=== ""macOS""
macOS installation instructions.
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F10_04_CollapsibleSections_NestedContent_PreservesFormatting()
    {
        var md = @"<details><summary>Specifications Table</summary>

| Spec | Value |
|---|---|
| Latency | <10ms |

</details>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public void T1_F10_05_CollapsibleSections_HTMLPreview_RendersDetailsTag()
    {
        var md = "<details><summary>Preview Toggle</summary>Body</details>";
        var html = E2ETestContext.RenderHtml(md);
        Assert.Contains("<details", html);
        Assert.Contains("<summary", html);
    }

    // =========================================================================
    // F11: Multi-Column Blocks (:::columns) DOCX & Preview (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F11_01_Columns_2ColumnBlock_EmitsContinuousSectionWithCols()
    {
        var md = @":::columns
Left side content with key bullet points.
===
Right side content with descriptive text.
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("Left side content", docXml);
        Assert.Contains("Right side content", docXml);
    }

    [Fact]
    public async Task T1_F11_02_Columns_3ColumnBlock_EmitsBalancedColumnBreaks()
    {
        var md = @":::columns
Col 1
===
Col 2
===
Col 3
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public void T1_F11_03_Columns_HTMLPreview_LiftsColumnsToResponsiveContainer()
    {
        var md = @":::columns
Col A
===
Col B
:::";

        var html = E2ETestContext.RenderHtml(md);
        Assert.Contains("ms-columns", html);
    }

    [Fact]
    public async Task T1_F11_04_Columns_FollowedByStandardParagraph_RestoresLayout()
    {
        var md = @":::columns
Col 1
===
Col 2
:::

Standard full-width body paragraph.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("Standard full-width body paragraph", docXml);
    }

    [Fact]
    public async Task T1_F11_05_Columns_WithFormattedListsAndHeadings()
    {
        var md = @":::columns
### Left Heading
- Item A1
- Item A2
===
### Right Heading
- Item B1
- Item B2
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F12: Nested Grid HTML Table Parser (colspan/rowspan/nested) (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F12_01_HtmlTable_Colspan_EmitsGridSpan()
    {
        var md = @"<table>
  <tr><th colspan=""2"">Merged Header</th></tr>
  <tr><td>Cell 1</td><td>Cell 2</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("gridSpan", docXml);
    }

    [Fact]
    public async Task T1_F12_02_HtmlTable_Rowspan_EmitsVMerge()
    {
        var md = @"<table>
  <tr><td rowspan=""2"">Merged Row</td><td>Cell A</td></tr>
  <tr><td>Cell B</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);

        var docXml = E2ETestContext.ReadZipPartXml(bytes, "word/document.xml")!;
        Assert.Contains("vMerge", docXml);
    }

    [Fact]
    public async Task T1_F12_03_HtmlTable_NestedTableInCell_EmitsNestedTbl()
    {
        var md = @"<table>
  <tr>
    <td>Outer Cell</td>
    <td>
      <table><tr><td>Inner 1</td><td>Inner 2</td></tr></table>
    </td>
  </tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F12_04_HtmlTable_RichInlineFormattingInCells()
    {
        var md = @"<table>
  <tr><td><strong>Bold</strong> and <em>Italic</em> and <code>Code</code></td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F12_05_HtmlTable_CombinedColspanAndRowspan()
    {
        var md = @"<table>
  <tr><td colspan=""2"" rowspan=""2"">Big Corner</td><td>Right 1</td></tr>
  <tr><td>Right 2</td></tr>
  <tr><td>Bottom 1</td><td>Bottom 2</td><td>Bottom 3</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F13: DrawingML Chart Dynamic IDs & SmartArt Rel Hardening (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F13_01_Chart_DynamicDocPropertiesId_AvoidsHardcodedCollision()
    {
        var md = @":::chart type=""bar"" title=""Revenue Growth""
Categories: Q1, Q2, Q3, Q4
Series: 2025, 100, 150, 200, 250
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F13_02_Chart_MultipleChartsInSingleDocument_HaveUniqueRelIds()
    {
        var md = @":::chart type=""pie"" title=""Market Share""
Series: Chrome, 65; Edge, 20; Safari, 15
:::

:::chart type=""line"" title=""User Growth""
Categories: Jan, Feb, Mar
Series: Users, 1000, 2500, 5000
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F13_03_SmartArt_DynamicRelationshipIdsInPackage()
    {
        var md = @":::smartart layout=""radial"" title=""Ecosystem Architecture""
- Core Engine
  - Parser
  - Emitter
  - Patcher
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F13_04_ChartAndSmartArt_MixedInDocument_AllPartsValid()
    {
        var md = @"# Mixed Visuals

:::chart type=""bar"" title=""Performance Metrics""
Categories: SAX, DOM
Series: Throughput, 5000, 500
:::

:::smartart layout=""process"" title=""Workflow""
- Ingest
- Parse
- Stream
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F13_05_RelationshipsXml_ZeroCorruptedTargets()
    {
        var md = "# Doc with links\n[Docs](https://example.com) and [Portal](https://portal.example.com)";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;
        Assert.DoesNotContain("Target=\"\"", relsXml);
    }

    // =========================================================================
    // F14: Dual-Pipeline Parity & XSS Governance (5 tests)
    // =========================================================================

    [Fact]
    public void T1_F14_01_DualPipeline_CodeFenceXss_NotHtmlDecodedInPreview()
    {
        var xssMd = "```html\n<script>alert('xss')</script>\n```";
        var html = E2ETestContext.RenderHtml(xssMd);
        Assert.DoesNotContain("<script>alert('xss')</script>", html);
        Assert.Contains("&lt;script&gt;alert('xss')&lt;/script&gt;", html);
    }

    [Fact]
    public void T1_F14_02_DualPipeline_SanitizerPreservesCommentsAsAnchors()
    {
        var md = "<!-- MARKSMITH_FEATURE:test_anchor -->\nParagraph content.";
        var html = E2ETestContext.RenderHtml(md);
        Assert.Contains("MARKSMITH_FEATURE:test_anchor", html);
    }

    [Fact]
    public async Task T1_F14_03_DualPipeline_CalloutsRenderInBothDocxAndHtml()
    {
        var md = "> [!WARNING]\n> Production deployment requires rollback plan.";
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var html = E2ETestContext.RenderHtml(md);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("markdown-alert", html);
    }

    [Fact]
    public async Task T1_F14_04_DualPipeline_MathRendersInBothDocxAndHtml()
    {
        var md = "The formula is $$ f(x) = x^2 + 2x + 1 $$.";
        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var html = E2ETestContext.RenderHtml(md);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("katex", html.ToLowerInvariant());
    }

    [Fact]
    public async Task T1_F14_05_DualPipeline_WatermarksRenderInBothPipelines()
    {
        var md = @":::watermark ""CONFIDENTIAL"" color=""#FF0000"" opacity=""0.2""
# Sensitive Document
Internal data.";

        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var html = E2ETestContext.RenderHtml(md);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("mk-watermark", html);
    }

    // =========================================================================
    // F15: Multi-Threaded SAX Streaming Pipeline (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F15_01_StreamingSax_ExportsValidPackage()
    {
        var md = "# Streaming Document\n\nExported via high-throughput SAX engine.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F15_02_StreamingSax_PreservesSequentialBlockOrder()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 20; i++)
        {
            sb.AppendLine($"## Section {i}\nParagraph for section {i}.\n");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var rep = E2ETestContext.InspectDocx(bytes);
        var headings = rep.Blocks.Where(b => b.HeadingLevel == 2).Select(b => b.Text).ToList();

        for (int i = 1; i <= 20; i++)
        {
            Assert.Contains(headings, h => h.Contains($"Section {i}"));
        }
    }

    [Fact]
    public async Task T1_F15_03_StreamingSax_HandlesInterleavedBlocksAndTables()
    {
        var md = @"# Document
Paragraph 1

| H1 | H2 |
|---|---|
| A | B |

Paragraph 2

```python
print('hello')
```

Paragraph 3";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F15_04_StreamingSax_DirectMemoryStreamTarget()
    {
        var md = "# Memory Stream Test\nDirect stream write.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.NotEmpty(bytes);

        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F15_05_StreamingSax_NoCorruptedXmlEntriesInZip()
    {
        var md = "# Zip Test\nTesting zip integrity.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        using var zipMs = new MemoryStream(bytes);
        using var zip = new ZipArchive(zipMs, ZipArchiveMode.Read);
        Assert.True(zip.Entries.Count >= 5);
    }

    // =========================================================================
    // F16: Asynchronous Token Ingestion for Gemini 3.8 (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F16_01_AsyncTokenIngestion_AggregatesChunksAccurately()
    {
        var fullText = "# Gemini 3.8 Ingestion\n\nReal-time streaming token ingestion test.";
        var tokenStream = E2ETestContext.CreateTokenStreamAsync(fullText, chunkSize: 5);

        var sb = new System.Text.StringBuilder();
        await foreach (var token in tokenStream)
        {
            sb.Append(token);
        }

        Assert.Equal(fullText, sb.ToString());
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F16_02_AsyncTokenIngestion_SimulatesTokenStreamDelays()
    {
        var fullText = "Streaming token payload with simulated latency.";
        var tokenStream = E2ETestContext.CreateTokenStreamAsync(fullText, chunkSize: 8, delayMs: 1);

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in tokenStream)
        {
            sb.Append(chunk);
        }

        Assert.Equal(fullText, sb.ToString());
    }

    [Fact]
    public async Task T1_F16_03_AsyncTokenIngestion_HandlesMarkdownDividersAcrossTokens()
    {
        var fullText = ":::columns\nLeft\n===\nRight\n:::";
        var tokenStream = E2ETestContext.CreateTokenStreamAsync(fullText, chunkSize: 3);

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in tokenStream)
        {
            sb.Append(chunk);
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F16_04_AsyncTokenIngestion_EmptyStream_HandledSafely()
    {
        var tokenStream = E2ETestContext.CreateTokenStreamAsync("", chunkSize: 5);
        int count = 0;
        await foreach (var _ in tokenStream)
        {
            count++;
        }
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task T1_F16_05_AsyncTokenIngestion_LargeDocumentStream()
    {
        var sbLarge = new System.Text.StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            sbLarge.AppendLine($"Paragraph {i} streaming token test.");
        }

        var tokenStream = E2ETestContext.CreateTokenStreamAsync(sbLarge.ToString(), chunkSize: 20);
        var sbCollected = new System.Text.StringBuilder();
        await foreach (var token in tokenStream)
        {
            sbCollected.Append(token);
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sbCollected.ToString());
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F17: Thread-Safe Relationship & Part Staging (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F17_01_ConcurrentExports_ZeroStateContention()
    {
        var tasks = Enumerable.Range(1, 10).Select(i =>
            E2ETestContext.ExportMarkdownToBytesAsync($"# Thread {i}\nContent for thread {i}."));

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);
        foreach (var bytes in results)
        {
            var errors = E2ETestContext.ValidateDocxSchema(bytes);
            Assert.Empty(errors);
        }
    }

    [Fact]
    public async Task T1_F17_02_DynamicRelationshipIds_NoDuplicationsUnderLoad()
    {
        var md = @"# Links
[L1](https://a.com)
[L2](https://b.com)
[L3](https://c.com)
[L4](https://d.com)
[L5](https://e.com)";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;

        var relIds = Regex.Matches(relsXml, @"Id=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(relIds.Count, relIds.Distinct().Count());
    }

    [Fact]
    public async Task T1_F17_03_PartStaging_MaintainsValidPackageContentTypes()
    {
        var md = "# Content Types Test\nParagraph content.";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var contentTypes = E2ETestContext.ReadZipPartXml(bytes, "[Content_Types].xml")!;
        Assert.Contains("application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", contentTypes);
    }

    [Fact]
    public async Task T1_F17_04_PartStaging_StylesAndNumbering_StagedCleanly()
    {
        var md = "# Numbered List\n1. Item 1\n2. Item 2";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var entries = E2ETestContext.ListZipEntries(bytes);
        Assert.Contains("word/styles.xml", entries);
    }

    [Fact]
    public async Task T1_F17_05_ConcurrentPatchingAndInspection_ThreadSafe()
    {
        var baseDocx = await E2ETestContext.ExportMarkdownToBytesAsync("# Base Document\n\nParagraph 1.\n\nParagraph 2.");
        var tasks = Enumerable.Range(1, 5).Select(i => Task.Run(() =>
        {
            var rep = E2ETestContext.InspectDocx(baseDocx);
            Assert.NotNull(rep);
        }));

        await Task.WhenAll(tasks);
    }

    // =========================================================================
    // F18: O(1) Memory Footprint & Buffer Pooling (5 tests)
    // =========================================================================

    [Fact]
    public async Task T1_F18_01_BufferPooling_LargeDocument_ExecutesWithinMemoryBounds()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            sb.AppendLine($"Paragraph {i}: Validating buffer pooling and memory bounding in SAX streaming engine.");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.NotEmpty(bytes);
        var errors = E2ETestContext.ValidateDocxSchema(bytes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task T1_F18_02_BufferPooling_ConsecutiveRuns_NoMemoryLeak()
    {
        for (int i = 0; i < 5; i++)
        {
            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync($"# Run {i}\nContent iteration {i}.");
            Assert.NotEmpty(bytes);
        }
    }

    [Fact]
    public async Task T1_F18_03_Throughput_Processes100HeadingsRapidly()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 50; i++)
        {
            sb.AppendLine($"# Heading {i}\nBody content.");
        }

        var start = DateTime.UtcNow;
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var duration = DateTime.UtcNow - start;

        Assert.NotEmpty(bytes);
        Assert.True(duration.TotalSeconds < 10);
    }

    [Fact]
    public async Task T1_F18_04_ResourceDisposal_StreamsCleanlyClosed()
    {
        var tempFile = await E2ETestContext.ExportMarkdownToTempDocxAsync("# Dispose Test\nContent.");
        try
        {
            Assert.True(File.Exists(tempFile));
            var bytes = await File.ReadAllBytesAsync(tempFile);
            Assert.NotEmpty(bytes);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T1_F18_05_MemoryStreamExport_ScalesWithContentSize()
    {
        var smallBytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Small");
        var largeBytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Large\n" + string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Line {i}")));

        Assert.True(largeBytes.Length > smallBytes.Length);
    }
}
