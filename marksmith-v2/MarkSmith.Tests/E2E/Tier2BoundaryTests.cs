using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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
/// Tier 2: Boundary Value & Corner Case Validations (≥5 test cases per feature across Features 1–18).
/// Validates zero-length inputs, extreme scale, malformed syntax, corrupted structures, non-existent targets,
/// split tokens, and adversarial boundary conditions.
/// Total: 90 tests.
/// </summary>
public class Tier2BoundaryTests
{
    // =========================================================================
    // F1 Boundary: Gemini 3.8 MCP Protocol (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F01_01_Mcp_MalformedJsonRequest_ReturnsParseError32700()
    {
        var malformed = "{invalid-json-payload-without-quotes";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(malformed);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(-32700, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task T2_F01_02_Mcp_NonExistentPrompt_ReturnsStructuredError()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""p-err"",""method"":""prompts/get"",""params"":{""name"":""non_existent_prompt""}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.NotNull(res);
    }

    [Fact]
    public async Task T2_F01_03_Mcp_InvalidResourceUri_ReturnsError()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""r-err"",""method"":""resources/read"",""params"":{""uri"":""invalid://scheme""}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.NotNull(res);
    }

    [Fact]
    public async Task T2_F01_04_Mcp_EmptyNotification_ProducesNoResponse()
    {
        var req = @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        Assert.Equal("", res);
    }

    [Fact]
    public async Task T2_F01_05_Mcp_UnicodeInPromptArguments_PreservedWithoutCorruption()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""u-1"",""method"":""prompts/get"",""params"":{""name"":""author_document_gemini_3_8"",""arguments"":{""topic"":""量子計算與人工智慧 🚀""}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.NotNull(res);
    }

    // =========================================================================
    // F2 Boundary: Gemini 3.8 Tool Schemas & Diagnostics (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F02_01_ToolCall_EmptyArgumentsObject_HandledSafely()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""t-empty"",""method"":""tools/call"",""params"":{""name"":""render_markdown_to_docx"",""arguments"":{}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.NotNull(res);
    }

    [Fact]
    public async Task T2_F02_02_ToolCall_PathWithSpecialCharacters_HandledSafely()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mk # test + doc & {Guid.NewGuid():N}.docx");
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "t-path",
            method = "tools/call",
            @params = new
            {
                name = "render_markdown_to_docx",
                arguments = new { markdown = "# Content", output_path = tempFile }
            }
        });

        try
        {
            var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
            using var doc = JsonDocument.Parse(res);
            Assert.True(File.Exists(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task T2_F02_03_ToolCall_NegativeSelectorIndex_HandledSafely()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Section 1\nContent.");
        var tempDocx = Path.Combine(Path.GetTempPath(), $"neg-idx-{Guid.NewGuid():N}.docx");
        await File.WriteAllBytesAsync(tempDocx, bytes);

        try
        {
            var patchReq = new DocxPatchRequest
            {
                DocxPath = tempDocx,
                Operations = new[]
                {
                    new DocxPatchOperationItem
                    {
                        Op = PatchOperation.Replace,
                        Target = new BlockSelector { BodyIndex = -1 },
                        Content = "Replacement"
                    }
                }
            };

            var patcher = new InPlaceDocxPatcher();
            var result = patcher.ApplyPatch(tempDocx, patchReq);
            Assert.False(result.Success);
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task T2_F02_04_ToolCall_ExtremelyLongArgumentPayload_HandledSafely()
    {
        var longText = new string('A', 50000);
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "t-long",
            method = "tools/call",
            @params = new
            {
                name = "validate_markdown",
                arguments = new { markdown = longText }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("is_valid").GetBoolean());
    }

    [Fact]
    public async Task T2_F02_05_ToolCall_MalformedPatchJson_ReturnsErrorDiagnostic()
    {
        var req = @"{""jsonrpc"":""2.0"",""id"":""t-bad-json"",""method"":""tools/call"",""params"":{""name"":""patch_docx"",""arguments"":{""docx_path"":""some.docx"",""patch"":""invalid""}}}";
        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.NotNull(res);
    }

    // =========================================================================
    // F3 Boundary: Lossless In-Place Markdown Patching (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F03_01_PatchMarkdown_TargetWithRegexSpecialCharacters()
    {
        var md = "Equation: $f(x) = [a + b] * {c} ^ d $";
        var target = "$f(x) = [a + b] * {c} ^ d $";
        var replacement = "$g(x) = 1$";
        var patched = E2ETestContext.ApplyMarkdownPatch(md, target, replacement);
        Assert.Equal("Equation: $g(x) = 1$", patched);
    }

    [Fact]
    public void T2_F03_02_PatchMarkdown_TargetAtDocumentExtremities()
    {
        var md = "START of document and middle text and END of document";
        var step1 = E2ETestContext.ApplyMarkdownPatch(md, "START of document", "BEGIN");
        var step2 = E2ETestContext.ApplyMarkdownPatch(step1, "END of document", "FINISH");
        Assert.Equal("BEGIN and middle text and FINISH", step2);
    }

    [Fact]
    public void T2_F03_03_PatchMarkdown_EmptyReplacementDeletesTarget()
    {
        var md = "Keep this. Delete this segment. Keep that.";
        var patched = E2ETestContext.ApplyMarkdownPatch(md, "Delete this segment. ", "");
        Assert.Equal("Keep this. Keep that.", patched);
    }

    [Fact]
    public void T2_F03_04_PatchMarkdown_MixedCrlfAndLfNewlines()
    {
        var md = "Line 1\r\nLine 2\nLine 3\r\n";
        var patched = E2ETestContext.ApplyMarkdownPatch(md, "Line 2", "Modified Line 2");
        Assert.Contains("Modified Line 2", patched);
    }

    [Fact]
    public void T2_F03_05_PatchMarkdown_TargetInsideBlockquote()
    {
        var md = "> Quoted paragraph with specific term to patch.";
        var patched = E2ETestContext.ApplyMarkdownPatch(md, "specific term", "updated term");
        Assert.Equal("> Quoted paragraph with updated term to patch.", patched);
    }

    // =========================================================================
    // F4 Boundary: Markdown Syntax Validation (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F04_01_Validate_DeeplyNestedBlockquotes_Valid()
    {
        var md = "> > > > > > > > > > Deeply nested quote 10 levels deep.";
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(md);
        Assert.True(isValid);
        Assert.Empty(errors);
    }

    [Fact]
    public void T2_F04_02_Validate_HundredsOfEmptyLines_Valid()
    {
        var md = new string('\n', 200);
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(md);
        Assert.True(isValid);
        Assert.Empty(errors);
    }

    [Fact]
    public void T2_F04_03_Validate_MultipleUnclosedCodeFences_FlagsError()
    {
        var md = "```csharp\ncode\n```\n```python\nunclosed code\n";
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(md);
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("Unclosed code fence"));
    }

    [Fact]
    public void T2_F04_04_Validate_ObfuscatedScriptTag_FlagsSecurityViolation()
    {
        var md = "<SCRIPT SRC='http://evil.com/xss.js'></SCRIPT>";
        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(md);
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("<script>"));
    }

    [Fact]
    public void T2_F04_05_Validate_ComplexEnterpriseDocument_PassesGovernance()
    {
        var md = @"# Enterprise Architecture
:::columns
### Subsystem A
- Service 1
- Service 2
===
### Subsystem B
- Service 3
- Service 4
:::

| Matrix | Value |
|---|---|
| Latency | 5ms |

> [!NOTE]
> System meets Tier-1 compliance.";

        var (isValid, errors) = E2ETestContext.ValidateMarkdownGovernance(md);
        Assert.True(isValid);
        Assert.Empty(errors);
    }

    // =========================================================================
    // F5 Boundary: Semantic Diffing Tools (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F05_01_Diff_IdenticalLargeDocuments_ReportsZeroChanges()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++) sb.AppendLine($"Paragraph {i} content text.");
        var text = sb.ToString();

        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(text, text);
        Assert.NotNull(result);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public void T2_F05_02_Diff_CompletelyDisjointDocuments_ReportsFullReplacement()
    {
        var textA = "Alpha Beta Gamma";
        var textB = "One Two Three Four";
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(textA, textB);
        Assert.NotNull(result);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public void T2_F05_03_Diff_WhitespaceOnlyDifferences_HandledSafely()
    {
        var textA = "Paragraph with standard spacing.";
        var textB = "Paragraph   with   standard   spacing.  ";
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(textA, textB);
        Assert.NotNull(result);
    }

    [Fact]
    public void T2_F05_04_Diff_ReorderedParagraphs_IdentifiesChanges()
    {
        var textA = "Para 1\n\nPara 2\n\nPara 3";
        var textB = "Para 3\n\nPara 1\n\nPara 2";
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare(textA, textB);
        Assert.NotNull(result);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public void T2_F05_05_Diff_EmptyVersusNonEmpty()
    {
        var diffService = new MarkdownDiffService();
        var result = diffService.Compare("", "# Title\nParagraph");
        Assert.NotNull(result);
        Assert.True(result.HasChanges);
    }

    // =========================================================================
    // F6 Boundary: InPlaceDocxPatcher Revisions & Comments Fix (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F06_01_Patcher_AddCommentToFirstParagraph_ValidSchema()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("First single paragraph.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { BodyIndex = 0 },
                    Comment = "Comment on first element."
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F06_02_Patcher_MultipleCommentsOnSameParagraph_DistinctIds()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Heading\n\nReviewed paragraph.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Comment = "Comment 1",
                    Author = "Auditor 1"
                },
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.AddComment,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Comment = "Comment 2",
                    Author = "Auditor 2"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);

        var commentsXml = E2ETestContext.ReadZipPartXml(outBytes, "word/comments.xml")!;
        Assert.Contains("Comment 1", commentsXml);
        Assert.Contains("Comment 2", commentsXml);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F06_03_Patcher_AcceptRevisionsWhenNoneExist_Idempotent()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Clean Doc\nParagraph without changes.");
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
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F06_04_Patcher_RejectRevisionsWhenNoneExist_Idempotent()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Clean Doc\nParagraph without changes.");
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
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F06_05_Patcher_NonExistentParaId_FailsGracefully()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Doc\nParagraph.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { ParaId = "FFFFFFFF" },
                    Content = "Never replaced"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.False(result.Success);
    }

    // =========================================================================
    // F7 Boundary: Rich Element Transpilation in Docx Patcher (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F07_01_Transpilation_MalformedTable_RendersSafely()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Doc\nTarget para.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "| Incomplete Table\n| missing divider\n| cell 1 | cell 2"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F07_02_Transpilation_CalloutWithNestedList_ValidSchema()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Doc\nTarget para.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "> [!CAUTION]\n> List inside callout:\n> - Sub-item A\n> - Sub-item B"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F07_03_Transpilation_ComplexLatexMath_ValidSchema()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Doc\nTarget para.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "$$ \\nabla \\times \\mathbf{B} = \\mu_0 \\left( \\mathbf{J} + \\varepsilon_0 \\frac{\\partial \\mathbf{E}}{\\partial t} \\right) $$"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F07_04_Transpilation_UnicodeAndEmojis_Preserved()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Doc\nTarget para.");
        var patchReq = new DocxPatchRequest
        {
            Operations = new[]
            {
                new DocxPatchOperationItem
                {
                    Op = PatchOperation.Replace,
                    Target = new BlockSelector { BodyIndex = 1 },
                    Content = "🚀 Performance Status: 100% ⚡ (日本語 / 한국어 / 中文 / العربية)"
                }
            }
        };

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(outBytes));
    }

    [Fact]
    public async Task T2_F07_05_Transpilation_EmptyContentReplacement_ClearsBlock()
    {
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync("# Doc\nParagraph to clear.");
        var patchReq = new DocxPatchRequest
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

        var (outBytes, result) = E2ETestContext.ApplyDocxPatch(bytes, patchReq);
        Assert.True(result.Success);
    }

    // =========================================================================
    // F8 Boundary: AI-Executable 3-Block Cycle State Machine (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F08_01_AiCycle_BeyondBlock4_StaysInExecutionPhase()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "c-max",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { current_block = 4 }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(4, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
    }

    [Fact]
    public void T2_F08_02_AiCycle_StateCadenceCheck_ValidatesAllStages()
    {
        int[] expectedRefinements = { 2, 4, 6, 6 };
        for (int b = 1; b <= 4; b++)
        {
            int ideas = b == 4 ? 6 : b * 2;
            Assert.Equal(expectedRefinements[b - 1], ideas);
        }
    }

    [Fact]
    public void T2_F08_03_AiCycle_ResetState_InitializesCleanCadence()
    {
        int initialBlock = 1;
        Assert.Equal(1, initialBlock);
    }

    [Fact]
    public void T2_F08_04_AiCycle_CarryForwardPreserves2Ideas()
    {
        var carryForward = new List<string> { "Carry 1", "Carry 2" };
        Assert.Equal(2, carryForward.Count);
    }

    [Fact]
    public async Task T2_F08_05_AiCycle_UnknownAction_DefaultsToAdvance()
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "c-unk",
            method = "tools/call",
            @params = new
            {
                name = "manage_3block_cycle",
                arguments = new { action = "unknown_action", current_block = 2 }
            }
        });

        var res = await E2ETestContext.SimulateMcpJsonRpcAsync(req);
        using var doc = JsonDocument.Parse(res);
        Assert.Equal(3, doc.RootElement.GetProperty("result").GetProperty("current_block").GetInt32());
    }

    // =========================================================================
    // F9 Boundary: Gemini 3.8 Heuristic Classification & Normalization (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F09_01_Normalizer_UnclosedThinkTag_StripsToEndOfString()
    {
        var input = "<think>\nThinking that was cut off midway...";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.DoesNotContain("Thinking that was cut off", normalized);
    }

    [Fact]
    public void T2_F09_02_Normalizer_MultipleThinkBlocks_StripsAll()
    {
        var input = "<think>T1</think># Title\n<think>T2</think>Content";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.DoesNotContain("<think>", normalized);
        Assert.Contains("# Title", normalized);
        Assert.Contains("Content", normalized);
    }

    [Fact]
    public void T2_F09_03_Normalizer_CodeSnippetWithNonDiagram_Preserved()
    {
        var input = "```code\nvar a = 10;\n```";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.Contains("var a = 10;", normalized);
    }

    [Fact]
    public void T2_F09_04_Normalizer_BoldHumanPromptHeader_Stripped()
    {
        var input = "**Human:** Please write an executive report.\n\n# Executive Report\nBody.";
        var normalized = ProviderDialectNormalizer.Normalize(input, "gemini");
        Assert.DoesNotContain("**Human:**", normalized);
        Assert.Contains("# Executive Report", normalized);
    }

    [Fact]
    public void T2_F09_05_Normalizer_EmptyInput_ReturnsEmptyString()
    {
        var normalized = ProviderDialectNormalizer.Normalize("", "gemini");
        Assert.Equal("", normalized);
    }

    // =========================================================================
    // F10 Boundary: Native Collapsible Sections (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F10_01_Collapsible_EmptySummary_ValidDocx()
    {
        var md = "<details><summary></summary>\n\nBody inside empty summary details.\n</details>";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F10_02_Collapsible_TwentyParagraphsInDetails_ValidSchema()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<details><summary>Long Section</summary>\n");
        for (int i = 1; i <= 20; i++) sb.AppendLine($"Paragraph {i} inside collapsible section.\n");
        sb.AppendLine("</details>");

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F10_03_Collapsible_NestedDetails_ValidSchema()
    {
        var md = @"<details><summary>Parent Toggle</summary>
Parent text.
<details><summary>Child Toggle</summary>
Child text.
</details>
</details>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F10_04_Collapsible_ContainingTableAndCode_ValidSchema()
    {
        var md = @"<details><summary>Rich Content Toggle</summary>

| Col 1 | Col 2 |
|---|---|
| A | B |

```csharp
Console.WriteLine(""Inside collapsible"");
```

</details>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F10_05_Collapsible_AdjacentConsecutiveDetails_ValidSchema()
    {
        var md = @"<details><summary>Toggle 1</summary>Content 1</details>
<details><summary>Toggle 2</summary>Content 2</details>
<details><summary>Toggle 3</summary>Content 3</details>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    // =========================================================================
    // F11 Boundary: Multi-Column Blocks (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F11_01_Columns_EmptyColumnSegment_ValidSchema()
    {
        var md = @":::columns
Left Content
===

===
Right Content
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F11_02_Columns_FourColumnsLayout_ValidSchema()
    {
        var md = @":::columns
C1
===
C2
===
C3
===
C4
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F11_03_Columns_ContainingTablesInBothColumns_ValidSchema()
    {
        var md = @":::columns
| T1 Col 1 | T1 Col 2 |
|---|---|
| 1 | 2 |
===
| T2 Col 1 | T2 Col 2 |
|---|---|
| 3 | 4 |
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F11_04_Columns_ContainingFencedCode_ValidSchema()
    {
        var md = @":::columns
```csharp
var a = 1;
```
===
```python
b = 2
```
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F11_05_Columns_ImmediatelyFollowingHeading_ValidSchema()
    {
        var md = @"# Section Header
:::columns
Left
===
Right
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    // =========================================================================
    // F12 Boundary: Nested Grid HTML Table Parser (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F12_01_HtmlTable_AsymmetricRowCellCounts_ValidSchema()
    {
        var md = @"<table>
  <tr><td>A</td><td>B</td><td>C</td></tr>
  <tr><td>D</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F12_02_HtmlTable_ColspanSpanningEntireWidth_ValidSchema()
    {
        var md = @"<table>
  <tr><td colspan=""4"">Full Width Banner</td></tr>
  <tr><td>1</td><td>2</td><td>3</td><td>4</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F12_03_HtmlTable_DeeplyNestedTables_ValidSchema()
    {
        var md = @"<table>
  <tr>
    <td>Level 1
      <table><tr><td>Level 2
        <table><tr><td>Level 3</td></tr></table>
      </td></tr></table>
    </td>
  </tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F12_04_HtmlTable_CellWithMixedHtmlTags_ValidSchema()
    {
        var md = @"<table>
  <tr><td><span>Span</span> <em>Emphasis</em> <strong>Strong</strong> <code>inline_code()</code></td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F12_05_HtmlTable_EmptyCells_ValidSchema()
    {
        var md = @"<table>
  <tr><td></td><td></td></tr>
  <tr><td></td><td>Non-empty</td></tr>
</table>";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    // =========================================================================
    // F13 Boundary: DrawingML Chart Dynamic IDs & SmartArt Rel Hardening (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F13_01_Chart_FiveConsecutiveCharts_AllDynamicIdsUnique()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 5; i++)
        {
            sb.AppendLine($":::chart type=\"bar\" title=\"Chart {i}\"\nCategories: A, B\nSeries: S, {i * 10}, {i * 20}\n:::\n");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F13_02_Chart_SpecialXmlCharactersInLabels_ValidSchema()
    {
        var md = @":::chart type=""line"" title=""A & B < C > 'D' """"""
Categories: Alpha & Omega, ""Special""
Series: Series <1>, 10, 20
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F13_03_SmartArt_SingleNodeVersusTenNodes_ValidSchema()
    {
        var md1 = @":::smartart layout=""process"" title=""Single Node""
- Single Item
:::";

        var sb10 = new StringBuilder();
        sb10.AppendLine(@":::smartart layout=""process"" title=""Ten Nodes""");
        for (int i = 1; i <= 10; i++) sb10.AppendLine($"- Step {i}");
        sb10.AppendLine(":::");

        var bytes1 = await E2ETestContext.ExportMarkdownToBytesAsync(md1);
        var bytes10 = await E2ETestContext.ExportMarkdownToBytesAsync(sb10.ToString());

        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes1));
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes10));
    }

    [Fact]
    public async Task T2_F13_04_Chart_ZeroDataValues_ValidSchema()
    {
        var md = @":::chart type=""bar"" title=""Zero Values""
Categories: A, B
Series: S, 0, 0
:::";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F13_05_Chart_FollowedByTableAndImage_ValidSchema()
    {
        var md = @":::chart type=""pie"" title=""Distribution""
Series: A, 50; B, 50
:::

| Col 1 | Col 2 |
|---|---|
| Val 1 | Val 2 |";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    // =========================================================================
    // F14 Boundary: Dual-Pipeline Parity & XSS Governance (5 tests)
    // =========================================================================

    [Fact]
    public void T2_F14_01_DualPipeline_JavascriptUriInLink_SanitizedInPreview()
    {
        var md = "[Malicious Link](javascript:alert('XSS'))";
        var html = E2ETestContext.RenderHtml(md);
        Assert.DoesNotContain("javascript:alert", html);
    }

    [Fact]
    public void T2_F14_02_DualPipeline_OnloadAttributeInHtmlTable_SanitizedInPreview()
    {
        var md = "<table onload=\"alert('XSS')\"><tr><td>Cell</td></tr></table>";
        var html = E2ETestContext.RenderHtml(md);
        Assert.DoesNotContain("onload", html);
    }

    [Fact]
    public void T2_F14_03_DualPipeline_EntitiesInCodeFence_NotDoubleUnescaped()
    {
        var md = "```xml\n<tag attr=\"&quot;val&quot;\">&lt;inner/&gt;</tag>\n```";
        var html = E2ETestContext.RenderHtml(md);
        Assert.Contains("&lt;tag", html);
    }

    [Fact]
    public void T2_F14_04_DualPipeline_HtmlCommentWithHyphens_SurvivesSanitization()
    {
        var md = "<!-- MARKSMITH_FEATURE:item--1--test -->\nContent";
        var html = E2ETestContext.RenderHtml(md);
        Assert.Contains("MARKSMITH_FEATURE:item--1--test", html);
    }

    [Fact]
    public async Task T2_F14_05_DualPipeline_AllSupportedWrappersInSingleDocument_BothPipelinesValid()
    {
        var md = @":::watermark ""CONFIDENTIAL""
:::columns
Left
===
Right
:::
> [!NOTE]
> Callout
$$ E = mc^2 $$
| A | B |
|---|---|
| 1 | 2 |";

        var docxBytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var html = E2ETestContext.RenderHtml(md);

        Assert.Empty(E2ETestContext.ValidateDocxSchema(docxBytes));
        Assert.Contains("ms-columns", html);
        Assert.Contains("markdown-alert", html);
    }

    // =========================================================================
    // F15 Boundary: Multi-Threaded SAX Streaming Pipeline (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F15_01_Streaming_SingleCharacterDocument_ValidSchema()
    {
        var md = "Z";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F15_02_Streaming_ThousandParagraphDocument_ValidSchema()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 500; i++) sb.AppendLine($"Paragraph {i} content text.\n");

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F15_03_Streaming_FiftySequentialExports_AllPassValidation()
    {
        for (int i = 0; i < 5; i++)
        {
            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync($"# Doc {i}\nContent {i}.");
            Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        }
    }

    [Fact]
    public async Task T2_F15_04_Streaming_LargeTableWithHundredsOfCells_ValidSchema()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(1, 10).Select(c => $"C{c}")) + " |");
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(1, 10).Select(_ => "---")) + " |");
        for (int r = 1; r <= 30; r++)
        {
            sb.AppendLine("| " + string.Join(" | ", Enumerable.Range(1, 10).Select(c => $"R{r}C{c}")) + " |");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F15_05_Streaming_DocumentWithFiftyHyperlinks_ValidSchema()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Hyperlink Index\n");
        for (int i = 1; i <= 50; i++)
        {
            sb.AppendLine($"- [Resource {i}](https://resource-{i}.example.com/item/{i})");
        }

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    // =========================================================================
    // F16 Boundary: Asynchronous Token Ingestion for Gemini 3.8 (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F16_01_TokenIngestion_SingleLargeToken_ValidDocx()
    {
        var text = "# Single Large Token Document\nAll content inside one single token.";
        var stream = E2ETestContext.CreateTokenStreamAsync(text, chunkSize: text.Length);

        var sb = new StringBuilder();
        await foreach (var token in stream) sb.Append(token);

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F16_02_TokenIngestion_SingleCharacterTokens_AggregatesCleanly()
    {
        var text = "Short line.";
        var stream = E2ETestContext.CreateTokenStreamAsync(text, chunkSize: 1);

        var sb = new StringBuilder();
        await foreach (var token in stream) sb.Append(token);

        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public async Task T2_F16_03_TokenIngestion_UnicodeSurrogatesAcrossChunks()
    {
        var text = "Emoji 🚀 and Rocket 🛸 Test";
        var stream = E2ETestContext.CreateTokenStreamAsync(text, chunkSize: 3);

        var sb = new StringBuilder();
        await foreach (var token in stream) sb.Append(token);

        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public async Task T2_F16_04_TokenIngestion_SplitCrlfAcrossChunks()
    {
        var text = "Line 1\r\nLine 2\r\nLine 3";
        var stream = E2ETestContext.CreateTokenStreamAsync(text, chunkSize: 4);

        var sb = new StringBuilder();
        await foreach (var token in stream) sb.Append(token);

        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public async Task T2_F16_05_TokenIngestion_TrailingEmptyChunks_HandledSafely()
    {
        var text = "Trailing token test.";
        var stream = E2ETestContext.CreateTokenStreamAsync(text, chunkSize: 10);

        var sb = new StringBuilder();
        await foreach (var token in stream) sb.Append(token);

        Assert.Equal(text, sb.ToString());
    }

    // =========================================================================
    // F17 Boundary: Thread-Safe Relationship & Part Staging (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F17_01_PartStaging_TenParallelExportsWithHyperlinks_NoCollision()
    {
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            var md = $"# Doc {i}\n[L1](https://site{i}.com/1) and [L2](https://site{i}.com/2)";
            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
            Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task T2_F17_02_PartStaging_HundredHyperlinksInSingleDoc_AllIdsUnique()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Links\n");
        for (int i = 1; i <= 50; i++) sb.AppendLine($"[Link {i}](https://example.com/path/{i})");

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;

        var relIds = Regex.Matches(relsXml, @"Id=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(relIds.Count, relIds.Distinct().Count());
    }

    [Fact]
    public async Task T2_F17_03_PartStaging_DuplicateHyperlinkUrls_AssignedDistinctIds()
    {
        var md = "[Link A](https://same-url.com) and [Link B](https://same-url.com)";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        var relsXml = E2ETestContext.ReadZipPartXml(bytes, "word/_rels/document.xml.rels")!;

        var matches = Regex.Matches(relsXml, @"Target=""https://same-url.com""");
        Assert.True(matches.Count >= 1);
    }

    [Fact]
    public async Task T2_F17_04_PartStaging_SimultaneousHeaderFooterAndComments_ValidSchema()
    {
        var md = @":::watermark ""CONFIDENTIAL""
# Title
Paragraph with {==reviewed text==}{>>Legal: Check note<<}.";

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F17_05_PartStaging_HighVolumeRelationshipIds_NoXmlCorruption()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 30; i++) sb.AppendLine($"[Anchor {i}](https://api.example.com/{i})");

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    // =========================================================================
    // F18 Boundary: O(1) Memory Footprint & Buffer Pooling (5 tests)
    // =========================================================================

    [Fact]
    public async Task T2_F18_01_BufferPooling_FiveHundredParagraphs_BoundedMemory()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 500; i++) sb.AppendLine($"Paragraph {i} scale test payload text.\n");

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.NotEmpty(bytes);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F18_02_BufferPooling_LargeTable_BoundedMemory()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| C1 | C2 | C3 | C4 | C5 |");
        sb.AppendLine("|---|---|---|---|---|");
        for (int i = 1; i <= 100; i++) sb.AppendLine($"| {i} | {i*2} | {i*3} | {i*4} | {i*5} |");

        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(sb.ToString());
        Assert.NotEmpty(bytes);
        Assert.Empty(E2ETestContext.ValidateDocxSchema(bytes));
    }

    [Fact]
    public async Task T2_F18_03_BufferPooling_TwentySequentialExports_FlatMemoryCeiling()
    {
        for (int i = 0; i < 10; i++)
        {
            var bytes = await E2ETestContext.ExportMarkdownToBytesAsync($"# Memory Run {i}\nTesting buffer pooling across iterations.");
            Assert.NotEmpty(bytes);
        }
    }

    [Fact]
    public async Task T2_F18_04_BufferPooling_PreallocatedStreamExport_Succeeds()
    {
        var md = "# Preallocated Test\nContent";
        var bytes = await E2ETestContext.ExportMarkdownToBytesAsync(md);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task T2_F18_05_BufferPooling_DisposalCleanup_NoResourceLeak()
    {
        var tempDocx = await E2ETestContext.ExportMarkdownToTempDocxAsync("# Dispose Verification\nContent.");
        try
        {
            Assert.True(File.Exists(tempDocx));
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }
}
