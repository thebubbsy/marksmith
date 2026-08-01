using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace MdToPdf.Core.Tests;

public class Milestone3AdversarialTests
{
    // Helper simulating OnPreviewWebMessage JSON payload extraction
    private static (bool Handled, string Type, string Code, int Index, string? ErrorMessage) ProcessWebMessage(string json)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return (false, "", "", 0, null);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return (false, "", "", 0, null);
            var type = typeProp.GetString() ?? "";

            if (type == "launch-mermaid-studio" || type == "edit-mermaid-code")
            {
                var code = root.TryGetProperty("code", out var cProp) ? cProp.GetString() : "";
                var idx = root.TryGetProperty("index", out var iProp) ? iProp.GetInt32() : 0;
                return (true, type, code ?? "", idx, null);
            }
            return (false, type, "", 0, null);
        }
        catch (Exception ex)
        {
            return (false, "", "", 0, ex.Message);
        }
    }

    // Helper simulating block selection logic in ShowMermaidDiagramStudioWindowAsync
    private static string SelectOriginalBlock(string currentMd, int targetIndex, string sampleCode)
    {
        var blocks = MdToPdf.Mermaid.Sync.MermaidMarkdownSyncService.ExtractMermaidBlocks(currentMd);
        string originalBlock = "";
        string originalBody = "";
        if (targetIndex >= 0 && targetIndex < blocks.Count)
        {
            var target = blocks[targetIndex];
            originalBlock = currentMd[target.StartOffset..(target.EndOffset + 1)];
            originalBody = blocks[targetIndex].Code;
        }

        if (string.IsNullOrWhiteSpace(originalBody) && !string.IsNullOrWhiteSpace(sampleCode))
        {
            originalBody = sampleCode.Trim();
        }
        return originalBlock;
    }

    [Fact]
    public void WebMessage_ValidPayload_ParsesSuccessfully()
    {
        var json = "{\"type\":\"launch-mermaid-studio\",\"index\":1,\"code\":\"graph TD; A-->B;\"}";
        var res = ProcessWebMessage(json);
        Assert.True(res.Handled);
        Assert.Equal("launch-mermaid-studio", res.Type);
        Assert.Equal("graph TD; A-->B;", res.Code);
        Assert.Equal(1, res.Index);
    }

    [Fact]
    public void WebMessage_MissingIndex_DefaultsToZero()
    {
        var json = "{\"type\":\"launch-mermaid-studio\",\"code\":\"graph TD; A-->B;\"}";
        var res = ProcessWebMessage(json);
        Assert.True(res.Handled);
        Assert.Equal(0, res.Index);
    }

    [Fact]
    public void WebMessage_NullCode_ReturnsEmptyString()
    {
        var json = "{\"type\":\"launch-mermaid-studio\",\"index\":0,\"code\":null}";
        var res = ProcessWebMessage(json);
        Assert.True(res.Handled);
        Assert.Equal("", res.Code);
    }

    [Fact]
    public void WebMessage_InvalidJsonString_ThrowsJsonException_HandledByCatch()
    {
        var json = "{ type: 'launch-mermaid-studio', index: ";
        var res = ProcessWebMessage(json);
        Assert.False(res.Handled);
        Assert.NotNull(res.ErrorMessage);
    }

    [Fact]
    public void WebMessage_StringIndex_ThrowsInvalidOperationException_HandledByCatch()
    {
        var json = "{\"type\":\"launch-mermaid-studio\",\"index\":\"0\",\"code\":\"graph TD;\"}";
        var res = ProcessWebMessage(json);
        Assert.False(res.Handled);
        Assert.NotNull(res.ErrorMessage);
    }

    [Fact]
    public void WebMessage_ObjectCode_ThrowsInvalidOperationException_HandledByCatch()
    {
        var json = "{\"type\":\"launch-mermaid-studio\",\"index\":0,\"code\":{\"invalid\":\"object\"}}";
        var res = ProcessWebMessage(json);
        Assert.False(res.Handled);
        Assert.NotNull(res.ErrorMessage);
    }

    [Fact]
    public void BlockSelection_NegativeIndex_DoesNotFallbackToFirstDiagram()
    {
        var md = "```mermaid\nDiagram 0\n```\n\n```mermaid\nDiagram 1\n```";
        var selected = SelectOriginalBlock(md, -1, "Fallback");
        Assert.Equal("", selected);
    }

    [Fact]
    public void BlockSelection_OutOfRangeIndex_DoesNotFallbackToFirstDiagram()
    {
        var md = "```mermaid\nDiagram 0\n```\n\n```mermaid\nDiagram 1\n```";
        var selected = SelectOriginalBlock(md, 99, "Fallback");
        Assert.Equal("", selected);
    }
}
