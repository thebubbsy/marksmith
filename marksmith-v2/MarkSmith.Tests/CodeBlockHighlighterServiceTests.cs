using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class CodeBlockHighlighterServiceTests
    {
        private readonly CodeBlockHighlighterService _service = new CodeBlockHighlighterService();

        [Fact]
        public void ParseFenceOptions_ExtractsLanguageAndLineHighlights()
        {
            string fence = "csharp {1,3-5} showLineNumbers";
            var options = _service.ParseFenceOptions(fence);

            Assert.Equal("csharp", options.Language);
            Assert.True(options.ShowLineNumbers);
            Assert.Contains(1, options.HighlightedLines);
            Assert.Contains(3, options.HighlightedLines);
            Assert.Contains(4, options.HighlightedLines);
            Assert.Contains(5, options.HighlightedLines);
            Assert.DoesNotContain(2, options.HighlightedLines);
        }

        [Fact]
        public void ParseFenceOptions_SingleLine_ParsesCorrectly()
        {
            string fence = "python {2}";
            var options = _service.ParseFenceOptions(fence);

            Assert.Equal("python", options.Language);
            Assert.Single(options.HighlightedLines);
            Assert.Contains(2, options.HighlightedLines);
        }

        [Fact]
        public void RenderCodeBlock_AppliesHighlightClassToSpecifiedLines()
        {
            string code = "var a = 1;\nvar b = 2;\nvar c = 3;";
            var options = new CodeBlockOptions
            {
                Language = "csharp",
                ShowLineNumbers = true
            };
            options.HighlightedLines.Add(2);

            string html = _service.RenderCodeBlock(code, options);

            Assert.Contains("<pre class=\"code-block language-csharp\">", html);
            Assert.Contains("<span class=\"line-num\">  1</span>", html);
            Assert.Contains("highlighted-line", html);
            Assert.Contains("<span class=\"line-content\">var b = 2;</span>", html);
        }

        [Fact]
        public void RenderCodeBlock_EncodesHtmlSpecialCharacters()
        {
            string code = "List<string> items = new List<string>();";
            var options = new CodeBlockOptions();

            string html = _service.RenderCodeBlock(code, options);

            Assert.Contains("List&lt;string&gt;", html);
        }
    }
}
