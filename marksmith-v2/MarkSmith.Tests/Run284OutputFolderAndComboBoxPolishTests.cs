using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

// Run #284:
// - OutputFolderTextBox horizontal end-scroll when unfocused reveals the leaf destination directory name
//   (e.g. "...MarkSmith\Exports") instead of cutting it off at "C:\Users\Username...";
// - OutputFolderText.LeafFolder and DisplaySummary services for clean leaf path extraction;
// - InsertDialogBody problem reporting uses polite LiveAnnounce.RaiseIfChanged instead of ad-hoc peer event;
// - MainViewModel.RefreshCloudProviders preserves CloudProviderId during collection clear/refill under TwoWay binding;
// - MainViewModel.LoadPresets preserves ActivePreset during collection clear/refill under TwoWay binding.
[Collection("MainViewModel settings")]
public class Run284OutputFolderAndComboBoxPolishTests
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
    public void OutputFolderText_LeafFolder_ExtractsDirectoryName()
    {
        Assert.Equal("Exports", OutputFolderText.LeafFolder(@"C:\Users\Tony\Documents\Exports"));
        Assert.Equal("Exports", OutputFolderText.LeafFolder(@"C:\Users\Tony\Documents\Exports\"));
        Assert.Equal("out", OutputFolderText.LeafFolder("/home/user/workspace/out"));
        Assert.Equal("out", OutputFolderText.LeafFolder("/home/user/workspace/out/"));
        Assert.Equal("docs", OutputFolderText.LeafFolder("docs"));
        Assert.Equal(string.Empty, OutputFolderText.LeafFolder(null));
        Assert.Equal(string.Empty, OutputFolderText.LeafFolder("   "));
        Assert.Equal(string.Empty, OutputFolderText.LeafFolder(@"C:\"));
        Assert.Equal(string.Empty, OutputFolderText.LeafFolder("/"));
    }

    [Fact]
    public void OutputFolderText_DisplaySummary_ReturnsLeafOrFallback()
    {
        Assert.Equal("next to input file", OutputFolderText.DisplaySummary(null));
        Assert.Equal("next to input file", OutputFolderText.DisplaySummary("   "));
        Assert.Equal("Exports", OutputFolderText.DisplaySummary(@"C:\Users\Tony\Documents\Exports"));
    }

    [Fact]
    public void Presets_LoadPresets_PreservesActivePreset_WhenClearedUnderBinding()
    {
        var vm = new MainViewModel();
        vm.LoadPresets();

        // Create a test preset if none exist
        if (vm.Presets.Count == 0)
        {
            vm.SavePreset("TestPresetRun284");
        }

        var targetPreset = vm.Presets.First();
        vm.ActivePreset = targetPreset;

        // Simulate WinUI 3 ComboBox writing null back on ItemsSource Reset
        vm.Presets.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) vm.ActivePreset = null!;
        };

        vm.LoadPresets();

        Assert.NotNull(vm.ActivePreset);
        Assert.Equal(targetPreset.Name, vm.ActivePreset.Name);
    }

    [Fact]
    public void CloudProviders_RefreshCloudProviders_PreservesSelection_WhenClearedUnderBinding()
    {
        var vm = new MainViewModel();
        var targetId = "custom-test-cloud-id";
        vm.CloudProviderId = targetId;

        // Simulate WinUI 3 ComboBox writing null back on ItemsSource Reset
        vm.CloudProviders.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) vm.CloudProviderId = null!;
        };

        vm.RefreshCloudProviders();

        Assert.Equal(targetId, vm.CloudProviderId);
    }

    [Fact]
    public void MainWindow_OutputFolder_ScrollToEnd_SourceContracts()
    {
        var src = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        Assert.Contains("ScrollOutputFolderToEnd", src);
        Assert.Contains("OutputFolderTextBox.Loaded += (_, _) => ScrollOutputFolderToEnd();", src);
        Assert.Contains("OutputFolderTextBox.SizeChanged += (_, _) => ScrollOutputFolderToEnd();", src);
        Assert.Contains("OutputFolderTextBox.LostFocus += (_, _) => ScrollOutputFolderToEnd();", src);
        Assert.Contains("DispatcherQueue?.TryEnqueue(ScrollOutputFolderToEnd);", src);
        Assert.Contains("FindDescendant<ScrollViewer>(OutputFolderTextBox)", src);
    }

    [Fact]
    public void InsertDialogBody_LiveAnnounce_SourceContracts()
    {
        var src = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("LiveAnnounce.RaiseIfChanged(_problem, problem);", src);
        Assert.DoesNotContain("_lastAnnouncedProblem", src);
        Assert.DoesNotContain("RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)", src);
    }
}
