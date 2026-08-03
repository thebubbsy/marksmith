using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class UserThemeStylesheetServiceTests
{
    [Fact]
    public void ScopeCss_Wraps_CSS_In_TargetClass()
    {
        var raw = "h1 { color: red; }";
        var scoped = UserThemeStylesheetService.ScopeCss(raw, "doc-body");
        Assert.Contains(".doc-body {", scoped);
        Assert.Contains("h1 { color: red; }", scoped);
    }

    [Fact]
    public void ScopeCss_Strips_Dangerous_Expressions()
    {
        var raw = "body { width: expression(alert(1)); background: url(javascript:alert(1)); }";
        var scoped = UserThemeStylesheetService.ScopeCss(raw);
        Assert.DoesNotContain("expression(", scoped);
        Assert.DoesNotContain("javascript:", scoped);
    }

    [Fact]
    public void InjectIntoHtml_Inserts_StyleTag_Before_ClosingHead()
    {
        var html = "<html><head><title>Test</title></head><body><h1>Hi</h1></body></html>";
        var custom = "p { font-size: 16px; }";
        var result = UserThemeStylesheetService.InjectIntoHtml(html, custom);

        Assert.Contains("<style id=\"user-custom-stylesheet\">", result);
        Assert.Contains("p { font-size: 16px; }", result);
        Assert.Contains("</head>", result);
    }
}
