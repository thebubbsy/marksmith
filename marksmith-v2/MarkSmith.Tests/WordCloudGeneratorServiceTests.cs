using MarkSmith.Core.Services;
using System.Linq;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class WordCloudGeneratorServiceTests
    {
        [Fact]
        public void Generate_ExtractsWordFrequenciesAndScalesFontSizes()
        {
            var service = new WordCloudGeneratorService();
            string input = "# Document Analytics\n\nAnalytics engine provides analytics data for document analytics. Processing markdown analytics engine.";

            var items = service.Generate(input);

            Assert.NotEmpty(items);
            Assert.Equal("analytics", items.First().Word);
            Assert.True(items.First().Frequency >= 4);
            Assert.True(items.First().ScaledFontSizePx > items.Last().ScaledFontSizePx);
        }

        [Fact]
        public void Generate_ExcludesStopWordsAndCodeBlocks()
        {
            var service = new WordCloudGeneratorService();
            string input = "The is at on which. ```csharp\nstring secretCode = \"secret\";\n```";

            var items = service.Generate(input);

            Assert.DoesNotContain(items, x => x.Word == "secret");
            Assert.DoesNotContain(items, x => x.Word == "the");
        }
    }
}
