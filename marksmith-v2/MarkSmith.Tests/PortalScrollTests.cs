using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>Pins the Looking Glass portal fix: the click handler must map the click into true
/// DOCUMENT coordinates (adding window.scrollX/Y). Without it, a click on a scrolled page opens
/// the aperture at the top of the document and the source caret estimates the top line — the
/// "portal opens at the top / right edge instead of on the clicked word" bug.</summary>
public class PortalScrollTests
{
    [Fact]
    public void PortalScript_DocPoint_AccountsForScroll()
    {
        var settings = new AppSettings { LookingGlassMode = true, IncludeToc = false };
        var theme = new ThemeDefinition("Light", "#ffffff", "#111111", "#222222", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");
        var html = AppServices.MarkdownHtml.Render("# Hi\n\nSome body text to render.", settings, theme, interactive: true);

        // The docPoint mapping must add the scroll offsets to the viewport-relative clientX/Y.
        Assert.Contains("+ window.scrollX", html);
        Assert.Contains("+ window.scrollY", html);
        // And the portal script itself is only emitted in interactive Looking Glass mode.
        Assert.Contains("__portalSetSource", html);
    }

    [Fact]
    public void PortalScript_NotEmitted_WhenLookingGlassOff()
    {
        var settings = new AppSettings { LookingGlassMode = false };
        var theme = new ThemeDefinition("Light", "#ffffff", "#111111", "#222222", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");
        var html = AppServices.MarkdownHtml.Render("# Hi", settings, theme, interactive: true);
        Assert.DoesNotContain("__portalSetSource", html);
    }
}
