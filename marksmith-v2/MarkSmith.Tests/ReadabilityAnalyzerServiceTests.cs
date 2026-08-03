using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class ReadabilityAnalyzerServiceTests
    {
        private readonly ReadabilityAnalyzerService _service = new ReadabilityAnalyzerService();

        [Fact]
        public void Analyze_EmptyMarkdown_ReturnsEmptyResult()
        {
            var result = _service.Analyze("");
            Assert.Equal(0, result.TotalWords);
            Assert.Equal(0, result.TotalSentences);
        }

        [Fact]
        public void Analyze_SimpleMarkdown_CalculatesReadabilityScores()
        {
            string markdown = "# Sample Title\n\nThis is a simple sentence. It is easy to read.";
            var result = _service.Analyze(markdown);

            Assert.True(result.TotalWords > 0);
            Assert.True(result.TotalSentences >= 2);
            Assert.True(result.FleschReadingEase > 0);
            Assert.False(string.IsNullOrEmpty(result.ReadabilityLabel));
        }

        [Fact]
        public void Analyze_MarkdownWithCodeFences_StripsCodeBeforeAnalyzing()
        {
            string markdown = "Here is some text.\n\n```csharp\npublic void Method() { Console.WriteLine(\"Hello\"); }\n```\n\nAnother simple sentence.";
            var result = _service.Analyze(markdown);

            Assert.True(result.TotalWords < 15);
            Assert.True(result.TotalSentences >= 2);
        }
    }
}
