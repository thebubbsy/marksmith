using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MdToPdf.Core.AdvancedFeatures;
using MdToPdf.Models;
using MdToPdf.Plugins;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class Category1And3FixTests
{
    private static readonly ThemeCatalog Themes = new();

    // M1-01: HtmlSanitizer event handler regex \son\w+ -> allow slash prefix [\s/]on\w+
    [Fact]
    public void M1_01_HtmlSanitizer_Strips_Slash_Delimited_Event_Handlers()
    {
        var html = "<img/onload=alert(1) src=\"x.png\">";
        var sanitized = HtmlSanitizer.Apply(html);
        Assert.DoesNotContain("onload", sanitized);
        Assert.DoesNotContain("alert(1)", sanitized);
    }

    // M1-02: MarkdownHtmlService embedded Mermaid string literal -> WebUtility.HtmlEncode()
    [Fact]
    public void M1_02_MarkdownHtmlService_Encodes_Mermaid_String_Literals()
    {
        var service = new MarkdownHtmlService();
        var theme = Themes.GetOrDefault("GitHub Light");
        var settings = new AppSettings { MermaidEnabled = true };
        var markdown = "```typescript\nrenderMermaid(`graph TD\n<script>alert(1)</script>`);\n```";

        var html = service.Render(markdown, settings, theme);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<div class=\"mermaid mermaid-embedded\">graph TD\n<script>", html);
    }

    // M1-03: AdvancedFeaturePipeline nested ::: containers -> stack depth tracking
    [Fact]
    public void M1_03_AdvancedFeaturePipeline_Tracks_Container_Nesting_Depth()
    {
        var pipeline = new AdvancedFeaturePipeline();
        var markdown = ":::columns count=2\n:::column\nLeft content\n:::\n:::column\nRight content\n:::\n:::";
        var nodes = pipeline.Process(markdown, "doc-test");

        Assert.Single(nodes);
        var node = nodes[0];
        Assert.Equal("Columns", node.Detector.FeatureName);
        Assert.Contains("Left content", node.InnerContent);
        Assert.Contains("Right content", node.InnerContent);
    }

    // M1-04: DialectNormalizer double hyphen -> check AppSettings.DashMode
    [Fact]
    public void M1_04_DialectNormalizer_Respects_DashMode_Keep()
    {
        var markdown = "hello -- world";
        var normalized = DialectNormalizer.Apply(markdown, DashReplacer.Keep);
        Assert.Equal("hello -- world", normalized);
    }

    // M1-05: DashReplacer 4-space indented code blocks -> skip double hyphen replacement
    [Fact]
    public void M1_05_DashReplacer_Skips_Indented_Code_Blocks()
    {
        var markdown = "    var x = a -- b;\nprose -- test";
        var result = DashReplacer.NormalizeDoubleHyphens(markdown);
        Assert.Contains("    var x = a -- b;", result);
        Assert.Contains("prose — test", result);
    }

    // M1-06: AdmonitionNormalizer admonition body loop -> track code fences so ::: inside code blocks does not truncate
    [Fact]
    public void M1_06_AdmonitionNormalizer_Ignores_Colons_Inside_Code_Fences()
    {
        var markdown = ":::tip Code Example\nHere is code:\n```\n:::\n```\nEnd of tip.\n:::";
        var result = AdmonitionNormalizer.Apply(markdown);

        Assert.Contains("> [!TIP]", result);
        Assert.Contains("> **Code Example**", result);
        Assert.Contains("> :::", result);
        Assert.Contains("> End of tip.", result);
    }

    // M1-07: EpubExportService void tag XHTML regex -> quote-aware attribute parsing
    [Fact]
    public void M1_07_EpubExportService_Quote_Aware_Void_Tag_Regex()
    {
        var method = typeof(EpubExportService).GetMethod("XhtmlSafe", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var html = "<img src=\"foo.png\" alt=\"A > B\">";
        var xhtml = (string)method.Invoke(null, new object[] { html })!;

        Assert.Equal("<img src=\"foo.png\" alt=\"A > B\" />", xhtml);
    }

    // M1-08: DiagramFenceSniffer fence marker length -> dynamic fence length matching
    [Fact]
    public void M1_08_DiagramFenceSniffer_Matches_Opening_Fence_Length()
    {
        var markdown = "````\n```\ndigraph G { A -> B }\n```\n````";
        var result = DiagramFenceSniffer.Apply(markdown);

        Assert.StartsWith("````dot", result);
        Assert.EndsWith("````", result.Trim());
    }

    // M1-09: MarkdownHtmlService TOC building regex -> preserve generic types like List<T>
    [Fact]
    public void M1_09_MarkdownHtmlService_Toc_Preserves_Generic_Types()
    {
        var service = new MarkdownHtmlService();
        var theme = Themes.GetOrDefault("GitHub Light");
        var settings = new AppSettings { IncludeToc = true };
        var markdown = "# Overview\n\n# `List<T>` Implementation\n\n## `Dictionary<TKey, TValue>` Guide\n";

        var html = service.Render(markdown, settings, theme);
        Assert.Contains("List&lt;T&gt;", html);
        Assert.Contains("Dictionary&lt;TKey, TValue&gt;", html);
    }

    // M3-01: SvgSanitizer -> HTML-decode SVG attribute values before checking javascript URIs
    [Fact]
    public void M3_01_SvgSanitizer_Decodes_Attribute_Values_Before_Sanitizing()
    {
        var svg = "<svg><a href=\"java&#x73;cript:alert(1)\"><rect/></a></svg>";
        var sanitized = SvgSanitizer.Sanitize(svg);

        Assert.DoesNotContain("javascript", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", sanitized);
        Assert.Contains("<rect/>", sanitized);
    }

    // M3-02: MarkdownHtmlService plugin SVG output -> pass through SvgSanitizer.Sanitize(svg)
    [Fact]
    public void M3_02_SvgSanitizer_Sanitizes_Malicious_Svg_Output()
    {
        var maliciousSvg = "<svg onload=\"alert(1)\"><script>evil()</script><rect/></svg>";
        var sanitized = SvgSanitizer.Sanitize(maliciousSvg);

        Assert.DoesNotContain("onload", sanitized);
        Assert.DoesNotContain("<script", sanitized);
        Assert.Contains("<rect/>", sanitized);
    }

    // M3-03: DialectNormalizer multi-backtick inline code -> regex matching multi-backtick delimiters
    [Fact]
    public void M3_03_DialectNormalizer_Preserves_Multi_Backtick_Inline_Code()
    {
        var markdown = "Look at `` [[WikiLink]] `` and `#tag` in code";
        var result = DialectNormalizer.Apply(markdown);

        Assert.Contains("`` [[WikiLink]] ``", result);
        Assert.DoesNotContain("class=\"wikilink\"", result);
    }

    // M3-04: MarkdownHtmlService SkiaSharp SKBitmap leak -> wrap in using block
    [Fact]
    public void M3_04_MarkdownHtmlService_PrepareImageForInline_Handles_Memory_Cleanly()
    {
        var method = typeof(MarkdownHtmlService).GetMethod("PrepareImageForInline", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        // Generate a 1x1 valid PNG image bytes
        byte[] pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var tempFile = Path.Combine(Path.GetTempPath(), $"test-image-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tempFile, pngBytes);

        try
        {
            var fileInfo = new FileInfo(tempFile);
            var result = method.Invoke(null, new object[] { tempFile, fileInfo });
            Assert.NotNull(result);
            var tuple = ((byte[]? Data, string? MimeType))result!;
            Assert.NotNull(tuple.Data);
            Assert.Equal("image/png", tuple.MimeType);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
