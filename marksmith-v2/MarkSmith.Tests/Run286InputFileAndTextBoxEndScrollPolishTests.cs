using System;
using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #286:
// - InputFilePathText service for leaf file name extraction, directory parsing, display summaries, and rich tooltips;
// - StatusCopyPillTooltipText canonical tooltips and accessible descriptions covering all status bar copy actions;
// - TextBoxEndScroll service to keep single-line path and URL text boxes scrolled to the end when unfocused;
// - InputFileTextBox in MainWindow wired with TextBoxEndScroll and rich dynamic tooltips;
// - ImageInsertControl._source wired with TextBoxEndScroll to reveal the image file name / URL endpoint;
// - InsertDialogControls._url in Link & Media dialogs wired with TextBoxEndScroll;
// - SettingsView.WebDavEndpointBox and GoogleClientIdBox wired with TextBoxEndScroll;
// - StatusMoreCopyButton tooltip updated to explicitly include plain text and rendered HTML.
public class Run286InputFileAndTextBoxEndScrollPolishTests
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
    public void InputFilePathText_FileName_ExtractsLeafFileName()
    {
        Assert.Equal("report.md", InputFilePathText.FileName(@"C:\Users\Tony\Documents\report.md"));
        Assert.Equal("report.md", InputFilePathText.FileName(@"C:\Users\Tony\Documents\report.md\"));
        Assert.Equal("README.md", InputFilePathText.FileName("/home/user/workspace/README.md"));
        Assert.Equal("README.md", InputFilePathText.FileName("/home/user/workspace/README.md/"));
        Assert.Equal("notes.md", InputFilePathText.FileName("notes.md"));
        Assert.Equal(string.Empty, InputFilePathText.FileName(null));
        Assert.Equal(string.Empty, InputFilePathText.FileName("   "));
        Assert.Equal(string.Empty, InputFilePathText.FileName(""));
    }

    [Fact]
    public void InputFilePathText_Directory_ExtractsParentDirectory()
    {
        Assert.Equal(@"C:\Users\Tony\Documents", InputFilePathText.Directory(@"C:\Users\Tony\Documents\report.md"));
        Assert.Equal("/home/user/workspace", InputFilePathText.Directory("/home/user/workspace/README.md"));
        Assert.Equal(string.Empty, InputFilePathText.Directory("notes.md"));
        Assert.Equal(string.Empty, InputFilePathText.Directory(null));
        Assert.Equal(string.Empty, InputFilePathText.Directory("   "));
    }

    [Fact]
    public void InputFilePathText_ToolTip_FormatsInformativeGuidance()
    {
        var emptyTip = InputFilePathText.ToolTip(null);
        Assert.Contains("No file selected", emptyTip);
        Assert.Contains("open a Markdown file", emptyTip);

        var fileTip = InputFilePathText.ToolTip(@"C:\Docs\report.md");
        Assert.Contains(@"Selected file: C:\Docs\report.md", fileTip);
        Assert.Contains("File name: report.md", fileTip);
        Assert.Contains(@"Folder: C:\Docs", fileTip);
        Assert.Contains("Right-click or Shift+F10 for file actions", fileTip);
    }

    [Fact]
    public void InputFilePathText_DisplaySummary_ReturnsFileNameOrFallback()
    {
        Assert.Equal("Unsaved document", InputFilePathText.DisplaySummary(null));
        Assert.Equal("Unsaved document", InputFilePathText.DisplaySummary("   "));
        Assert.Equal("report.md", InputFilePathText.DisplaySummary(@"C:\Docs\report.md"));
        Assert.Equal("notes.md", InputFilePathText.DisplaySummary("notes.md"));
    }

    [Fact]
    public void StatusCopyPillTooltipText_ContainsAllCanonicalTooltips()
    {
        Assert.Contains("Markdown", StatusCopyPillTooltipText.MarkdownTooltip);
        Assert.Contains("plain text", StatusCopyPillTooltipText.TextTooltip);
        Assert.Contains("Table of Contents", StatusCopyPillTooltipText.TocTooltip);
        Assert.Contains("links", StatusCopyPillTooltipText.LinksTooltip);
        Assert.Contains("fenced code blocks", StatusCopyPillTooltipText.CodeTooltip);
        Assert.Contains("tables", StatusCopyPillTooltipText.TablesTooltip);
        Assert.Contains("task list", StatusCopyPillTooltipText.TasksTooltip);
        Assert.Contains("math", StatusCopyPillTooltipText.MathTooltip);
        Assert.Contains("callouts", StatusCopyPillTooltipText.CalloutsTooltip);
        Assert.Contains("image", StatusCopyPillTooltipText.ImagesTooltip);
        Assert.Contains("footnote", StatusCopyPillTooltipText.FootnotesTooltip);
        Assert.Contains("diagrams", StatusCopyPillTooltipText.DiagramsTooltip);
        Assert.Contains("frontmatter", StatusCopyPillTooltipText.FrontmatterTooltip);
        Assert.Contains("HTML", StatusCopyPillTooltipText.HtmlTooltip);
        Assert.Contains("HTML", StatusCopyPillTooltipText.MoreCopyTooltip);
        Assert.Contains("frontmatter", StatusCopyPillTooltipText.MoreCopyTooltip);
        Assert.Contains("text", StatusCopyPillTooltipText.MoreCopyTooltip);
    }

    [Fact]
    public void MainWindow_InputFile_SourceContracts()
    {
        var src = Read("MarkSmith.Desktop/MainWindow.xaml.cs");
        Assert.Contains("TextBoxEndScroll.Attach(InputFileTextBox);", src);
        Assert.Contains("TextBoxEndScroll.Attach(OutputFolderTextBox);", src);
        Assert.Contains("UpdateInputFileToolTip();", src);
        Assert.Contains("TextBoxEndScroll.ScrollToEndIfUnfocused(OutputFolderTextBox);", src);
        Assert.Contains("TextBoxEndScroll.ScrollToEndIfUnfocused(InputFileTextBox)", src);

        var xaml = Read("MarkSmith.Desktop/MainWindow.xaml");
        Assert.Contains("text, code, tables", xaml);
        Assert.Contains("frontmatter and HTML", xaml);
    }

    [Fact]
    public void DialogsAndSettings_TextBoxEndScroll_SourceContracts()
    {
        var img = Read("MarkSmith.Desktop/Views/ImageInsertControl.cs");
        Assert.Contains("TextBoxEndScroll.Attach(_source);", img);

        var insert = Read("MarkSmith.Desktop/Views/InsertDialogControls.cs");
        Assert.Contains("TextBoxEndScroll.Attach(_url);", insert);

        var settings = Read("MarkSmith.Desktop/Views/SettingsView.xaml.cs");
        Assert.Contains("TextBoxEndScroll.Attach(WebDavEndpointBox);", settings);
        Assert.Contains("TextBoxEndScroll.Attach(GoogleClientIdBox);", settings);

        var settingsXaml = Read("MarkSmith.Desktop/Views/SettingsView.xaml");
        Assert.Contains("x:Name=\"WebDavEndpointBox\"", settingsXaml);
    }
}
