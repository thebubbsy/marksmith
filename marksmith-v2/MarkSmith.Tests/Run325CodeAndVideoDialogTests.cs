using System;
using System.IO;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #325, the Code block and Video embed Insert dialogs:
// - code holding a ``` line gets a ~~~ fence (a longer backtick fence when it holds both), so the
//   block no longer ends early and spills the rest of the code out as Markdown;
// - a language with a backtick is refused (the line stops being a fence);
// - the card follows the language while it is typed, not only once it is submitted;
// - the language tooltip / help says any fence id can be typed;
// - the video dialog refuses a provider that disagrees with the pasted link;
// - a link pasted as <https://…> or "https://…" is cleaned, and a quote in a link is written as %22.
public class Run325CodeAndVideoDialogTests
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

    private const string Dialogs = "MarkSmith.Desktop/Views/InsertDialogControls.cs";

    private static string Class(string code, string name)
    {
        var start = code.IndexOf($"public sealed class {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, name);
        var end = code.IndexOf("\npublic sealed class ", start + 1, StringComparison.Ordinal);
        return end < 0 ? code[start..] : code[start..end];
    }

    [Theory]
    [InlineData("var x = 1;", "```")]
    [InlineData("", "```")]
    [InlineData("Use ``inline`` ticks", "```")]
    [InlineData("```js\nalert(1)\n```", "~~~")]
    [InlineData("  ```", "~~~")]
    [InlineData("```\n~~~", "````")]
    [InlineData("`````\n~~~", "``````")]
    public void CodeFence_never_closes_on_a_line_of_the_code(string body, string fence) =>
        Assert.Equal(fence, InsertSnippetBuilder.CodeFence(body));

    [Fact]
    public void CodeFence_reads_WinUI_line_breaks()
    {
        // A WinUI TextBox separates lines with a bare '\r'; the host passes Body as it is.
        Assert.Equal("~~~", InsertSnippetBuilder.CodeFence("intro\r```\rmore"));
    }

    [Fact]
    public void CodeBlock_wraps_fenced_code_in_a_tilde_fence()
    {
        Assert.Equal("\n~~~markdown\n```js\nalert(1)\n```\n~~~\n", InsertSnippetBuilder.CodeBlock("markdown", "```js\nalert(1)\n```"));
        Assert.Equal("\n```csharp\nvar x = 1;\n```\n", InsertSnippetBuilder.CodeBlock("csharp", "var x = 1;"));
    }

    [Fact]
    public void CodeBlock_with_inner_fence_renders_as_one_block()
    {
        var html = new MarkdownHtmlService().Render(InsertSnippetBuilder.CodeBlock("markdown", "```js\nalert(1)\n```\n# Not a heading"),
            new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.DoesNotContain("<h1", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("# Not a heading", html);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("csharp", null)]
    [InlineData("c`s", "A language name can't contain a backtick (`).")]
    public void CodeLanguageProblem_refuses_a_backtick(string language, string? expected) =>
        Assert.Equal(expected, InsertCountText.CodeLanguageProblem(language));

    [Fact]
    public void CodeDialog_checks_the_language_and_reads_it_while_typed()
    {
        var code = Class(Read(Dialogs), "CodeBlockInsertControl");
        Assert.Contains("protected override string? Problem => InsertCountText.CodeLanguageProblem(SelectedLanguage);", code);
        Assert.Contains("inner.TextChanged += (_, _) => Refresh();", code);
        Assert.Contains("var text = (_langText?.Text ?? _lang.SelectedItem as string ?? _lang.Text ?? \"\").Trim();", code);
        Assert.Contains("Pick one or type any fence id", code);
        Assert.Contains("ToolTipService.SetToolTip(_lang, LanguageHelp);", code);
        // The inner edit box takes focus, so it carries the help too.
        Assert.Contains("AutomationProperties.SetHelpText(inner, LanguageHelp);", code);
    }

    private static readonly string[] Offered = { "youtube", "vimeo", "loom", "codepen", "bilibili" };

    [Theory]
    [InlineData("youtube", "https://youtu.be/abc", null)]
    [InlineData("vimeo", "https://www.youtube.com/watch?v=abc", "That's a YouTube link, but Vimeo is picked; pick YouTube.")]
    [InlineData("youtube", "https://loom.com/share/x", "That's a Loom link, but YouTube is picked; pick Loom.")]
    [InlineData("youtube", "https://example.com/video", null)]
    [InlineData("youtube", "https://www.figma.com/file/x", null)]
    public void EmbedProviderProblem_flags_only_a_known_mismatch(string picked, string url, string? expected) =>
        Assert.Equal(expected, InsertCountText.EmbedProviderProblem(picked, url, Offered));

    [Fact]
    public void Embed_writes_a_quote_in_the_link_as_percent_22()
    {
        Assert.Contains("src=\"https://vimeo.com/1?t=%22a%22\"", InsertSnippetBuilder.Embed("vimeo", "https://vimeo.com/1?t=\"a\""));
        var attrs = ContainerBlockParsers.ParseAttributes(":::embed provider=\"vimeo\" src=\"https://vimeo.com/1?t=%22a%22\"");
        Assert.Equal("https://vimeo.com/1?t=%22a%22", attrs["src"]);
    }

    [Fact]
    public void EmbedDialog_cleans_the_link_and_checks_the_provider()
    {
        var embed = Class(Read(Dialogs), "EmbedInsertControl");
        Assert.Contains("public string Url => Unwrap(_url.Text.Trim());", embed);
        // Only a matching pair: a link's own trailing quote (…?t="a") stays.
        Assert.Contains("(s[0] == '<' && s[^1] == '>') || (s[0] == '\"' && s[^1] == '\"')", embed);
        Assert.DoesNotContain(".Trim('\"'", embed);
        Assert.Contains("DetectProvider(Url)", embed);
        Assert.Contains("? InsertCountText.EmbedProviderProblem(Provider, Url, Providers)", embed);
    }
}
