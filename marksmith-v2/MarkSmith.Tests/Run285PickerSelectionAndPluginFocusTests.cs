using System;
using System.IO;
using System.Linq;
using MarkSmith.Ocr;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #285:
// - Step 1's Markdown file picker keeps the open document selected after Rescan / pin / unpin
//   (the list rebuild left it blank) and follows a file opened some other way;
// - its right-click menu names the file;
// - Settings › Plugins: Install stays focused while it downloads, and Install / Remove hand focus
//   to each other before hiding themselves;
// - OCR studio: "Reclassify all 3 text blocks as" (was "Mosaic / Shapes", "Key-Values"), and the
//   status names the kind it changed from (it read the element after reclassifying it).
public class Run285PickerSelectionAndPluginFocusTests
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

    private static MarkdownFileEntry Entry(string path) => new(path, Path.GetFileName(path), "Documents", false);

    [Fact]
    public void IndexOfOpenFile_FindsTheOpenDocumentsRow()
    {
        var dir = Path.GetTempPath();
        var entries = new[] { Entry(Path.Combine(dir, "a.md")), Entry(Path.Combine(dir, "b.md")) };
        Assert.Equal(1, MarkdownPickerText.IndexOfOpenFile(entries, Path.Combine(dir, "b.md")));
        Assert.Equal(0, MarkdownPickerText.IndexOfOpenFile(entries, Path.Combine(dir, "sub", "..", "a.md")));
    }

    [Fact]
    public void IndexOfOpenFile_IgnoresCaseOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var entries = new[] { Entry(@"C:\Docs\Notes.md") };
        Assert.Equal(0, MarkdownPickerText.IndexOfOpenFile(entries, @"c:\docs\notes.MD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IndexOfOpenFile_NoOpenFile_IsMinusOne(string? open)
    {
        var entries = new[] { Entry(Path.Combine(Path.GetTempPath(), "a.md")) };
        Assert.Equal(-1, MarkdownPickerText.IndexOfOpenFile(entries, open));
    }

    [Fact]
    public void IndexOfOpenFile_UnlistedFile_IsMinusOne()
    {
        var entries = new[] { Entry(Path.Combine(Path.GetTempPath(), "a.md")) };
        Assert.Equal(-1, MarkdownPickerText.IndexOfOpenFile(entries, Path.Combine(Path.GetTempPath(), "z.md")));
        Assert.Equal(-1, MarkdownPickerText.IndexOfOpenFile(Array.Empty<MarkdownFileEntry>(), Path.Combine(Path.GetTempPath(), "a.md")));
    }

    [Fact]
    public void PickerMenu_NamesTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "notes.md");
        Assert.Equal("Show notes.md in File Explorer", MarkdownPickerText.ShowInExplorer(path));
        Assert.Equal("Copy path of notes.md", MarkdownPickerText.CopyPath(path));
        Assert.Equal("Show selected in File Explorer", MarkdownPickerText.ShowInExplorer(null));
        Assert.Equal("Copy selected file path", MarkdownPickerText.CopyPath("  "));
    }

    [Fact]
    public void MainViewModel_RaisesMarkdownFilesRefreshed_AfterTheRefill()
    {
        var src = Read("MarkSmith.Core/ViewModels/MainViewModel.cs");
        Assert.Contains("public event EventHandler? MarkdownFilesRefreshed;", src);
        var clear = src.IndexOf("MarkdownFiles.Clear();", StringComparison.Ordinal);
        var raise = src.IndexOf("MarkdownFilesRefreshed?.Invoke(this, EventArgs.Empty);", StringComparison.Ordinal);
        Assert.True(clear > 0 && raise > clear, "the event is raised after the list is refilled");
    }

    [Fact]
    public void MainWindow_ReselectsTheOpenFile_WithoutReopeningIt()
    {
        var src = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        Assert.Contains("ViewModel.MarkdownFilesRefreshed += (_, _) => DispatcherQueue.TryEnqueue(SelectOpenFileInPicker);", src);
        Assert.Contains("if (e.PropertyName == nameof(ViewModel.InputFilePath)) DispatcherQueue.TryEnqueue(SelectOpenFileInPicker);", src);
        Assert.Contains("Services.MarkdownPickerText.IndexOfOpenFile(ViewModel.MarkdownFiles, ViewModel.InputFilePath)", src);
        // The programmatic selection must not open the file again.
        var handler = src.IndexOf("private async void OnMarkdownFileSelected", StringComparison.Ordinal);
        Assert.True(handler > 0);
        Assert.Contains("if (_selectingOpenFile) return;", src.Substring(handler, 200));
        // Never while the list is open under the user.
        Assert.Contains("MarkdownFilesCombo.IsDropDownOpen) return;", src);
    }

    [Fact]
    public void MainWindow_PickerMenu_UsesFileNames()
    {
        var src = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        Assert.Contains("Text = Services.MarkdownPickerText.ShowInExplorer(selectedPath),", src);
        Assert.Contains("Text = Services.MarkdownPickerText.CopyPath(selectedPath),", src);
        Assert.DoesNotContain("\"Show selected in Explorer\"", src);
    }

    [Fact]
    public void SettingsPlugins_InstallStaysFocusable_AndHandsOverFocus()
    {
        var src = Read("MarkSmith.Desktop/Views/SettingsView.xaml.cs");
        Assert.DoesNotContain("installButton.IsEnabled = false;", src);
        Assert.Contains("if (installing) return;", src);
        Assert.Contains("installButton.Content = \"Installing…\";", src);
        Assert.Contains("HandOver(installButton, removeButton);", src);
        Assert.Contains("HandOver(removeButton, installButton);", src);
        // The partner is shown and focused before Refresh collapses the pressed button.
        var body = src.Substring(src.IndexOf("void HandOver(Button from, Button to)", StringComparison.Ordinal));
        Assert.True(body.IndexOf("to.Focus(how);", StringComparison.Ordinal) < body.IndexOf("Refresh();", StringComparison.Ordinal));
        Assert.Contains("FocusRescue.StillOn(from)", body);
        // No unconditional focus grabs after a long download.
        Assert.DoesNotContain("removeButton.Focus(FocusState.Programmatic);", src);
        Assert.DoesNotContain("installButton.Focus(FocusState.Programmatic);", src);
    }

    [Theory]
    [InlineData(OcrBlockKind.Paragraph, "text blocks")]
    [InlineData(OcrBlockKind.ShapeMosaic, "mosaics / shapes")]
    [InlineData(OcrBlockKind.KeyValue, "key-value pairs")]
    [InlineData(OcrBlockKind.Heading, "headings")]
    [InlineData(OcrBlockKind.Picture, "pictures")]
    public void OcrKindPlural_ReadsNaturally(OcrBlockKind kind, string expected)
    {
        Assert.Equal(expected, OcrInspectionElement.KindPlural(kind));
    }

    [Fact]
    public void OcrKindPlural_CoversEveryKind()
    {
        foreach (var kind in Enum.GetValues<OcrBlockKind>())
        {
            var plural = OcrInspectionElement.KindPlural(kind);
            Assert.False(string.IsNullOrWhiteSpace(plural));
            Assert.Equal(plural.ToLowerInvariant(), plural);
        }
    }

    [Fact]
    public void OcrBatchReclassify_SourceContracts()
    {
        var src = Read("MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        Assert.DoesNotContain("{elem.KindDisplayName}s as", src);
        Assert.Contains("Reclassify all {siblingsOfSameKind.Count} {OcrInspectionElement.KindPlural(elem.Kind)} as", src);
        // The source kind is captured before the loop reclassifies elem itself.
        Assert.Contains("var fromPlural = OcrInspectionElement.KindPlural(elem.Kind);", src);
        Assert.Contains("SetFooterStatusText($\"Reclassified {siblingsOfSameKind.Count} {fromPlural} as {OcrInspectionElement.KindPlural(targetKind)}.\");", src);
        Assert.DoesNotContain("{elem.KindDisplayName} elements to", src);
    }
}
