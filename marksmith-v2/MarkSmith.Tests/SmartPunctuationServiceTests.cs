using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class SmartPunctuationServiceTests
    {
        [Fact]
        public void Process_ConvertsQuotesDashesEllipsesInNormalText()
        {
            var service = new SmartPunctuationService();
            string input = "\"Hello world!\" It's a test... with -- en dash and --- em dash.";

            string result = service.Process(input);

            Assert.Equal("“Hello world!” It’s a test… with – en dash and — em dash.", result);
        }

        [Fact]
        public void Process_IgnoresPunctuationInsideCodeAndMathBlocks()
        {
            var service = new SmartPunctuationService();
            string input = "\"Hello\" `\"code\" ... --` and ```\n\"block\" -- ...\n``` with $ \"math\" -- $";

            string result = service.Process(input);

            Assert.Contains("“Hello”", result);
            Assert.Contains("`\"code\" ... --`", result);
            Assert.Contains("```\n\"block\" -- ...\n```", result);
            Assert.Contains("$ \"math\" -- $", result);
        }
    }
}
