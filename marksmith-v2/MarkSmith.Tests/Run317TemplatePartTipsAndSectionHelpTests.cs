using System;
using System.IO;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #317:
// - the Style & Export section headers (and Automation) say what each section holds, as a tooltip
//   and, through ToolTipHelp, as help text;
// - the Theme box and "Choose output folder" have tooltips that say more than their names;
// - every InfoBar's close button says what pressing it does, and every dialog's Close/Cancel
//   button names Esc;
// - every Insert dialog's Insert button names Ctrl+Enter.
public class Run317TemplatePartTipsAndSectionHelpTests
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

    [Fact]
    public void Expander_header_borrows_the_expanders_help_text()
    {
        Assert.Equal("Cover page, company logo, document font and author",
            TemplatePartTipRule.For("ExpanderHeader", "Expander", "Cover page, company logo, document font and author", "Branding"));
        Assert.Null(TemplatePartTipRule.For("ExpanderHeader", "Expander", "  ", null));
        Assert.Null(TemplatePartTipRule.For("ExpanderHeader", "Expander", null, null));
    }

    [Fact]
    public void InfoBar_close_says_what_it_does()
    {
        Assert.Equal("Dismiss this message", TemplatePartTipRule.For("CloseButton", "InfoBar", null, null));
    }

    [Fact]
    public void Dialog_close_names_esc()
    {
        Assert.Equal("Close (Esc)", TemplatePartTipRule.For("CloseButton", "ContentDialog", null, "Close"));
        Assert.Equal("Cancel (Esc)", TemplatePartTipRule.For("CloseButton", "ContentDialog", null, " Cancel "));
        // No close text: the button is hidden, so it gets nothing.
        Assert.Null(TemplatePartTipRule.For("CloseButton", "ContentDialog", null, ""));
    }

    [Fact]
    public void Other_parts_and_owners_get_nothing()
    {
        Assert.Null(TemplatePartTipRule.For("PrimaryButton", "ContentDialog", null, "Insert"));
        Assert.Null(TemplatePartTipRule.For("CloseButton", null, null, "Close"));
        Assert.Null(TemplatePartTipRule.For("ExpanderHeader", "InfoBar", "Help", null));
    }

    [Fact]
    public void Only_a_missing_tip_or_one_repeating_the_name_is_replaced()
    {
        Assert.True(TemplatePartTipRule.CanReplace(false, null, "Close"));
        Assert.True(TemplatePartTipRule.CanReplace(true, "Close", "close "));
        Assert.False(TemplatePartTipRule.CanReplace(true, "Close the panel", "Close"));
        // A tooltip that isn't text (a custom ToolTip) is kept.
        Assert.False(TemplatePartTipRule.CanReplace(true, null, "Close"));
    }

    [Fact]
    public void HoverPolish_sets_the_part_tip_before_copying_tooltips_to_help_text()
    {
        var src = Read("MarkSmith.Desktop/Services/HoverPolish.cs");
        var apply = src.IndexOf("public static void Apply(DependencyObject root)", StringComparison.Ordinal);
        var tip = src.IndexOf("GiveTemplatePartTip(button);", apply, StringComparison.Ordinal);
        var sync = src.IndexOf("ToolTipHelp.Sync(tipped);", apply, StringComparison.Ordinal);
        Assert.True(apply >= 0 && tip > apply && sync > tip);
        Assert.Contains("TemplatePartTipRule.For(button.Name, kind,", src);
        Assert.Contains("public static void SetDialogButtonTip(ContentDialog dialog, string partName, string tip)", src);
    }

    [Theory]
    [InlineData("AutomationExpander")]
    [InlineData("StyleAppearanceExpander")]
    [InlineData("StyleLayoutExpander")]
    [InlineData("StyleWordExpander")]
    [InlineData("StyleEmailExpander")]
    [InlineData("StyleDiagramsExpander")]
    [InlineData("StyleContentExpander")]
    [InlineData("StyleFormattingExpander")]
    [InlineData("ExportBrandingExpander")]
    public void Every_side_panel_section_says_what_it_holds(string name)
    {
        var xaml = Read("MarkSmith.Desktop/MainWindow.xaml");
        Assert.Matches(new Regex($"<Expander x:Name=\"{name}\" AutomationProperties.HelpText=\"[^\"]{{12,}}\""), xaml);
    }

    [Fact]
    public void Theme_box_and_output_folder_button_say_more_than_their_names()
    {
        var xaml = Read("MarkSmith.Desktop/MainWindow.xaml");
        Assert.Contains("ToolTipService.ToolTip=\"Colours and fonts of the preview and every export · Alt+Down lists the themes, type a letter to jump\"", xaml);
        Assert.Contains("ToolTipService.ToolTip=\"Choose the folder exports are saved to (empty: next to the input file)\"", xaml);
        Assert.DoesNotContain("ToolTipService.ToolTip=\"Choose output folder\"", xaml);
    }

    [Fact]
    public void Insert_dialogs_insert_button_names_ctrl_enter()
    {
        var src = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        var shell = src.IndexOf("private async Task<ContentDialogResult> ShowInsertDialogAsync(", StringComparison.Ordinal);
        var tip = src.IndexOf("SetDialogButtonTip(\n                        dialog, \"PrimaryButton\", $\"{dialog.PrimaryButtonText} (Ctrl+Enter)\")", shell, StringComparison.Ordinal);
        var show = src.IndexOf("HoverPolish.ShowPolishedAsync(dialog);", shell, StringComparison.Ordinal);
        // Registered before ShowPolishedAsync's Opened handler walks the dialog.
        Assert.True(shell >= 0 && tip > shell && show > tip);
    }
}
