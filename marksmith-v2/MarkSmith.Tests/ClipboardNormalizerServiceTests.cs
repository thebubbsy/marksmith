using System;
using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class ClipboardNormalizerServiceTests
    {
        private readonly ClipboardNormalizerService _service = new ClipboardNormalizerService();

        [Fact]
        public void EmptyInput_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, _service.NormalizeHtmlToMarkdown(null));
            Assert.Equal(string.Empty, _service.NormalizeHtmlToMarkdown("   "));
        }

        [Fact]
        public void PlainText_ReturnsTrimmedText()
        {
            string plain = "Hello world markdown text";
            Assert.Equal(plain, _service.NormalizeHtmlToMarkdown(plain));
        }

        [Fact]
        public void Headings_ConvertCorrectly()
        {
            string html = "<h1>Heading 1</h1><h2>Heading 2</h2>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("# Heading 1", md);
            Assert.Contains("## Heading 2", md);
        }

        [Fact]
        public void BoldAndItalic_ConvertCorrectly()
        {
            string html = "<p>This is <b>bold</b> and <i>italic</i> content</p>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("**bold**", md);
            Assert.Contains("*italic*", md);
        }

        [Fact]
        public void Hyperlinks_ConvertCorrectly()
        {
            string html = "<a href=\"https://marksmith.app\">Marksmith App</a>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("[Marksmith App](https://marksmith.app)", md);
        }

        [Fact]
        public void InlineCodeAndPreBlocks_ConvertCorrectly()
        {
            string html = "<p>Use <code>dotnet test</code> command</p><pre><code class=\"language-csharp\">var x = 10;</code></pre>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("`dotnet test`", md);
            Assert.Contains("```csharp", md);
            Assert.Contains("var x = 10;", md);
        }

        [Fact]
        public void UnorderedList_ConvertsCorrectly()
        {
            string html = "<ul><li>Item 1</li><li>Item 2</li></ul>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("- Item 1", md);
            Assert.Contains("- Item 2", md);
        }

        [Fact]
        public void Blockquote_ConvertsCorrectly()
        {
            string html = "<blockquote>A quoted sentence</blockquote>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("> A quoted sentence", md);
        }

        [Fact]
        public void Images_ConvertCorrectly()
        {
            string html = "<img src=\"https://example.com/logo.png\" alt=\"Logo\" />";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("![Logo](https://example.com/logo.png)", md);
        }

        [Fact]
        public void HtmlEntities_DecodeCorrectly()
        {
            string html = "<p>Standard &amp; Custom &lt;Test&gt;</p>";
            string md = _service.NormalizeHtmlToMarkdown(html);

            Assert.Contains("Standard & Custom <Test>", md);
        }
    }
}
