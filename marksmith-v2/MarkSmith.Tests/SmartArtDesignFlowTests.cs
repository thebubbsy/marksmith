using System.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// The SmartArt design flow: the studio inserts :::smartart MARKDOWN into the ACTIVE document
// (no standalone DOCX at design time), and the main preview renders that markdown as an SVG
// diagram so the user designs the whole document before exporting to DOCX.
public class SmartArtDesignFlowTests
{
    private static readonly ThemeDefinition LightTheme = new(
        "Light", "#ffffff", "#1a1a1a", "#111111", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");

    [Fact]
    public void Preview_RendersSmartArtBlock_AsSvgDiagram()
    {
        const string md = "Intro paragraph.\n\n" +
            ":::smartart type=\"hierarchy\"\n" +
            "- Executive Board\n" +
            "  - CEO\n" +
            "    - Engineering Team\n" +
            "    - Product Team\n" +
            "  - CFO\n" +
            ":::\n\n" +
            "Outro paragraph.";

        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);

        Assert.Contains("class=\"smartart", html);
        Assert.Contains("smartart-container", html);
        Assert.Contains("<svg", html);
        Assert.DoesNotContain("<!--SMARTART:", html); // every placeholder is swapped for the diagram
        Assert.DoesNotContain(":::smartart type=\"hierarchy\"", html); // the raw block is fully replaced
    }

    [Fact]
    public void Preview_WithSmartartInsideCodeFence_IsNotExtracted()
    {
        // A code sample that merely SHOWS the smartart syntax must stay literal code.
        const string md = "Example:\n\n```\n:::smartart type=\"process\"\n- Step 1\n:::\n```\n";

        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);

        Assert.DoesNotContain("class=\"smartart", html);
        Assert.Contains(":::smartart", html); // still visible as code
    }

    [Fact]
    public void Preview_SmartArtAtDocumentStart_Renders()
    {
        const string md = ":::smartart type=\"process\"\n- Step 1\n- Step 2\n:::\n\nAfter.";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);
        Assert.Contains("class=\"smartart", html);
        Assert.DoesNotContain(":::smartart type=\"process\"", html);
    }

    [Fact]
    public void Preview_SmartArtWithCrlf_Renders()
    {
        const string md = "Intro.\r\n\r\n:::smartart type=\"hierarchy\"\r\n- CEO\r\n  - Engineering\r\n:::\r\n\r\nOutro.";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);
        Assert.Contains("class=\"smartart", html);
    }

    [Fact]
    public void Preview_MultipleSmartArtBlocks_AllRender()
    {
        const string md = ":::smartart type=\"process\"\n- Step 1\n:::\n\nText.\n\n:::smartart type=\"cycle\"\n- A\n- B\n:::";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);
        Assert.Equal(2, CountOccurrences(html, "class=\"smartart\">")); // one wrapper div per block
        Assert.DoesNotContain("<!--SMARTART:", html);
    }

    [Fact]
    public void Preview_SmartArtInsideFenceWithBlankLine_IsNotExtracted()
    {
        // A fence that documents the syntax with a blank line after the opener: the blank line is
        // inside the fence, so it must NOT trigger extraction.
        const string md = "Example:\n\n```\n:::smartart type=\"process\"\n\n- Step 1\n:::\n```\n";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);
        Assert.DoesNotContain("class=\"smartart", html);
        Assert.Contains(":::smartart type=&quot;process&quot;", html); // still visible as (escaped) code
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }

    [Fact]
    public void Preview_WithoutSmartArt_HasNoSmartArtFrame()
    {
        const string md = "Just prose.\n\n- a\n- b\n";
        var html = new MarkdownHtmlService().Render(md, new AppSettings(), LightTheme);
        Assert.DoesNotContain("class=\"smartart", html);
    }

    [Fact]
    public void Studio_InsertIntoDocument_EmitsSmartArtMarkdownBlock()
    {
        var vm = new SmartArtDesignStudioViewModel();
        string? emitted = null;
        vm.InsertToDocumentRequested += (s, block) => emitted = block;

        vm.SelectedLayout = vm.Layouts.FirstOrDefault(l => l.Alias == "hierarchy");
        vm.MarkdownText = "- CEO\n  - Engineering\n  - Product";
        vm.InsertIntoDocumentCommand.Execute(null);

        Assert.NotNull(emitted);
        Assert.Contains(":::smartart type=\"hierarchy\"", emitted);
        Assert.Contains("- CEO\n  - Engineering", emitted);
        Assert.True(emitted.TrimEnd().EndsWith(":::"), "block ends with the closing marker"); // blank-line padded so it stays a block wherever the caret is
        Assert.DoesNotContain("SmartArt_", emitted); // it inserts a block, it does not write a .docx
    }

    [Fact]
    public void Studio_InsertIntoDocument_EmptyHierarchy_DoesNotEmit()
    {
        var vm = new SmartArtDesignStudioViewModel();
        bool fired = false;
        vm.InsertToDocumentRequested += (s, block) => fired = true;

        vm.MarkdownText = "   "; // blank hierarchy
        vm.InsertIntoDocumentCommand.Execute(null);

        Assert.False(fired);
        Assert.Contains("build a hierarchy", vm.StatusMessage);
    }
}
