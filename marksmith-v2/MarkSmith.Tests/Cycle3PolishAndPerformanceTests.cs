using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests
{
    public class Cycle3PolishAndPerformanceTests
    {
        [Fact]
        public void FastMathTokenizer_DetectsInlineAndDisplayMath_WithoutAllocatingSubstrings()
        {
            string markdown = "Here is an inline formula $E = mc^2$ and a display block:\n\n$$\\int_0^\\infty e^{-x^2} dx = \\frac{\\sqrt{\\pi}}{2}$$\n\nAlso escaped \\$100 currency.";

            var spans = FastMathTokenizer.ScanMathSpans(markdown.AsSpan());

            Assert.Equal(2, spans.Count);

            // First span: inline math
            Assert.Equal(MathSpanKind.Inline, spans[0].Kind);
            string inner1 = markdown.Substring(spans[0].InnerStart, spans[0].InnerLength);
            Assert.Equal("E = mc^2", inner1);

            // Second span: display math
            Assert.Equal(MathSpanKind.Display, spans[1].Kind);
            string inner2 = markdown.Substring(spans[1].InnerStart, spans[1].InnerLength);
            Assert.Contains("\\int_0^\\infty", inner2);
        }

        [Fact]
        public void FastMathTokenizer_IgnoresCodeFencesAndEscapes()
        {
            string markdown = "```markdown\n$this is in code fence$\n```\nPrice is \\$50 and not math.";

            var spans = FastMathTokenizer.ScanMathSpans(markdown.AsSpan());

            Assert.Empty(spans);
        }

        [Theory]
        [InlineData("", AdaptivePreviewDebouncer.MinDebounceMs)]
        [InlineData("Short document with a few words", AdaptivePreviewDebouncer.MinDebounceMs)]
        public void AdaptivePreviewDebouncer_CalculatesFastTime_ForSmallDocuments(string text, int expectedMin)
        {
            int ms = AdaptivePreviewDebouncer.ComputeDebounceMilliseconds(text);
            Assert.Equal(expectedMin, ms);
        }

        [Fact]
        public void AdaptivePreviewDebouncer_ScalesForHeavyDiagrams()
        {
            string heavyMd = "# Complex Architecture\n\n:::smartart process\n- Step 1\n- Step 2\n:::\n\n```mermaid\ngraph TD\nA --> B\n```";

            int ms = AdaptivePreviewDebouncer.ComputeDebounceMilliseconds(heavyMd);
            Assert.True(ms > AdaptivePreviewDebouncer.MinDebounceMs);
        }
    }
}
