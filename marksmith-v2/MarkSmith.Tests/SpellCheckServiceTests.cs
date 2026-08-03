using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class SpellCheckServiceTests
    {
        private readonly SpellCheckService _service = new SpellCheckService();

        [Fact]
        public void CheckDocument_IdentifiesUnknownWords()
        {
            string markdown = "This is a good test document with a mispelled word.";
            var issues = _service.CheckDocument(markdown);

            Assert.Contains(issues, i => i.Word == "mispelled");
        }

        [Fact]
        public void CheckDocument_IgnoresCodeBlocksAndUrls()
        {
            string markdown = "Here is a url: https://example.com/foo and code:\n```csharp\nvar qwertyuiop = 123;\n```";
            var issues = _service.CheckDocument(markdown);

            Assert.DoesNotContain(issues, i => i.Word == "qwertyuiop");
            Assert.DoesNotContain(issues, i => i.Word == "example");
        }

        [Fact]
        public void AddCustomWord_PreventsFlaggingCustomTerms()
        {
            _service.AddCustomWord("marksmith");
            string markdown = "Welcome to marksmith document editor!";
            var issues = _service.CheckDocument(markdown);

            Assert.DoesNotContain(issues, i => i.Word.ToLowerInvariant() == "marksmith");
        }

        [Fact]
        public void CheckDocument_ProvidesLevenshteinSuggestions()
        {
            string markdown = "This is a documnt.";
            var issues = _service.CheckDocument(markdown);

            var issue = Assert.Single(issues, i => i.Word == "documnt");
            Assert.Contains("document", issue.SuggestedReplacements);
        }
    }
}
