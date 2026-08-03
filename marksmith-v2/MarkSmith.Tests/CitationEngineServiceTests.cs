using System.Collections.Generic;
using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class CitationEngineServiceTests
    {
        private readonly CitationEngineService _service = new CitationEngineService();

        [Fact]
        public void Process_NoCitations_ReturnsOriginalMarkdown()
        {
            var result = _service.Process("Hello world", new Dictionary<string, CitationEntry>());
            Assert.Equal("Hello world", result.ProcessedMarkdown);
            Assert.Empty(result.UsedCitations);
        }

        [Fact]
        public void Process_InlineCitation_ReplacesWithNumericIndexAndAppendsReferences()
        {
            string markdown = "According to recent studies [@smith2023], markdown formatting works well.";
            var library = new Dictionary<string, CitationEntry>
            {
                ["smith2023"] = new CitationEntry
                {
                    Key = "smith2023",
                    Author = "J. Smith",
                    Title = "Markdown PDF Generation",
                    Year = 2023,
                    Publisher = "Tech Press"
                }
            };

            var result = _service.Process(markdown, library);

            Assert.Contains("[1]", result.ProcessedMarkdown);
            Assert.Contains("## References", result.ProcessedMarkdown);
            Assert.Contains("1. J. Smith (2023). *Markdown PDF Generation*, Tech Press.", result.ProcessedMarkdown);
            Assert.Single(result.UsedCitations);
        }

        [Fact]
        public void Process_MultipleCitationsInOneBlock_FormatsIndicesSeparatedByCommas()
        {
            string markdown = "Several authors agree [@smith2023; @jones2024].";
            var library = new Dictionary<string, CitationEntry>
            {
                ["smith2023"] = new CitationEntry { Key = "smith2023", Author = "J. Smith", Title = "Paper A", Year = 2023 },
                ["jones2024"] = new CitationEntry { Key = "jones2024", Author = "A. Jones", Title = "Paper B", Year = 2024 }
            };

            var result = _service.Process(markdown, library);

            Assert.Contains("[1, 2]", result.ProcessedMarkdown);
            Assert.Equal(2, result.UsedCitations.Count);
        }
    }
}
