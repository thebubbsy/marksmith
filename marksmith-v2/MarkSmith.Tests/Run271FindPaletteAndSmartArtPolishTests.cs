using System;
using System.IO;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Tests;

// Run #271: Find/Replace empty states & match announcements, Command Palette pinned and recent files
// parity, SmartArt Studio empty/unselected action feedback, and high-contrast status contrast.
public class Run271FindPaletteAndSmartArtPolishTests
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

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    // ---- SmartArt Design Studio View Model Polish ----

    [Fact]
    public void SmartArt_EmptyOutline_Duplicate_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.DuplicateSelected();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_UnselectedNode_Duplicate_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.RootNodes.Add(new StudioNodeViewModel { Text = "Node 1" });
        vm.Select(null);
        vm.DuplicateSelected();
        Assert.Equal("Nothing to duplicate: select an item in the outline first.", vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_EmptyOutline_Delete_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.DeleteSelected();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_UnselectedNode_Delete_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.RootNodes.Add(new StudioNodeViewModel { Text = "Node 1" });
        vm.Select(null);
        vm.DeleteSelected();
        Assert.Equal("Nothing to delete: select an item in the outline first.", vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_EmptyOutline_MoveUp_MoveDown_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.MoveUp();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);

        vm.MoveDown();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_UnselectedNode_MoveUp_MoveDown_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.RootNodes.Add(new StudioNodeViewModel { Text = "Node 1" });
        vm.Select(null);

        vm.MoveUp();
        Assert.Equal("Nothing to move: select an item in the outline first.", vm.StatusMessage);

        vm.MoveDown();
        Assert.Equal("Nothing to move: select an item in the outline first.", vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_EmptyOutline_Promote_Demote_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();

        vm.Promote();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);

        vm.Demote();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_UnselectedNode_Promote_Demote_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.RootNodes.Add(new StudioNodeViewModel { Text = "Node 1" });
        vm.Select(null);

        vm.Promote();
        Assert.Equal("Nothing to promote: select an item in the outline first.", vm.StatusMessage);

        vm.Demote();
        Assert.Equal("Nothing to demote: select an item in the outline first.", vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_EmptyOutline_BeginRename_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.BeginRename();
        Assert.Equal(SmartArtDesignStudioViewModel.EmptyOutlineMessage, vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_UnselectedNode_BeginRename_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.RootNodes.Clear();
        vm.RootNodes.Add(new StudioNodeViewModel { Text = "Node 1" });
        vm.Select(null);
        vm.BeginRename();
        Assert.Equal("Nothing to rename: select an item in the outline first.", vm.StatusMessage);
    }

    [Fact]
    public void SmartArt_UndoRedo_EmptyStack_ExplainsWhy()
    {
        var vm = new SmartArtDesignStudioViewModel();
        vm.Undo();
        Assert.Equal("Nothing to undo.", vm.StatusMessage);

        vm.Redo();
        Assert.Equal("Nothing to redo.", vm.StatusMessage);
    }

    // ---- ThemeBrush & StatusSeverityBrushes High Contrast ----

    [Fact]
    public void ThemeBrush_IsHighContrast_Property_SourceStructure()
    {
        var src = Read("Services/ThemeBrush.cs");
        Assert.Contains("public static bool IsHighContrast => SafeHighContrast();", src);
    }

    [Fact]
    public void StatusSeverityBrushes_HighContrast_SourceStructure()
    {
        var src = Read("Converters/StatusSeverityBrushes.cs");
        Assert.Contains("if (Services.ThemeBrush.IsHighContrast && severity == Models.StatusSeverity.Informational)", src);
        Assert.Contains("return \"TextFillColorPrimaryBrush\";", src);
        Assert.Contains("Models.StatusSeverity.Success => \"SystemFillColorSuccessBrush\",", src);
        Assert.Contains("Models.StatusSeverity.Warning => \"SystemFillColorCautionBrush\",", src);
        Assert.Contains("Models.StatusSeverity.Error => \"SystemFillColorCriticalBrush\",", src);
    }

    // ---- MainWindow Find/Replace & Palette Source Guards ----

    [Fact]
    public void MainWindow_Find_EmptyDocument_And_EmptyQuery_Announcements()
    {
        var src = Read("MainWindow.xaml.cs");
        Assert.Contains("AnnounceStatus(\"The document is empty: nothing to find.\");", src);
        Assert.Contains("AnnounceStatus(\"Type the text to find first, then press Enter.\");", src);
        Assert.Contains("AnnounceStatus($\"No matches found for \\\"{query}\\\".\");", src);
    }

    [Fact]
    public void MainWindow_Replace_EmptyDocument_Announcement()
    {
        var src = Read("MainWindow.xaml.cs");
        Assert.Contains("AnnounceStatus(\"The document is empty: nothing to replace.\");", src);
        Assert.Contains("ViewModel.StatusText = \"Type the text to find first, then Replace.\";", src);
    }

    [Fact]
    public void MainWindow_Palette_PinnedAndRecent_Files()
    {
        var src = Read("MainWindow.xaml.cs");
        Assert.Contains("var pinned = ViewModel.PinnedFiles ?? System.Array.Empty<string>();", src);
        Assert.Contains("var prefix = isPinned ? \"Open pinned: \" : \"Open recent: \";", src);
        Assert.Contains("var keywords = isPinned ? $\"recent pinned open {p}\" : $\"recent open {p}\";", src);
    }

    [Fact]
    public void MainViewModel_Exposes_PinnedFiles()
    {
        var vm = new MainViewModel();
        Assert.NotNull(vm.PinnedFiles);
    }
}
