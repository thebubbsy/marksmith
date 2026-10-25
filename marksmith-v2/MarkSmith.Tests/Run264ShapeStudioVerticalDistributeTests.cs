using System;
using System.IO;
using System.Linq;
using MarkSmith.Models;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.History;
using MarkSmith.ViewModels.Mermaid;
using MarkSmith.ViewModels.ShapeStudio;
using Xunit;

namespace MarkSmith.Tests;

public class Run264ShapeStudioVerticalDistributeTests
{
    [Fact]
    public void HistoryEntry_PathLine_ShowsMovedOrDeleted_WhenOutputMissingIsTrue()
    {
        var entry = new HistoryEntry
        {
            DocumentTitle = "Report",
            OutputPath = @"C:\Exports\report.docx",
            OutputMissing = false
        };

        Assert.Equal(@"C:\Exports\report.docx", entry.PathLine);
        Assert.DoesNotContain("moved or deleted", entry.ToString());

        entry.OutputMissing = true;
        Assert.Equal(@"Moved or deleted · C:\Exports\report.docx", entry.PathLine);
        Assert.Contains("moved or deleted", entry.ToString());
    }

    [Fact]
    public void MainViewModel_RefreshExportFileStates_MarksMissingFiles_Accurately()
    {
        var vm = new MainViewModel();
        vm.History.Clear();

        var entry1 = new HistoryEntry { DocumentTitle = "Doc 1", OutputPath = @"C:\valid.pdf" };
        var entry2 = new HistoryEntry { DocumentTitle = "Doc 2", OutputPath = @"C:\missing.pdf" };
        vm.History.Add(entry1);
        vm.History.Add(entry2);

        bool MockFileExists(string path) => path == @"C:\valid.pdf";

        var changed = vm.RefreshExportFileStates(MockFileExists);
        Assert.True(changed);
        Assert.False(entry1.OutputMissing);
        Assert.True(entry2.OutputMissing);
        Assert.Equal(1, vm.MissingExportCount);

        // Calling again with the same file state returns false (no redraw needed)
        var secondRunChanged = vm.RefreshExportFileStates(MockFileExists);
        Assert.False(secondRunChanged);
    }

    [Fact]
    public void MainViewModel_RemoveMissingHistoryEntries_RemovesOnlyMissingRows()
    {
        var vm = new MainViewModel();
        vm.History.Clear();

        var keep = new HistoryEntry { DocumentTitle = "Keep", OutputPath = @"C:\keep.pdf", OutputMissing = false };
        var remove1 = new HistoryEntry { DocumentTitle = "Gone 1", OutputPath = @"C:\gone1.pdf", OutputMissing = true };
        var remove2 = new HistoryEntry { DocumentTitle = "Gone 2", OutputPath = @"C:\gone2.pdf", OutputMissing = true };
        vm.History.Add(keep);
        vm.History.Add(remove1);
        vm.History.Add(remove2);

        var removed = vm.RemoveMissingHistoryEntries();
        Assert.Equal(2, removed);
        Assert.Single(vm.History);
        Assert.Equal("Keep", vm.History[0].DocumentTitle);
        Assert.Equal(0, vm.MissingExportCount);
    }

    [Fact]
    public void MermaidPaletteItem_Matches_FindsShapeByStartingLabelAndShapeType()
    {
        var item = new MermaidPaletteItem
        {
            DisplayName = "Decision Node",
            Category = "Flowchart",
            DefaultText = "Is valid?",
            ShapeType = "Rhombus"
        };

        Assert.True(item.Matches("decision"));
        Assert.True(item.Matches("flowchart"));
        Assert.True(item.Matches("valid"));
        Assert.True(item.Matches("rhombus"));
        Assert.False(item.Matches("database"));
        Assert.True(item.Matches("   "));
        Assert.True(item.Matches(null));
    }

    [Fact]
    public void MermaidPaletteItem_CountLabel_FormatsFilteredAndUnfilteredCorrectly()
    {
        Assert.Equal("21 shapes", MermaidPaletteItem.CountLabel(21, 21));
        Assert.Equal("3 of 21 shapes", MermaidPaletteItem.CountLabel(3, 21));
        Assert.Equal("1 shape", MermaidPaletteItem.CountLabel(1, 1));
        Assert.Equal("", MermaidPaletteItem.CountLabel(0, 0));
    }

    [Fact]
    public void HistoryWindowViewModel_NoticeLine_TruncatesMultiLineAndNormalizesBreaks()
    {
        Assert.Equal("", HistoryWindowViewModel.NoticeLine(null));
        Assert.Equal("", HistoryWindowViewModel.NoticeLine("   \r\n   "));
        Assert.Equal("Single line notice", HistoryWindowViewModel.NoticeLine("Single line notice"));

        var multiline = "First line of notice\r\nSecond line\rThird line\nFourth line";
        Assert.Equal("First line of notice …", HistoryWindowViewModel.NoticeLine(multiline));

        var multilineWithInitialBlank = "\r\n  Actual first line  \r\nSecond line";
        Assert.Equal("Actual first line …", HistoryWindowViewModel.NoticeLine(multilineWithInitialBlank));
    }

    [Fact]
    public void ShapeDesignStudio_DistributeSpacingUniformly_Vertical_CalculatesCorrectGap()
    {
        var vm = new ShapeDesignStudioViewModel();
        vm.ClearAll();
        vm.AddShapeAt("rect", 0, 0, 50, 40, "0078D4", "Top");
        vm.AddShapeAt("rect", 0, 60, 50, 40, "0078D4", "Middle");
        vm.AddShapeAt("rect", 0, 160, 50, 40, "0078D4", "Bottom");

        var (total, modified, gap, summary) = vm.DistributeSpacingUniformly(DistributionAxis.Vertical);
        Assert.Equal(3, total);
        Assert.Equal(1, modified);
        Assert.Equal(40.0, gap);
        Assert.Contains("Equal gaps in whole pixels (vertical)", summary);
        Assert.Contains("Ctrl+Z to undo", summary);

        // Middle shape was at Y=60; with first at 0 (height 40) + 40 gap, it moves to 80
        var ordered = vm.Shapes.OrderBy(s => s.Y).ToList();
        Assert.Equal(80.0, ordered[1].Y);
    }

    [Fact]
    public void ShapeDesignStudioWindow_Xaml_ContainsVerticalSpacingMenuItem()
    {
        var xamlPath = Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\MarkSmith.Desktop\Views\ShapeStudio\ShapeDesignStudioWindow.xaml");
        if (File.Exists(xamlPath))
        {
            var content = File.ReadAllText(xamlPath);
            Assert.Contains("SelectionDistributeSpacingVerticalItem", content);
            Assert.Contains("Equal gaps in whole pixels (vertical)", content);
            Assert.Contains("OnSelectionDistributeSpacingVerticalClick", content);
        }
    }

    [Fact]
    public void ThemeName_LineBreakNormalization_StripsCarriageReturnsAndNewlines()
    {
        string rawInput = "  Custom \r\n Dark \r Ocean \n Theme  ";
        string normalized = rawInput.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        Assert.Equal("Custom   Dark   Ocean   Theme", normalized);
        Assert.DoesNotContain("\r", normalized);
        Assert.DoesNotContain("\n", normalized);
    }
}
