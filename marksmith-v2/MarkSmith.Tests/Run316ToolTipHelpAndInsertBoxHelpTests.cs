using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #316:
// - every control HoverPolish walks (main window, studios, History, Settings, Suite Hub, Insert
//   dialogs, opened flyouts) gets its tooltip's words as help text, so the keys a tooltip names
//   reach keyboard and screen reader users; help text set by code is never overwritten;
// - Insert dialogs' line-list and chart boxes say the shape a line takes and that Ctrl+Enter inserts;
// - the preview card's copy button names its keys in its help text;
// - Image Insert's Browse button reads its name once (its icon and label are raw);
// - Video's provider tooltip says the provider is picked from the pasted link.
public class Run316ToolTipHelpAndInsertBoxHelpTests
{
    private const string Dialogs = "MarkSmith.Desktop/Views/InsertDialogControls.cs";

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

    [Fact]
    public void Tooltip_is_copied_into_empty_help_text()
    {
        Assert.Equal("Search versions (Ctrl+F)", ToolTipHelpRule.HelpFor("Search versions (Ctrl+F)", "Search", null, null));
        Assert.Equal("Next change (F7)", ToolTipHelpRule.HelpFor("Next change (F7)", "", "", null));
    }

    [Fact]
    public void Help_text_set_by_code_is_kept()
    {
        Assert.Null(ToolTipHelpRule.HelpFor("Bold (Ctrl+B)", "Bold", "4 shapes. One item per line", null));
        // Code replaced the copy: leave it alone from then on.
        Assert.Null(ToolTipHelpRule.HelpFor("Bold (Ctrl+B)", "Bold", "Custom", "Bold (Ctrl+B)"));
    }

    [Fact]
    public void Tooltip_that_repeats_the_name_adds_nothing()
    {
        Assert.Null(ToolTipHelpRule.HelpFor("Bold", "bold ", null, null));
        Assert.Null(ToolTipHelpRule.HelpFor("   ", "Bold", null, null));
    }

    [Fact]
    public void Changed_or_removed_tooltip_follows_its_copy()
    {
        Assert.Equal("Redo (Ctrl+Y)", ToolTipHelpRule.HelpFor("Redo (Ctrl+Y)", "Redo", "Undo (Ctrl+Z)", "Undo (Ctrl+Z)"));
        Assert.Equal("", ToolTipHelpRule.HelpFor(null, "Redo", "Undo (Ctrl+Z)", "Undo (Ctrl+Z)"));
        Assert.Null(ToolTipHelpRule.HelpFor("Undo (Ctrl+Z)", "Undo", "Undo (Ctrl+Z)", "Undo (Ctrl+Z)"));
    }

    [Fact]
    public void HoverPolish_walk_syncs_help_after_naming()
    {
        var code = Read("MarkSmith.Desktop/Services/HoverPolish.cs");
        var apply = code.IndexOf("public static void Apply(DependencyObject root)", StringComparison.Ordinal);
        var naming = code.IndexOf("AutomationProperties.SetName(control, tip);", apply, StringComparison.Ordinal);
        var sync = code.IndexOf("ToolTipHelp.Sync(tipped);", apply, StringComparison.Ordinal);
        var recurse = code.IndexOf("Apply(VisualTreeHelper.GetChild(root, i));", apply, StringComparison.Ordinal);
        Assert.True(naming > apply && sync > naming && recurse > sync);
    }

    [Fact]
    public void ToolTipHelp_follows_later_tooltip_changes()
    {
        var code = Read("MarkSmith.Desktop/Services/ToolTipHelp.cs");
        Assert.Contains("RegisterPropertyChangedCallback(ToolTipService.ToolTipProperty", code);
        Assert.Contains("ToolTipHelpRule.HelpFor(", code);
    }

    [Fact]
    public void Box_help_leads_with_count_and_names_ctrl_enter()
    {
        Assert.Equal("3 items. One item per line. Ctrl+Enter inserts.", InsertCountText.BoxHelp("3 items", "item"));
        Assert.Equal("Header + 2 rows. Header line first, then one row per line, values separated by commas. Ctrl+Enter inserts.",
            InsertCountText.BoxHelp("Header + 2 rows", "row", headerFirst: true));
        Assert.Equal("1 milestone. Write each milestone as when: what, e.g. 2026: Launch. Ctrl+Enter inserts.",
            InsertCountText.BoxHelp("1 milestone", "milestone", "write each milestone as when: what, e.g. 2026: Launch."));
    }

    [Fact]
    public void Line_and_chart_boxes_use_the_longer_help()
    {
        var code = Read(Dialogs);
        Assert.Contains("AutomationProperties.SetHelpText(_box, Services.InsertCountText.BoxHelp(_count.Text, _noun, _lineHint, _headerFirst));", code);
        Assert.Contains("AutomationProperties.SetHelpText(_box, $\"{_count.Text}. {DataHelp}\");", code);
        Assert.Contains("Ctrl+Enter inserts.\";", code);
    }

    [Fact]
    public void Copy_snippet_button_names_its_keys()
    {
        var code = Read(Dialogs);
        Assert.Contains("ToolTipService.SetToolTip(_copySnippetButton, CopySnippetHelp);", code);
        Assert.Contains("AutomationProperties.SetHelpText(_copySnippetButton, CopySnippetHelp);", code);
    }

    [Fact]
    public void Browse_content_is_raw_and_provider_tooltip_explains_the_guess()
    {
        Assert.Contains("AutomationProperties.SetAccessibilityView((UIElement)browse.Content, AccessibilityView.Raw);",
            Read("MarkSmith.Desktop/Views/ImageInsertControl.cs"));
        Assert.Contains("Picked from the link you paste", Read(Dialogs));
    }
}
