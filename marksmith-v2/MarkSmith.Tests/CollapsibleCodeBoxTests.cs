using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Code;
using Xunit;

namespace MarkSmith.Tests;

public class CollapsibleCodeBoxTests
{
    [Fact]
    public void ProcessCodeContent_DetectsImports_AndGeneratesSubfold()
    {
        string csharpCode = """
            using System;
            using System.Collections.Generic;
            using System.Text;

            namespace App;

            public class Program
            {
                public static void Main()
                {
                    Console.WriteLine("Hello World");
                }
            }
            """;

        var box = CollapsibleCodeBoxService.ProcessCodeContent("csharp", csharpCode);
        Assert.Equal("csharp", box.Language);
        Assert.True(box.LineCount >= 10);
        Assert.Single(box.FoldRegions);
        Assert.Equal("imports", box.FoldRegions[0].RegionType);
        Assert.Equal("3 imports", box.FoldRegions[0].SummaryLabel);
        Assert.Contains("ms-fold-imports", box.FormattedHtml);
        Assert.Contains("3 imports", box.FormattedHtml);
        Assert.Contains("ms-line-num", box.FormattedHtml);
    }

    [Fact]
    public void EnhanceCodeBlocks_WrapsPreCodeInCollapsibleContainer()
    {
        string html = "<p>Here is code:</p><pre><code class=\"language-python\">import os\nimport sys\nprint('Done')</code></pre>";
        string enhanced = CollapsibleCodeBoxService.EnhanceCodeBlocks(html);

        Assert.Contains("class=\"ms-code-box\"", enhanced);
        Assert.Contains("class=\"ms-code-header\"", enhanced);
        Assert.Contains("class=\"ms-code-fold-btn\"", enhanced);
        Assert.Contains("class=\"ms-code-lang-badge\"", enhanced);
        Assert.Contains("Python", enhanced);
        Assert.Contains("ms-code-copy-btn", enhanced);
        Assert.Contains("class=\"ms-code-collapsed-footer\"", enhanced);
    }

    [Fact]
    public void MarkdownHtmlService_Render_KeepsPristineCodeRendering()
    {
        string markdown = """
            # Test Document

            ```csharp
            using System;
            using System.IO;

            var path = "test.txt";
            Console.WriteLine(path);
            ```
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        var html = new MarkdownHtmlService().Render(markdown, new AppSettings(), theme, interactive: true);

        Assert.Contains("<code", html);
        Assert.Contains("Console.WriteLine", html);
    }
}
