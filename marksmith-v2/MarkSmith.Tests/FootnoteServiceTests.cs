using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class FootnoteServiceTests
    {
        private readonly FootnoteService _service = new FootnoteService();

        [Fact]
        public void EmptyMarkdown_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, _service.ProcessFootnotes(null));
            Assert.Equal(string.Empty, _service.ProcessFootnotes("   "));
        }

        [Fact]
        public void NoFootnotes_ReturnsOriginalMarkdown()
        {
            string md = "# Heading\nStandard markdown content without footnotes.";
            Assert.Equal(md, _service.ProcessFootnotes(md));
        }

        [Fact]
        public void SingleFootnote_TransformsInlineRefAndAppendsSection()
        {
            string md = "Here is a statement[^1].\n\n[^1]: Clarifying explanation text.";
            string result = _service.ProcessFootnotes(md);

            Assert.Contains("<sup class=\"footnote-ref\"><a href=\"#fn-1\" id=\"fnref-1\">[1]</a></sup>", result);
            Assert.Contains("<section class=\"footnotes\"", result);
            Assert.Contains("<li id=\"fn-1\" class=\"footnote-item\">", result);
            Assert.Contains("Clarifying explanation text.", result);
            Assert.Contains("<a href=\"#fnref-1\" class=\"footnote-backref\"", result);
        }

        [Fact]
        public void MultipleFootnotes_IndexesSequentially()
        {
            string md = "First note[^a] and second note[^b].\n\n[^a]: Alpha footnote.\n[^b]: Beta footnote.";
            string result = _service.ProcessFootnotes(md);

            Assert.Contains("<a href=\"#fn-a\" id=\"fnref-a\">[1]</a>", result);
            Assert.Contains("<a href=\"#fn-b\" id=\"fnref-b\">[2]</a>", result);
            Assert.Contains("Alpha footnote.", result);
            Assert.Contains("Beta footnote.", result);
        }

        [Fact]
        public void UnresolvedFootnote_LeavesReferenceIntact()
        {
            string md = "Statement with missing footnote[^missing].";
            string result = _service.ProcessFootnotes(md);

            Assert.Equal(md, result);
        }
    }
}
