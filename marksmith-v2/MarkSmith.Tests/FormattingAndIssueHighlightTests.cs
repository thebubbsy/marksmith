using System;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class FormattingAndIssueHighlightTests
    {
        public static (string leadingBreak, string coreText, string trailingBreak) SplitSelectionLineBreaks(string selected)
        {
            if (string.IsNullOrEmpty(selected)) return ("", "", "");

            int leadEnd = 0;
            while (leadEnd < selected.Length && (selected[leadEnd] == '\r' || selected[leadEnd] == '\n'))
            {
                leadEnd++;
            }

            int trailStart = selected.Length;
            while (trailStart > leadEnd && (selected[trailStart - 1] == '\r' || selected[trailStart - 1] == '\n'))
            {
                trailStart--;
            }

            string leadingBreak = selected.Substring(0, leadEnd);
            string coreText = selected.Substring(leadEnd, trailStart - leadEnd);
            string trailingBreak = selected.Substring(trailStart);

            return (leadingBreak, coreText, trailingBreak);
        }

        public static string ApplyFormatting(string selected, string prefix, string suffix)
        {
            if (string.IsNullOrEmpty(selected)) return prefix + suffix;

            var (leadingBreak, coreText, trailingBreak) = SplitSelectionLineBreaks(selected);

            bool isInline = !string.IsNullOrEmpty(suffix);
            if (isInline && coreText.Length >= (prefix.Length + suffix.Length) && coreText.StartsWith(prefix) && coreText.EndsWith(suffix))
            {
                string unformatted = coreText.Substring(prefix.Length, coreText.Length - prefix.Length - suffix.Length);
                return leadingBreak + unformatted + trailingBreak;
            }

            if (string.IsNullOrEmpty(suffix) && (prefix.TrimEnd() == "#" || prefix.TrimEnd() == "##" || prefix.TrimEnd() == "###" || prefix.TrimEnd() == "####" || prefix.TrimEnd() == "-" || prefix.TrimEnd() == "1." || prefix.TrimEnd() == "- []" || prefix.TrimEnd() == ">"))
            {
                string[] lines = coreText.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length > 0)
                    {
                        if (line.EndsWith("\r"))
                            lines[i] = prefix + line.Substring(0, line.Length - 1) + "\r";
                        else
                            lines[i] = prefix + line;
                    }
                }
                return leadingBreak + string.Join("\n", lines) + trailingBreak;
            }

            return leadingBreak + prefix + coreText + suffix + trailingBreak;
        }

        [Fact]
        public void Strikethrough_TripleClickedLine_PreservesNewlineAfterSuffix()
        {
            string selected = "This is a triple-clicked sentence.\r\n";
            string formatted = ApplyFormatting(selected, "~~", "~~");

            Assert.Equal("~~This is a triple-clicked sentence.~~\r\n", formatted);
        }

        [Fact]
        public void Bold_TripleClickedLine_PreservesNewlineAfterSuffix()
        {
            string selected = "This is a bold line.\n";
            string formatted = ApplyFormatting(selected, "**", "**");

            Assert.Equal("**This is a bold line.**\n", formatted);
        }

        [Fact]
        public void Bold_AlreadyBoldSelection_TogglesOffBold()
        {
            string selected = "**Already bold text**\r\n";
            string formatted = ApplyFormatting(selected, "**", "**");

            Assert.Equal("Already bold text\r\n", formatted);
        }

        [Fact]
        public void Strikethrough_AlreadyStrikethroughSelection_TogglesOffStrikethrough()
        {
            string selected = "~~Strikethrough text~~\n";
            string formatted = ApplyFormatting(selected, "~~", "~~");

            Assert.Equal("Strikethrough text\n", formatted);
        }

        [Fact]
        public void Italic_TextWithLeadingAndTrailingNewlines_WrapsCoreOnly()
        {
            string selected = "\n\nSample text\r\n\r\n";
            string formatted = ApplyFormatting(selected, "*", "*");

            Assert.Equal("\n\n*Sample text*\r\n\r\n", formatted);
        }

        [Fact]
        public void Heading_SingleLineSelection_PrependsPrefixAtLineStart()
        {
            string selected = "Section Heading\r\n";
            string formatted = ApplyFormatting(selected, "# ", "");

            Assert.Equal("# Section Heading\r\n", formatted);
        }

        [Fact]
        public void List_MultiLineSelection_PrependsPrefixToEachLine()
        {
            string selected = "Item 1\r\nItem 2\r\nItem 3\r\n";
            string formatted = ApplyFormatting(selected, "- ", "");

            Assert.Equal("- Item 1\r\n- Item 2\r\n- Item 3\r\n", formatted);
        }
    }
}
