using System;
using System.IO;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #326, the Link Insert dialog and the find bar's Replace box:
// - the editor's selection opens in the dialog (words as the link text, an address as the URL);
// - inserting with a selection replaces it ("[Read the docs](url)docs" kept the old words);
// - brackets in the link text are escaped, so "a ] b" no longer ends the link early;
// - a local path with spaces or unbalanced parentheses is wrapped in <…>, spaces in a web address
//   become %20, a title ("…" after the address) is kept;
// - a link pasted as <https://…> or "https://…" is unwrapped, shared with the Video dialog;
// - the Replace box recalls earlier replacements with Up and Down and lists them in its menu.
public class Run326LinkAndReplaceTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return end < 0 ? code[start..] : code[start..end];
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("Read the docs", "Read the docs", "")]
    [InlineData("  docs  ", "docs", "")]
    [InlineData("https://example.com/a", "", "https://example.com/a")]
    [InlineData("<https://example.com/a>", "", "https://example.com/a")]
    [InlineData("mailto:me@example.com", "", "mailto:me@example.com")]
    [InlineData("www.example.com", "", "www.example.com")]
    [InlineData("see https://example.com", "see https://example.com", "")]
    [InlineData("line one\rline two", "", "")]
    public void LinkPrefill_splits_words_from_addresses(string selection, string text, string url) =>
        Assert.Equal((text, url), InsertSnippetBuilder.LinkPrefill(selection));

    [Theory]
    [InlineData("", "")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("<https://example.com>", "https://example.com")]
    [InlineData("\"https://example.com\"", "https://example.com")]
    [InlineData("https://example.com/my page", "https://example.com/my%20page")]
    [InlineData("my notes.md", "<my notes.md>")]
    [InlineData("./a(b.md", "<./a(b.md>")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("https://example.com \"Home page\"", "https://example.com \"Home page\"")]
    [InlineData("#intro", "#intro")]
    public void LinkDestination_is_always_a_valid_link_target(string url, string expected) =>
        Assert.Equal(expected, InsertSnippetBuilder.LinkDestination(url));

    [Fact]
    public void Link_escapes_brackets_in_the_text()
    {
        Assert.Equal(@"[a \] b](https://x.y)", InsertSnippetBuilder.Link("a ] b", "https://x.y"));
        Assert.Equal("[notes](<my notes.md>)", InsertSnippetBuilder.Link("notes", "my notes.md"));
    }

    [Theory]
    [InlineData("a ] b", "https://x.y", "https://x.y")]
    [InlineData("notes", "my notes.md", "my%20notes.md")]
    [InlineData("page", "https://example.com/my page", "https://example.com/my%20page")]
    public void Link_renders_as_one_anchor(string text, string url, string href)
    {
        var html = new MarkdownHtmlService().Render(InsertSnippetBuilder.Link(text, url),
            new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Contains($"href=\"{href}\"", html);
        Assert.Contains($">{text}</a>", html);
        Assert.DoesNotContain("](", html);
    }

    private static int CountOf(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void RecallHistory_steps_back_and_restores_the_draft()
    {
        var h = new RecallHistory(3);
        Assert.Null(h.Older("draft"));
        Assert.Null(h.Newer());
        h.Record("one");
        h.Record("two ");
        h.Record("");
        h.Record("one");
        Assert.Equal(new[] { "one", "two " }, h.Items);
        Assert.Equal("one", h.Older("typing"));
        Assert.Equal("two ", h.Older("ignored"));
        Assert.Equal("two ", h.Older("ignored")); // oldest: stays put
        Assert.Equal("one", h.Newer());
        Assert.Equal("typing", h.Newer());
        Assert.Null(h.Newer());
        h.Record("a"); h.Record("b"); h.Record("c");
        Assert.Equal(3, h.Items.Count);
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("foo ", "“foo ”")]
    [InlineData(" ", "“ ”")]
    public void RecallHistory_label_shows_edge_spaces(string entry, string expected) =>
        Assert.Equal(expected, RecallHistory.Label(entry));

    [Fact]
    public void LinkClick_prefills_and_replaces_the_selection()
    {
        var main = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        var click = Method(main, "private async void OnLinkClick(");
        Assert.Contains("Services.InsertSnippetBuilder.LinkPrefill(tb.SelectedText)", click);
        Assert.Contains("new Views.LinkInsertControl(initialText, initialUrl)", click);
        Assert.Contains("tb.SelectedText = lead + link + trail;", click);
        Assert.Contains("Services.InsertSnippetBuilder.LinkDestination(control.Url)", click);

        var dialogs = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("public LinkInsertControl(string initialText = \"\", string initialUrl = \"\")", dialogs);
        Assert.Equal(2, CountOf(dialogs, "public string Url => Snippets.UnwrapPasted(_url.Text);"));
    }

    [Fact]
    public void ReplaceBox_recalls_and_lists_earlier_replacements()
    {
        var main = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        Assert.Equal(2, CountOf(main, "_replaceHistory.Record(replacement);"));
        var keys = Method(main, "private void OnReplaceTextBoxKeyDown(");
        Assert.Contains("_replaceHistory.Older(ReplaceTextBox.Text)", keys);
        Assert.Contains("_replaceHistory.Newer()", keys);
        // Only handled when something was recalled, so the key keeps its job otherwise.
        Assert.Contains("if (recalled is not null)", keys);
        var menu = Method(main, "private void FillReplaceTextBoxMenu(");
        Assert.Contains("Text = \"Recent replacements\"", menu);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+Alt+Enter\"", menu);

        var xaml = Read("MarkSmith.Desktop/MainWindow.xaml");
        Assert.Contains("Up and Down: earlier replacements", xaml);
        Assert.Contains("Up and Down recall earlier replacements", xaml);
    }
}
