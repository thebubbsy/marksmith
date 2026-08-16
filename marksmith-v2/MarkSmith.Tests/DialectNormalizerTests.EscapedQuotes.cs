using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// ChatGPT & other AI providers: escaped double quotes in tables (\" from JSON-to-Markdown
// conversions) are normalized back to plain quotes on table lines only.
public class EscapedQuotesTests
{
    private static string N(string md) => DialectNormalizer.Apply(md, -1);

    [Fact]
    public void ChatGPT_Escaped_Quotes_In_Table_Are_Unescaped()
    {
        var md = "| a | \\\"b\\\" | c |\n| --- | --- | --- |\n| 1 | \\\"2\\\" | 3 |";
        var result = N(md);
        Assert.Contains("| a | \"b\" | c |", result);
        Assert.Contains("| 1 | \"2\" | 3 |", result);
    }

    [Fact]
    public void ChatGPT_Escaped_Quotes_Outside_Table_Are_Untouched()
    {
        var md = "This is a \\\"quote\\\" outside a table.";
        var result = N(md);
        Assert.Contains("This is a \\\"quote\\\" outside a table.", result);
    }
}
