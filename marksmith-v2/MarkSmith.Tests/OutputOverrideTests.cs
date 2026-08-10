using MarkSmith.Models;
using Xunit;

namespace MarkSmith.Core.Tests;

// Verifies the browser-extension output-profile contract: every field the extension can send
// (OutputOverride) must actually flow into the settings used for export (AppSettings.CloneWith).
public class OutputOverrideTests
{
    [Fact]
    public void CloneWith_Applies_PdfChrome_And_Security_Overrides()
    {
        var baseSettings = new AppSettings
        {
            MermaidEnabled = false,
            PdfEncrypt = false,
            PdfUserPassword = "old-user",
            PdfOwnerPassword = "old-owner",
            PdfAllowPrinting = true,
            PdfAllowCopying = true,
            PdfAllowModifying = true,
            PdfHeaderTemplate = "",
            PdfFooterTemplate = "",
            AuthorName = "",
        };

        var o = new OutputOverride
        {
            MermaidEnabled = true,
            PdfEncrypt = true,
            PdfUserPassword = "new-user",
            PdfOwnerPassword = "new-owner",
            PdfAllowPrinting = false,
            PdfAllowCopying = false,
            PdfAllowModifying = false,
            PdfHeaderTemplate = "{title}",
            PdfFooterTemplate = "Page {page} of {pages}",
            AuthorName = "Ada Lovelace",
        };

        var s = baseSettings.CloneWith(o);

        Assert.True(s.MermaidEnabled);
        Assert.True(s.PdfEncrypt);
        Assert.Equal("new-user", s.PdfUserPassword);
        Assert.Equal("new-owner", s.PdfOwnerPassword);
        Assert.False(s.PdfAllowPrinting);
        Assert.False(s.PdfAllowCopying);
        Assert.False(s.PdfAllowModifying);
        Assert.Equal("{title}", s.PdfHeaderTemplate);
        Assert.Equal("Page {page} of {pages}", s.PdfFooterTemplate);
        Assert.Equal("Ada Lovelace", s.AuthorName);
    }

    [Fact]
    public void CloneWith_Blank_Strings_And_Null_Fields_Leave_AppSettings_Untouched()
    {
        var baseSettings = new AppSettings
        {
            MermaidEnabled = false,
            PdfEncrypt = true,
            PdfUserPassword = "keep-me",
            PdfHeaderTemplate = "Existing header",
            AuthorName = "Existing author",
        };

        var o = new OutputOverride
        {
            MermaidEnabled = null,
            PdfEncrypt = null,
            PdfUserPassword = "",          // blank → app's own setting wins
            PdfOwnerPassword = "   ",      // whitespace → treated as unset
            PdfHeaderTemplate = "",
            PdfFooterTemplate = null,
            AuthorName = null,
        };

        var s = baseSettings.CloneWith(o);

        Assert.False(s.MermaidEnabled);
        Assert.True(s.PdfEncrypt);
        Assert.Equal("keep-me", s.PdfUserPassword);
        Assert.Equal("Existing header", s.PdfHeaderTemplate);
        Assert.Equal("Existing author", s.AuthorName);
    }

    [Fact]
    public void CloneWith_WithNull_Override_Returns_Pristine_Clone()
    {
        var baseSettings = new AppSettings { MermaidEnabled = false, PdfEncrypt = true, AuthorName = "x" };

        var s = baseSettings.CloneWith(null);

        Assert.False(s.MermaidEnabled);
        Assert.True(s.PdfEncrypt);
        Assert.Equal("x", s.AuthorName);
    }
}
