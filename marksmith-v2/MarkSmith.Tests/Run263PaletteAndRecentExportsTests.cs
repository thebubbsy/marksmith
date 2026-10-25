using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MarkSmith.Models;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.History;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Run #263:
// - Recent exports: rows whose file was moved or deleted say so, their Open / Show in File Explorer
//   items are disabled, and both menus offer "Remove moved or deleted files (N)";
// - Recent exports: "Copy all output paths" reports the number it actually copied, "Copy export count"
//   copies the number (not "(3)"), "Open export output folder" reports a missing folder (one shared helper
//   instead of three silent copies);
// - Diagram Studio palette: search matches a shape's starting label and type, the header shows
//   "3 of 21 shapes", pills have tooltips, and the search menu drops items that do nothing;
// - Version History: copy notices keep the first line of multi-line reports.
public class Run263PaletteAndRecentExportsTests
{
    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    private const string MainWindowCs = "MainWindow.xaml.cs";
    private const string MainWindowXaml = "MainWindow.xaml";
    private const string PaletteCs = "Views/Mermaid/NodePaletteControl.xaml.cs";
    private const string PaletteXaml = "Views/Mermaid/NodePaletteControl.xaml";
    private const string HistoryWindowCs = "Views/History/HistoryWindow.xaml.cs";

    // ---- Recent exports: missing files ----

    [Fact]
    public void HistoryEntry_PathLine_SaysWhenTheFileIsGone()
    {
        var entry = new HistoryEntry { OutputPath = @"C:\out\report.pdf", DocumentTitle = "Report", Kind = "PDF" };
        Assert.Equal(@"C:\out\report.pdf", entry.PathLine);
        Assert.DoesNotContain("moved or deleted", entry.ToString());

        entry.OutputMissing = true;
        Assert.Equal(@"Moved or deleted · C:\out\report.pdf", entry.PathLine);
        Assert.EndsWith(", moved or deleted", entry.ToString());
    }

    [Fact]
    public void HistoryEntry_MissingState_IsNeverSaved()
    {
        var json = JsonSerializer.Serialize(new HistoryEntry { OutputPath = "a.pdf", OutputMissing = true });
        Assert.DoesNotContain("OutputMissing", json);
        Assert.DoesNotContain("PathLine", json);
        Assert.False(JsonSerializer.Deserialize<HistoryEntry>(json)!.OutputMissing);
    }

    [Fact]
    public void RefreshExportFileStates_MarksMissingRows_AndReportsChanges()
    {
        var vm = new MainViewModel();
        vm.History.Clear();
        var kept = new HistoryEntry { OutputPath = @"C:\run263\kept.pdf" };
        var gone = new HistoryEntry { OutputPath = @"C:\run263\gone.pdf" };
        var blank = new HistoryEntry { OutputPath = "" };
        vm.History.Add(kept);
        vm.History.Add(gone);
        vm.History.Add(blank);

        Func<string, bool> exists = p => p.EndsWith("kept.pdf", StringComparison.Ordinal);
        Assert.True(vm.RefreshExportFileStates(exists));
        Assert.False(kept.OutputMissing);
        Assert.True(gone.OutputMissing);
        Assert.False(blank.OutputMissing); // no path is not "moved or deleted"
        Assert.Equal(1, vm.MissingExportCount);

        // Nothing changed: no redraw needed.
        Assert.False(vm.RefreshExportFileStates(exists));

        // The file came back.
        Assert.True(vm.RefreshExportFileStates(_ => true));
        Assert.Equal(0, vm.MissingExportCount);
    }

    [Fact]
    public void RemoveMissingHistoryEntries_RemovesOnlyMissingRows()
    {
        var vm = new MainViewModel();
        vm.History.Clear();
        var kept = new HistoryEntry { OutputPath = @"C:\run263\kept-" + Guid.NewGuid() + ".pdf" };
        var gone1 = new HistoryEntry { OutputPath = @"C:\run263\gone1-" + Guid.NewGuid() + ".pdf", OutputMissing = true };
        var gone2 = new HistoryEntry { OutputPath = @"C:\run263\gone2-" + Guid.NewGuid() + ".pdf", OutputMissing = true };
        vm.History.Add(kept);
        vm.History.Add(gone1);
        vm.History.Add(gone2);

        Assert.Equal(2, vm.RemoveMissingHistoryEntries());
        Assert.Same(kept, Assert.Single(vm.History));
        Assert.Equal(0, vm.RemoveMissingHistoryEntries());
    }

    [Fact]
    public void RecentExports_Flyout_ShowsPathLine_AndRefreshesOnOpen()
    {
        var xaml = Read(MainWindowXaml);
        Assert.Contains("<TextBlock Text=\"{Binding PathLine}\"", xaml);
        Assert.DoesNotContain("<TextBlock Text=\"{Binding OutputPath}\" Opacity=\"0.4\"", xaml);

        var cs = Read(MainWindowCs);
        var opened = cs[cs.IndexOf("private void OnExportHistoryFlyoutOpened", StringComparison.Ordinal)..];
        opened = opened[..opened.IndexOf("private void UpdateExportHistoryFlyoutState", StringComparison.Ordinal)];
        Assert.Contains("MarkMissingExportFiles();", opened);
        // Disk checks run off the UI thread, rows exported meanwhile are left alone, focus comes back.
        var mark = opened[opened.IndexOf("private async void MarkMissingExportFiles()", StringComparison.Ordinal)..];
        Assert.Contains("await Task.Run(() => paths.Where(File.Exists)", mark);
        Assert.Contains("ViewModel.RefreshExportFileStates(p => !paths.Contains(p) || existing.Contains(p))", mark);
        Assert.Contains("HistoryList.ItemsSource = null;", mark);
        Assert.Contains("if (hadFocus && ViewModel.History.Count > 0) HistoryList.Focus(FocusState.Programmatic);", mark);
    }

    [Fact]
    public void RecentExports_RowMenu_DisablesOpenAndShowForMissingFiles()
    {
        var cs = Read(MainWindowCs);
        var menu = cs[cs.IndexOf("private void ShowHistoryEntryContextMenu", StringComparison.Ordinal)..];
        menu = menu[..menu.IndexOf("private void OnHistoryCountButtonClick", StringComparison.Ordinal)];
        Assert.Contains("var missing = entry.OutputMissing;", menu);
        Assert.Contains("\"Open exported file (moved or deleted)\"", menu);
        Assert.Equal(2, menu.Split("IsEnabled = !missing").Length - 1);
        Assert.Contains("AddRemoveMissingExportsItem(menu);", menu);
    }

    [Fact]
    public void RecentExports_RemoveMissingItem_OnlyShowsWhenSomethingIsMissing()
    {
        var cs = Read(MainWindowCs);
        var helper = cs[cs.IndexOf("private void AddRemoveMissingExportsItem", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("if (missing == 0) return;", helper);
        Assert.Contains("Remove moved or deleted files ({missing})", helper);
        Assert.Contains("ViewModel.RemoveMissingHistoryEntries()", helper);
        Assert.Contains("UpdateExportHistoryFlyoutState();", helper);
        // Count menu offers it too.
        var countMenu = cs[cs.IndexOf("private void OnHistoryCountTextRightTapped", StringComparison.Ordinal)..];
        countMenu = countMenu[..countMenu.IndexOf("private void AddRemoveMissingExportsItem", StringComparison.Ordinal)];
        Assert.Contains("AddRemoveMissingExportsItem(menu);", countMenu);
    }

    [Fact]
    public void RecentExports_CountName_MentionsMissingFiles()
    {
        var cs = Read(MainWindowCs);
        var state = cs[cs.IndexOf("private void UpdateExportHistoryFlyoutState", StringComparison.Ordinal)..];
        state = state[..state.IndexOf("private void OnClearExportHistoryClick", StringComparison.Ordinal)];
        Assert.Contains("ViewModel.MissingExportCount", state);
        Assert.Contains("moved or deleted", state);
    }

    [Fact]
    public void CopyAllExportPaths_ReportsTheDistinctCountItCopied()
    {
        var cs = Read(MainWindowCs);
        var copy = cs[cs.IndexOf("private void CopyAllExportPaths", StringComparison.Ordinal)..];
        copy = copy[..copy.IndexOf("private void OnHistoryCountTextRightTapped", StringComparison.Ordinal)];
        Assert.Contains(".Distinct(StringComparer.OrdinalIgnoreCase)", copy);
        Assert.Contains("\"Copied 1 export file path.\"", copy);
        Assert.Contains("$\"Copied {paths.Count} export file paths.\"", copy);
        Assert.DoesNotContain("path(s)", copy);
    }

    [Fact]
    public void CopyExportCount_CopiesTheNumber_NotTheBracketedLabel()
    {
        var cs = Read(MainWindowCs);
        Assert.Contains("$\"Copy export count ({count:N0})\"", cs);
        Assert.Contains("CopyTextToStatusBar(count.ToString(), $\"Copied export count: {count:N0}\")", cs);
        Assert.DoesNotContain("Copy export count ({HistoryCountText.Text})", cs);
    }

    [Fact]
    public void OpenExportOutputFolder_IsOneHelper_ThatReportsAMissingFolder()
    {
        var cs = Read(MainWindowCs);
        Assert.Equal(3, cs.Split("openFolderItem.Click += (_, _) => OpenExportOutputFolder();").Length - 1);
        Assert.Equal(1, cs.Split("AnnounceStatus($\"Opened folder: {folder}\")").Length - 1);
        var helper = cs[cs.IndexOf("private void OpenExportOutputFolder()", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf("private void ShowExportedFileInFolder", StringComparison.Ordinal)];
        Assert.Contains("The export folder isn't there any more", helper);
        Assert.Contains("Couldn't open the export folder", helper);
        Assert.DoesNotContain("catch { }", helper);
    }

    // ---- Diagram Studio palette ----

    [Theory]
    [InlineData("process", true)]    // Rectangle Box starts as "Process"
    [InlineData("PROCESS", true)]
    [InlineData("rhombus", true)]    // shape type
    [InlineData("rect", true)]       // display name
    [InlineData("flow", true)]       // category
    [InlineData("  ", true)]
    [InlineData(null, true)]
    [InlineData("actor", false)]
    public void PaletteItem_Matches_NameCategoryLabelAndType(string? query, bool expected)
    {
        var item = new MermaidPaletteItem { Category = "Flowchart", DisplayName = "Rectangle Box", ShapeType = "Rhombus", DefaultText = "Process" };
        Assert.Equal(expected, item.Matches(query));
    }

    [Fact]
    public void Palette_SearchForAStartingLabel_FindsTheShape()
    {
        var vm = new MermaidStudioViewModel();
        var hits = vm.PaletteItems.Where(i => i.Matches("terminal")).Select(i => i.DisplayName).ToList();
        Assert.Contains("Stadium (Pill)", hits);
    }

    [Theory]
    [InlineData(21, 21, "21 shapes")]
    [InlineData(3, 21, "3 of 21 shapes")]
    [InlineData(0, 21, "0 of 21 shapes")]
    [InlineData(1, 1, "1 shape")]
    [InlineData(0, 0, "")]
    public void Palette_CountLabel(int shown, int total, string expected) =>
        Assert.Equal(expected, MermaidPaletteItem.CountLabel(shown, total));

    [Fact]
    public void Palette_Filter_UsesMatches_ShowsCount_AndKeepsFocus()
    {
        var cs = Read(PaletteCs);
        var filter = cs[cs.IndexOf("private void FilterItems()", StringComparison.Ordinal)..];
        filter = filter[..filter.IndexOf("private void OnPaletteDragItemsStarting", StringComparison.Ordinal)];
        Assert.Contains("item.Matches(searchText)", filter);
        Assert.DoesNotContain("ToLowerInvariant", filter);
        Assert.Contains("PaletteCountText.Text = ViewModels.Mermaid.MermaidPaletteItem.CountLabel(", filter);
        // Focus leaves the list before it empties.
        Assert.True(filter.IndexOf("SearchTextBox.Focus", StringComparison.Ordinal)
                    < filter.IndexOf("PaletteListView.ItemsSource = filtered;", StringComparison.Ordinal));

        var xaml = Read(PaletteXaml);
        Assert.Contains("x:Name=\"PaletteCountText\"", xaml);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", xaml);
        Assert.Contains("AutomationProperties.HelpText=\"Matches shape names, categories and the label", xaml);
    }

    [Fact]
    public void Palette_Pills_HaveTooltips()
    {
        var cs = Read(PaletteCs);
        Assert.Contains("ToolTipService.SetToolTip(pill, $\"{what} · Right-click or Shift+F10 for options\");", cs);
        Assert.Contains("AutomationProperties.SetHelpText(pill, what);", cs);
    }

    [Fact]
    public void Palette_SearchMenu_DropsItemsThatDoNothing()
    {
        var cs = Read(PaletteCs);
        var menu = cs[cs.IndexOf("private void FillSearchTextBoxMenu", StringComparison.Ordinal)..];
        menu = menu[..menu.IndexOf("private void SelectPill", StringComparison.Ordinal)];
        var selectAll = menu.IndexOf("\"Select all search text\"", StringComparison.Ordinal);
        Assert.True(selectAll > 0);
        // "Select all" sits inside a has-text check.
        var guard = menu.LastIndexOf("if (!string.IsNullOrEmpty(SearchTextBox.Text))", selectAll, StringComparison.Ordinal);
        Assert.True(guard > menu.IndexOf("menu.Items.Add(showAllItem);", StringComparison.Ordinal));
        Assert.Contains("showAllItem.IsEnabled = !string.IsNullOrEmpty(SearchTextBox.Text) || _selectedCategory != \"All\";", menu);
    }

    // ---- Version History copy notices ----

    [Theory]
    [InlineData("Copied: one line", "Copied: one line")]
    [InlineData("Copied report: Summary\r\n- a\r\n- b", "Copied report: Summary …")]
    [InlineData("Copied: feat(doc): x\rbody", "Copied: feat(doc): x …")]
    [InlineData("Copied: header\r\n\r\n", "Copied: header")]
    [InlineData("\n\nCopied: late", "Copied: late")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void HistoryNoticeLine_KeepsTheFirstLine(string? message, string expected) =>
        Assert.Equal(expected, HistoryWindowViewModel.NoticeLine(message));

    [Fact]
    public void HistoryWindow_CopyNotices_UseNoticeLine()
    {
        var cs = Read(HistoryWindowCs);
        Assert.Equal(2, cs.Split("_vm.ShowNotice(HistoryWindowViewModel.NoticeLine(message));").Length - 1);
        Assert.DoesNotContain("if (error is null) _vm.ShowNotice(message);", cs);
    }
}
