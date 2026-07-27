using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
{
    public class TagExtractorServiceTests
    {
        private readonly TagExtractorService _service = new TagExtractorService();

        [Fact]
        public void Extract_EmptyMarkdown_ReturnsEmptyLists()
        {
            var result = _service.Extract("");
            Assert.Empty(result.Hashtags);
            Assert.Empty(result.KeyPhrases);
        }

        [Fact]
        public void Extract_Hashtags_ExtractsUniqueTagsIgnoringCodeBlocks()
        {
            string markdown = @"
# Welcome #marksmith

This document covers #pdf-export and #marksmith features.

```
#ignored-in-code-fence
```
`#ignored-in-inline-code`
";
            var result = _service.Extract(markdown);

            Assert.Contains("marksmith", result.Hashtags);
            Assert.Contains("pdf-export", result.Hashtags);
            Assert.DoesNotContain("ignored-in-code-fence", result.Hashtags);
            Assert.DoesNotContain("ignored-in-inline-code", result.Hashtags);
        }

        [Fact]
        public void Extract_KeyPhrases_ReturnsTopFrequencyNonStopWords()
        {
            string markdown = "Marksmith renders document document document export export features quickly.";
            var result = _service.Extract(markdown);

            Assert.NotEmpty(result.KeyPhrases);
            Assert.Equal("document", result.KeyPhrases[0]);
            Assert.Equal("export", result.KeyPhrases[1]);
        }
    }
}
