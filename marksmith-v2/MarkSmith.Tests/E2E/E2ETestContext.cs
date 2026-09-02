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
/// and surgical in-place patch / inspector test harness.
/// </summary>
public static class E2ETestContext
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

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

    /// <summary>
    /// Creates a synthetic, fully valid ECMA-376 Word Template (.dotx) with custom styles,
    /// palettes, margins, and numbering definitions for test verification.
    /// </summary>
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

            // Add styles part
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
                new FontSize { Val = "48" }, // 24pt
                new Bold()
            ));
            styles.Append(h1Style);
            stylesPart.Styles = styles;

            // Add numbering part
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

            // Add theme part
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

    /// <summary>
    /// Inspects an OpenXML DOCX document package and extracts structural metrics,
    /// blocks, track change revisions, and threaded comments using DocxInspector.
    /// </summary>
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

    /// <summary>
    /// Performs non-destructive, surgical in-place OpenXML patching on a DOCX document
    /// using InPlaceDocxPatcher.
    /// </summary>
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

    /// <summary>
    /// Simulates standard MCP JSON-RPC 2.0 tool execution over in-memory pipe.
    /// </summary>
    public static async Task<string> SimulateMcpJsonRpcAsync(string jsonRpcRequest, AppSettings? settings = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonRpcRequest);
            var root = doc.RootElement;
            string method = root.GetProperty("method").GetString() ?? "";
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
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "marksmith-mcp", version = "2.0.0" }
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
                            new { name = "convert_docx_to_markdown", description = "Converts DOCX back to Markdown with CriticMarkup" }
                        }
                    }
                }, jsonOptions);
            }

            if (method == "tools/call")
            {
                var paramsElem = root.GetProperty("params");
                string toolName = paramsElem.GetProperty("name").GetString() ?? "";
                var arguments = paramsElem.GetProperty("arguments");

                if (toolName == "render_markdown_to_docx")
                {
                    string md = arguments.GetProperty("markdown").GetString() ?? "";
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
                    string docxPath = arguments.GetProperty("docx_path").GetString() ?? "";
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
                    string docxPath = arguments.GetProperty("docx_path").GetString() ?? "";
                    var patchJson = arguments.GetProperty("patch").GetRawText();
                    var patchReq = JsonSerializer.Deserialize<DocxPatchRequest>(patchJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
                    string docxPath = arguments.GetProperty("docx_path").GetString() ?? "";
                    var reverseService = new ReverseImportService();
                    var md = reverseService.ImportFromDocx(docxPath);
                    return JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = id,
                        result = new { markdown = md }
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
