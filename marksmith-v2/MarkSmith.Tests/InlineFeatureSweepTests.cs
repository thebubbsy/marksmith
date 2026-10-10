using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #67: inline features through the preview/PDF and Word. A footnote definition straight after
/// a definition list printed raw ("[^1]") in every format: Markdig took it for the next term,
/// then a link reference definition.
/// </summary>
public class InlineFeatureSweepTests
{
    private const string DlThenFootnote = "Footnote here.[^1]\n\nTerm\n: Its definition.\n\n[^1]: The footnote text.\n";

    private static string Render(string md) =>
        new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

    [Fact]
    public void Footnote_after_a_definition_list_links_in_the_preview()
    {
        var html = Render(DlThenFootnote);
        Assert.Contains("class=\"footnote-ref\"", html);
        Assert.Contains("<dt>Term</dt>", html);
        Assert.DoesNotContain("[^1]", html);
    }

    [Fact]
    public void Footnote_after_a_definition_list_is_a_Word_footnote()
    {
        var xml = MarkSmith.Core.Tests.E2ETestHelpers.ExportDocxXml(DlThenFootnote);
        Assert.Contains("footnoteReference", xml);
        Assert.DoesNotContain("[^1]", xml);
    }
}
