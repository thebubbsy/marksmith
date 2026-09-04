using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Tests.E2E;

/// <summary>
/// Shared test fixture, template generator, OpenXml ECMA-376 schema validator,
/// MCP protocol dispatcher, and surgical in-place patch / inspector test harness.
/// </summary>
public static class E2ETestContext
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly ThemeCatalog Themes = new();

    public static async Task<string> ExportMarkdownToTempDocxAsync(string markdown, AppSettings? settings = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"marksmith-e2e-{Guid.NewGuid():N}.docx");
        var service = new DocxExportService();
        await service.ExportAsync(markdown, tempPath, settings ?? new AppSettings());
        return tempPath;
    }

    public static async Task<byte[]> ExportMarkdownToBytesAsync(string markdown, AppSettings? settings = null)
    {
        var tempPath = await ExportMarkdownToTempDocxAsync(markdown, settings);
        try
        {
            return await File.ReadAllBytesAsync(tempPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static string RenderHtml(string markdown, AppSettings? settings = null, ThemeDefinition? theme = null)
    {
        var s = settings ?? new AppSettings();
        var t = theme ?? Themes.GetOrDefault(s.Theme ?? "GitHub Light");
        return new MarkdownHtmlService().Render(markdown, s, t);
    }

    public static List<ValidationErrorInfo> ValidateDocxSchema(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        return validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
            .ToList();
    }

    public static List<ValidationErrorInfo> ValidateDocxSchema(byte[] docxBytes)
    {
        using var ms = new MemoryStream(docxBytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        return validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
            .ToList();
    }

    public static string? ReadZipPartXml(string docxPath, string partPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry(partPath);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string? ReadZipPartXml(byte[] docxBytes, string partPath)
    {
        using var ms = new MemoryStream(docxBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(partPath);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static List<string> ListZipEntries(byte[] docxBytes)
    {
        using var ms = new MemoryStream(docxBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    public static string CreateSyntheticDotxTemplate(
        string? destinationPath = null,
        string bodyFont = "Segoe UI",
        string headingFont = "Segoe UI Semibold",
        string h1ColorHex = "1F497D",
        string accent1Hex = "0078D4",
        string accent2Hex = "2B579A",
        int topMarginDxa = 1440,
        int bottomMarginDxa = 1440,
        int leftMarginDxa = 1440,
        int rightMarginDxa = 1440)
    {
        var filePath = destinationPath ?? Path.Combine(Path.GetTempPath(), $"template-{Guid.NewGuid():N}.dotx");
        using (var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Template))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("Corporate Template Header"))
                ),
                new SectionProperties(
                    new PageMargin
                    {
                        Top = topMarginDxa,
                        Bottom = bottomMarginDxa,
                        Left = (uint)leftMarginDxa,
                        Right = (uint)rightMarginDxa
                    }
                )
            ));

            var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
            var styles = new Styles();
            styles.DocDefaults = new DocDefaults(
                new RunPropertiesDefault(
                    new RunPropertiesBaseStyle(
                        new RunFonts { Ascii = bodyFont, HighAnsi = bodyFont }
                    )
                )
            );

            var h1Style = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Heading1",
                CustomStyle = true
            };
            h1Style.Append(new StyleName { Val = "heading 1" });
            h1Style.Append(new StyleRunProperties(
                new RunFonts { Ascii = headingFont, HighAnsi = headingFont },
                new Color { Val = h1ColorHex.TrimStart('#') },
                new FontSize { Val = "48" },
                new Bold()
            ));
            styles.Append(h1Style);
            stylesPart.Styles = styles;

            var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
            var numbering = new Numbering();
            var abstractNum = new AbstractNum { AbstractNumberId = 100 };
            abstractNum.Append(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel });
            var lvl = new Level { LevelIndex = 0 };
            lvl.Append(new StartNumberingValue { Val = 1 });
            lvl.Append(new NumberingFormat { Val = NumberFormatValues.Bullet });
            lvl.Append(new LevelText { Val = "•" });
            abstractNum.Append(lvl);
            numbering.Append(abstractNum);

            var numInstance = new NumberingInstance { NumberID = 100 };
            numInstance.Append(new AbstractNumId { Val = 100 });
            numbering.Append(numInstance);
            numberingPart.Numbering = numbering;

            var themePart = mainPart.AddNewPart<ThemePart>();
            using (var sw = new StreamWriter(themePart.GetStream(FileMode.Create), Encoding.UTF8))
            {
                sw.Write($@"<?xml version=""1.0"" encoding=""utf-8""?>
<a:theme xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main"" name=""Office Theme"">
  <a:themeElements>
    <a:clrScheme name=""Office"">
      <a:dk1><a:srgbClr val=""000000""/></a:dk1>
      <a:lt1><a:srgbClr val=""FFFFFF""/></a:lt1>
      <a:accent1><a:srgbClr val=""{accent1Hex.TrimStart('#')}""/></a:accent1>
      <a:accent2><a:srgbClr val=""{accent2Hex.TrimStart('#')}""/></a:accent2>
      <a:hlink><a:srgbClr val=""0563C1""/></a:hlink>
    </a:clrScheme>
    <a:fontScheme name=""Office"">
      <a:majorFont><a:latin typeface=""{headingFont}""/></a:majorFont>
      <a:minorFont><a:latin typeface=""{bodyFont}""/></a:minorFont>
    </a:fontScheme>
  </a:themeElements>
</a:theme>");
            }

            mainPart.Document.Save();
        }

        return filePath;
    }

    public static DocxStructureReport InspectDocx(byte[] docxBytes)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"inspect-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(tempPath, docxBytes);
        try
        {
            var inspector = new DocxInspector();
            return inspector.Inspect(tempPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public static (byte[] OutputBytes, PatchResult Result) ApplyDocxPatch(byte[] inputDocxBytes, DocxPatchRequest request)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"patch-in-{Guid.NewGuid():N}.docx");
        var outPath = Path.Combine(Path.GetTempPath(), $"patch-out-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(tempPath, inputDocxBytes);
        try
        {
            var patcher = new InPlaceDocxPatcher();
            var reqWithPaths = request with { OutputPath = outPath };
            var result = patcher.ApplyPatch(tempPath, reqWithPaths);
            if (result.Success && File.Exists(outPath))
            {
                var bytes = File.ReadAllBytes(outPath);
                return (bytes, result);
            }
            return (inputDocxBytes, result);
        }
        catch (Exception ex)
        {
            return (inputDocxBytes, PatchResult.Fail(ex.Message));
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
        }
    }

    public static async IAsyncEnumerable<string> CreateTokenStreamAsync(string fullText, int chunkSize = 10, int delayMs = 0)
    {
        if (string.IsNullOrEmpty(fullText)) yield break;

        for (int i = 0; i < fullText.Length; i += chunkSize)
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            int len = Math.Min(chunkSize, fullText.Length - i);
            yield return fullText.Substring(i, len);
        }
    }

    public static string ApplyMarkdownPatch(string originalMd, string target, string replacement, bool trackChanges = false, string? author = null)
    {
        if (string.IsNullOrEmpty(target) || !originalMd.Contains(target))
        {
            return originalMd;
        }

        if (trackChanges)
        {
            string criticMarkup = $"{{--{target}--}}{{++{replacement}++}}";
            return originalMd.Replace(target, criticMarkup);
        }

        return originalMd.Replace(target, replacement);
    }

    public static (bool IsValid, List<string> Errors) ValidateMarkdownGovernance(string markdown)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown)) return (true, errors);

        int fenceCount = Regex.Matches(markdown, @"^```", RegexOptions.Multiline).Count;
        if (fenceCount % 2 != 0)
        {
            errors.Add("Unclosed code fence detected.");
        }

        if (Regex.IsMatch(markdown, @"<script\b", RegexOptions.IgnoreCase))
        {
            errors.Add("Raw <script> tag detected, violating MD_ENGINE_GOVERNANCE security baseline.");
        }

        return (errors.Count == 0, errors);
    }

    public static async Task<string> SimulateMcpJsonRpcAsync(string jsonRpcRequest, AppSettings? settings = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonRpcRequest))
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "1",
                    error = new { code = -32700, message = "Parse error: empty request" }
                });
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(jsonRpcRequest);
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = (string?)null,
                    error = new { code = -32700, message = $"Parse error: {ex.Message}" }
                });
            }

            using (doc)
            {
                var root = doc.RootElement;
                string method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() ?? "" : "";
                string id = root.TryGetProperty("id", out var idProp) ? idProp.ToString() : "1";

                var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

                if (method == "initialize")
                {
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            protocolVersion = "2024-11-05",
                            capabilities = new
                            {
                                prompts = new { listChanged = false },
                                resources = new { subscribe = false, listChanged = false },
                                tools = new { listChanged = false }
                            },
                            serverInfo = new { name = "marksmith-mcp", version = "3.8.0" }
                        }
                    }, jsonOptions);
                }

                if (method == "notifications/initialized" || method == "initialized")
                {
                    return "";
                }

                if (method == "ping")
                {
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new { }
                    }, jsonOptions);
                }

                if (method == "prompts/list")
                {
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            prompts = new object[]
                            {
                                new
                                {
                                    name = "author_document_gemini_3_8",
                                    description = "Generates high-fidelity markdown aligned with MarkSmith dual-pipeline syntax governance.",
                                    arguments = new[] { new { name = "topic", description = "Document topic", required = true } }
                                },
                                new
                                {
                                    name = "three_block_cycle_gemini_3_8",
                                    description = "Executes AI 3-block cadence with idea generation and refinement cycles.",
                                    arguments = new[] { new { name = "domain", description = "Domain area", required = true } }
                                },
                                new
                                {
                                    name = "review_and_patch_gemini_3_8",
                                    description = "Reviews DOCX/Markdown and applies surgical in-place patches with CriticMarkup.",
                                    arguments = new[] { new { name = "target_path", description = "Path to file", required = true } }
                                }
                            }
                        }
                    }, jsonOptions);
                }

                if (method == "prompts/get")
                {
                    var paramsElem = root.TryGetProperty("params", out var pp) ? pp : default;
                    string pName = paramsElem.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(pName))
                    {
                        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = -32602, message = "Missing prompt name" } });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            description = $"Prompt handler for {pName}",
                            messages = new[]
                            {
                                new { role = "user", content = new { type = "text", text = $"Execute prompt {pName} for Gemini 3.8." } }
                            }
                        }
                    }, jsonOptions);
                }

                if (method == "resources/list")
                {
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            resources = new object[]
                            {
                                new { uri = "marksmith://governance/syntax-contract", name = "Markdown Engine Syntax Contract", mimeType = "text/markdown" },
                                new { uri = "marksmith://templates/catalog", name = "Available Corporate Templates", mimeType = "application/json" },
                                new { uri = "marksmith://schemas/patch-spec", name = "In-Place Patch JSON Specification", mimeType = "application/json" }
                            }
                        }
                    }, jsonOptions);
                }

                if (method == "resources/read")
                {
                    var paramsElem = root.TryGetProperty("params", out var rp) ? rp : default;
                    string uri = paramsElem.TryGetProperty("uri", out var ru) ? ru.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(uri))
                    {
                        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = -32602, message = "Missing resource URI" } });
                    }

                    string content = uri switch
                    {
                        "marksmith://governance/syntax-contract" => "# MD_ENGINE_GOVERNANCE\nTwo pipelines, one contract: DOCX/OpenXML and HTML preview.",
                        "marksmith://templates/catalog" => "{\"templates\":[\"GitHub Light\",\"Corporate Blue\",\"Modern Dark\"]}",
                        "marksmith://schemas/patch-spec" => "{\"title\":\"DocxPatchRequest\",\"type\":\"object\"}",
                        _ => $"Content for {uri}"
                    };

                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            contents = new[]
                            {
                                new { uri = uri, mimeType = "text/plain", text = content }
                            }
                        }
                    }, jsonOptions);
                }

                if (method == "tools/list")
                {
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new
                        {
                            tools = new object[]
                            {
                                new { name = "render_markdown_to_docx", description = "Renders markdown to WordprocessingML DOCX" },
                                new { name = "inspect_docx", description = "Inspects DOCX structural metrics and blocks" },
                                new { name = "patch_docx", description = "Performs surgical in-place block patches on DOCX" },
                                new { name = "convert_docx_to_markdown", description = "Converts DOCX back to Markdown with CriticMarkup" },
                                new { name = "patch_markdown", description = "Performs lossless search/replace and CriticMarkup patching on Markdown" },
                                new { name = "validate_markdown", description = "Validates Markdown syntax against MD_ENGINE_GOVERNANCE.md" },
                                new { name = "diff_markdown", description = "Computes line and semantic AST differences between two Markdown documents" },
                                new { name = "diff_docx", description = "Computes structural block differences between two DOCX files" },
                                new { name = "manage_3block_cycle", description = "Manages GEMINI.md Section 7 AI 3-block idea generation and execution state machine" }
                            }
                        }
                    }, jsonOptions);
                }

                if (method == "tools/call")
                {
                    var paramsElem = root.GetProperty("params");
                    string toolName = paramsElem.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                    var arguments = paramsElem.TryGetProperty("arguments", out var args) ? args : default;

                    if (string.IsNullOrEmpty(toolName))
                    {
                        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = -32602, message = "Missing tool name" } });
                    }

                    if (toolName == "render_markdown_to_docx")
                    {
                        string md = arguments.TryGetProperty("markdown", out var m) ? m.GetString() ?? "" : "";
                        var bytes = await ExportMarkdownToBytesAsync(md, settings);
                        string outPath = arguments.TryGetProperty("output_path", out var op) ? op.GetString()! : Path.GetTempFileName() + ".docx";
                        await File.WriteAllBytesAsync(outPath, bytes);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { success = true, output_path = outPath, bytes_written = bytes.Length }
                        }, jsonOptions);
                    }
                    else if (toolName == "inspect_docx")
                    {
                        string docxPath = arguments.TryGetProperty("docx_path", out var dp) ? dp.GetString() ?? "" : "";
                        if (!File.Exists(docxPath))
                        {
                            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = -32602, message = $"File not found: {docxPath}" } });
                        }
                        var bytes = await File.ReadAllBytesAsync(docxPath);
                        var report = InspectDocx(bytes);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { report = report }
                        }, jsonOptions);
                    }
                    else if (toolName == "patch_docx")
                    {
                        string docxPath = arguments.TryGetProperty("docx_path", out var dp) ? dp.GetString() ?? "" : "";
                        if (!File.Exists(docxPath))
                        {
                            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = -32602, message = $"File not found: {docxPath}" } });
                        }
                        var patchJson = arguments.TryGetProperty("patch", out var pj) ? pj.GetRawText() : arguments.GetRawText();
                        DocxPatchRequest? patchReq = null;
                        try
                        {
                            var pasOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            pasOpts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                            pasOpts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
                            patchReq = JsonSerializer.Deserialize<DocxPatchRequest>(patchJson, pasOpts);
                        }
                        catch { }

                        bool hasValidTarget = patchReq != null &&
                            ((patchReq.Operations != null && patchReq.Operations.Count > 0 && (patchReq.Operations[0].Target.BodyIndex.HasValue || !string.IsNullOrEmpty(patchReq.Operations[0].Target.ParaId))) ||
                             (patchReq.Target != null && (patchReq.Target.BodyIndex.HasValue || !string.IsNullOrEmpty(patchReq.Target.ParaId))));

                        if (!hasValidTarget)
                        {
                            var patchOpts = new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                            };
                            patchOpts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                            patchOpts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
                            try
                            {
                                var fallbackReq = JsonSerializer.Deserialize<DocxPatchRequest>(patchJson, patchOpts);
                                if (fallbackReq != null) patchReq = fallbackReq;
                            }
                            catch { }
                        }
                        var bytes = await File.ReadAllBytesAsync(docxPath);
                        var (outBytes, patchResult) = ApplyDocxPatch(bytes, patchReq!);
                        await File.WriteAllBytesAsync(docxPath, outBytes);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { success = patchResult.Success, modified_blocks = patchResult.ModifiedBlocks }
                        }, jsonOptions);
                    }
                    else if (toolName == "convert_docx_to_markdown")
                    {
                        string docxPath = arguments.TryGetProperty("docx_path", out var dp) ? dp.GetString() ?? "" : "";
                        if (!File.Exists(docxPath))
                        {
                            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id, error = new { code = -32602, message = $"File not found: {docxPath}" } });
                        }
                        var reverseService = new ReverseImportService();
                        var md = reverseService.ImportFromDocx(docxPath);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { markdown = md }
                        }, jsonOptions);
                    }
                    else if (toolName == "patch_markdown")
                    {
                        string sourceMd = arguments.TryGetProperty("markdown", out var sm) ? sm.GetString() ?? "" : "";
                        string target = arguments.TryGetProperty("target", out var tg) ? tg.GetString() ?? "" : "";
                        string replacement = arguments.TryGetProperty("replacement", out var rp) ? rp.GetString() ?? "" : "";
                        bool trackChanges = arguments.TryGetProperty("track_changes", out var tc) && tc.GetBoolean();
                        string patched = ApplyMarkdownPatch(sourceMd, target, replacement, trackChanges);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { success = true, markdown = patched }
                        }, jsonOptions);
                    }
                    else if (toolName == "validate_markdown")
                    {
                        string md = arguments.TryGetProperty("markdown", out var vm) ? vm.GetString() ?? "" : "";
                        var (isValid, errs) = ValidateMarkdownGovernance(md);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { is_valid = isValid, error_count = errs.Count, diagnostics = errs }
                        }, jsonOptions);
                    }
                    else if (toolName == "diff_markdown")
                    {
                        string mdA = arguments.TryGetProperty("original", out var oa) ? oa.GetString() ?? "" : "";
                        string mdB = arguments.TryGetProperty("modified", out var mb) ? mb.GetString() ?? "" : "";
                        var diffService = new MarkdownDiffService();
                        var diffResult = diffService.Compare(mdA, mdB);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new { changed = mdA != mdB, diff = diffResult }
                        }, jsonOptions);
                    }
                    else if (toolName == "manage_3block_cycle")
                    {
                        string action = arguments.TryGetProperty("action", out var ac) ? ac.GetString() ?? "advance" : "advance";
                        int currentBlock = arguments.TryGetProperty("current_block", out var cb) ? cb.GetInt32() : 1;
                        int nextBlock = Math.Min(4, currentBlock + 1);
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            result = new
                            {
                                status = "success",
                                previous_block = currentBlock,
                                current_block = nextBlock,
                                is_execution_phase = nextBlock == 4,
                                total_refined_ideas = nextBlock == 4 ? 6 : nextBlock * 2
                            }
                        }, jsonOptions);
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            error = new { code = -32601, message = $"Tool not found: {toolName}" }
                        }, jsonOptions);
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    error = new { code = -32601, message = $"Method not found: {method}" }
                });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                error = new { code = -32603, message = ex.Message }
            });
        }
    }
}
