using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// The preview/PDF stylesheet themed headings, tables and code blocks but never reached body copy:
// links were the browser's #0000EE (dark blue on every dark theme), blockquotes had no quote
// styling, and inline code / kbd / mark / hr were browser defaults. Dark-page detection was also a
// list of theme NAMES that missed Nordic, Forest and every dark custom theme.
public class ThemeProseStylingTests
{
    private static readonly ThemeCatalog Catalog = new();

    private static string Render(string md, string themeName) =>
        new MarkdownHtmlService().Render(md, new AppSettings { Theme = themeName }, Catalog.GetOrDefault(themeName));

    [Theory]
    [InlineData("GitHub Light", false)]
    [InlineData("Solarized Light", false)]
    [InlineData("GitHub Dark", true)]
    [InlineData("Nordic", true)]
    [InlineData("Forest", true)]
    [InlineData("Obsidian", true)]
    [InlineData("Monokai Pro", true)]
    public void Dark_page_is_decided_by_background_not_name(string name, bool dark)
    {
        Assert.Equal(dark, Catalog.GetOrDefault(name).IsDarkPage);
    }

    [Fact]
    public void Custom_dark_theme_with_an_unremarkable_name_is_dark()
    {
        var t = new ThemeDefinition("Midnight Brand", "#101820", "#e6e6e6", "#7fb3ff", "#1b2633", "#334155", "#e6e6e6", "#1b2633", "#94a3b8");
        Assert.True(t.IsDarkPage);
    }

    [Theory]
    [InlineData("Nordic")]
    [InlineData("Forest")]
    public void Dark_themes_missed_by_the_old_name_list_get_dark_alert_colours(string name)
    {
        var html = Render("> [!NOTE]\n> Heads up.\n", name);
        Assert.Contains(".markdown-alert-note { border-left: 5px solid #58a6ff;", html);
        Assert.Contains("<body class=\"ms-dark", html);
    }

    [Theory]
    [InlineData("#000000", "#ffffff", true, "#0969da")]   // achromatic accent (GitHub Light) → link blue
    [InlineData("#b58900", "#fdf6e3", true, "#0969da")]   // Solarized Light's yellow is ~3:1 → link blue
    [InlineData("#bd93f9", "#282a36", false, "#bd93f9")]  // Dracula purple reads fine → keep the accent
    [InlineData("#88c0d0", "#2e3440", false, "#88c0d0")]  // Nordic frost
    [InlineData("#eceff4", "#2e3440", false, "#58a6ff")]  // near-white accent is achromatic → dark-page blue
    [InlineData("not-a-colour", "#ffffff", true, "#0969da")]
    public void Link_colour_uses_the_accent_only_when_it_is_a_legible_colour(string accent, string bg, bool light, string expected)
    {
        Assert.Equal(expected, MarkdownHtmlService.PickLinkColor(accent, bg, light));
    }

    [Theory]
    [InlineData("GitHub Light", "#0969da")]
    [InlineData("GitHub Dark", "#58a6ff")]
    [InlineData("Dracula", "#bd93f9")]
    public void Links_are_never_the_browser_default(string name, string expected)
    {
        var html = Render("A [link](https://example.com).", name);
        Assert.Contains($"a {{ color: {expected};", html);
    }

    [Fact]
    public void Body_copy_elements_are_all_themed()
    {
        var t = Catalog.GetOrDefault("Nordic");
        var html = Render("> quoted\n\nUse `x`, <kbd>K</kbd> and ==hi==.\n\n---\n", "Nordic");

        Assert.Contains($"blockquote:not([class]) {{ margin: 16px 0; padding: 2px 0 2px 18px; border-left: 4px solid {t.Border};", html);
        Assert.Contains($":not(pre) > code {{ background: {t.Code};", html);
        Assert.Contains("kbd { display: inline-block;", html);
        Assert.Contains("mark { background: rgba(250, 204, 21, 0.28);", html);
        Assert.Contains($"hr {{ border: 0; height: 1px; background: {t.Border};", html);
    }

    [Fact]
    public void Quotes_are_not_muted_when_body_text_has_no_contrast_to_spare()
    {
        // Solarized Light body text is ~4:1 on its cream page — muting it further would be faint.
        var html = Render("> quoted\n", "Solarized Light");
        Assert.Contains("border-left: 4px solid #93a1a1; color: #657b83; }", html);

        var dark = Render("> quoted\n", "GitHub Dark");
        Assert.Contains("color: color-mix(in srgb, #c9d1d9 80%, #0d1117); }", dark);
    }

    [Fact]
    public void Light_influence_page_counts_as_light()
    {
        var settings = new AppSettings { Theme = "Dracula", ThemeLightInfluence = true };
        var html = new MarkdownHtmlService().Render("text", settings, Catalog.GetOrDefault("Dracula"));
        Assert.Contains("<body class=\"ms-light", html);
    }
}
