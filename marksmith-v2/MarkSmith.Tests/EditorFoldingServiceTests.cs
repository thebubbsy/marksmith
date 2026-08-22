using System;
using System.Linq;
using MarkSmith.Services.Editor;
using Xunit;

namespace MarkSmith.Tests;

public class EditorFoldingServiceTests
{
    [Fact]
    public void DetectFoldableRegions_FindsCodeAndFeatureBlocks()
    {
        string md = """
            # Document Title

            Here is some introduction text.

            ```csharp
            public class Foo
            {
                public void Bar() => Console.WriteLine("Hello");
            }
            ```

            :::columns count="2"
            Col 1
            ===
            Col 2
            :::

            Footer text.
            """;

        var regions = EditorFoldingService.DetectFoldableRegions(md);
        Assert.NotEmpty(regions);

        var codeBlock = regions.FirstOrDefault(r => r.Type == EditorFoldType.CodeBlock);
        Assert.NotNull(codeBlock);
        Assert.Equal("csharp", codeBlock.Language);
        Assert.True(codeBlock.LineCount >= 5);

        var featBlock = regions.FirstOrDefault(r => r.Type == EditorFoldType.FeatureBlock);
        Assert.NotNull(featBlock);
    }

    [Fact]
    public void FoldAndUnfoldRegion_PreservesExactContent()
    {
        string md = """
            # Header

            ```python
            def compute(x):
                y = x * 2
                return y + 10
            ```

            Paragraph after code.
            """;

        var regions = EditorFoldingService.DetectFoldableRegions(md);
        var pyRegion = regions.First(r => r.Type == EditorFoldType.CodeBlock);

        string folded = EditorFoldingService.FoldRegion(md, pyRegion);
        Assert.Contains("/* ▾ [", folded);
        Assert.Contains("FOLDED:codeblock:python:", folded);
        Assert.DoesNotContain("def compute(x):", folded);

        // Unfold using line index of folded region
        string unfolded = EditorFoldingService.UnfoldRegion(folded, pyRegion.StartLine);
        Assert.Contains("def compute(x):", unfolded);
        Assert.Contains("return y + 10", unfolded);
        Assert.Equal(md.Trim(), unfolded.Trim());
    }

    [Fact]
    public void FoldAllCodeBlocks_And_UnfoldAll_WorksSeamlessly()
    {
        string md = """
            # Multi Block Document

            ```js
            console.log("first block");
            ```

            Middle content.

            ```rust
            fn main() {
                println!("second block");
            }
            ```

            End.
            """;

        string folded = EditorFoldingService.FoldAllCodeBlocks(md);
        Assert.Equal(2, EditorFoldingService.GetFoldedCount(folded));
        Assert.DoesNotContain("console.log", folded);
        Assert.DoesNotContain("println!", folded);

        string unfolded = EditorFoldingService.UnfoldAll(folded);
        Assert.Equal(0, EditorFoldingService.GetFoldedCount(unfolded));
        Assert.Contains("console.log", unfolded);
        Assert.Contains("println!", unfolded);
    }

    [Fact]
    public void ToggleFoldAtLine_TogglesCorrectly()
    {
        string md = """
            Line 1

            ```csharp
            var a = 1;
            var b = 2;
            ```

            Line 8
            """;

        // Toggle at line 3 (start of code block)
        string folded = EditorFoldingService.ToggleFoldAtLine(md, 3);
        Assert.True(EditorFoldingService.GetFoldedCount(folded) == 1);

        // Toggle at line 3 again to unfold
        string unfolded = EditorFoldingService.ToggleFoldAtLine(folded, 3);
        Assert.True(EditorFoldingService.GetFoldedCount(unfolded) == 0);
        Assert.Contains("var a = 1;", unfolded);
    }
}
