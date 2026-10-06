using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Task-list items become form-control checkboxes (so DOCX export can emit real Word checkbox
// content controls), but the conversion used to swallow the "- " list marker: the item stopped
// being a list item and Markdown folded it into the previous bullet as lazy continuation text —
// "Two customers ☑ Ship the polish ☐ Regenerate" rendered as ONE bullet.
public class TaskListStructureTests
{
    private static readonly ThemeDefinition Light = new(
        "Light", "#ffffff", "#1a1a1a", "#111111", "#f4f4f4", "#d9d9d9", "#0078d4", "#e8f4fd", "#bfbfbf");

    private static string Render(string md) => new MarkdownHtmlService().Render(md, new AppSettings(), Light);

    [Fact]
    public void Task_items_after_plain_bullets_stay_separate_list_items()
    {
        var html = Render("- Revenue grew\n- Two customers\n- [x] Ship the polish\n- [ ] Regenerate\n");

        Assert.Contains("<li>Two customers</li>", html);
        Assert.Contains("<li><input type=\"checkbox\" class=\"ms-form-checkbox\" checked /> Ship the polish</li>", html);
        Assert.Contains("<li><input type=\"checkbox\" class=\"ms-form-checkbox\" /> Regenerate</li>", html);
    }

    [Theory]
    [InlineData("* [x] Starred marker")]
    [InlineData("+ [x] Plus marker")]
    [InlineData("1. [x] Ordered marker")]
    [InlineData("  - [x] Indented marker")]
    public void Every_list_marker_style_is_kept(string line)
    {
        var html = Render("- Context\n" + line + "\n");
        Assert.Matches(@"<li><input type=""checkbox"" class=""ms-form-checkbox"" checked /> \w+ marker</li>", html);
    }

    [Fact]
    public void Checkbox_stands_in_for_the_bullet()
    {
        var html = Render("- [ ] Todo\n");
        Assert.Contains("li:has(> .ms-form-checkbox:first-child)", html);
    }

    [Fact]
    public void Inline_checkboxes_outside_lists_still_convert()
    {
        var html = Render("| Item | Done |\n|---|---|\n| Audit | [x] |\n");
        Assert.Contains("ms-form-checkbox", html);
    }
}
