using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #243: the Shape, SmartArt and Mind Map studios discarded the clipboard result of ~340 copies and
// then said "Copied …" anyway; a busy clipboard now says so on the status line, and their Copy buttons
// only tick after a real copy. The last silent copies elsewhere (Settings rows and menus, the Image and
// SmartArt insert dialogs, the extension guide, the export button's menu, Suite Hub) now confirm.
public class Run243HonestStudioCopiesTests
{
    private static readonly string[] Studios =
    {
        Path.Combine("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"),
        Path.Combine("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"),
        Path.Combine("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"),
    };

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

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative));

    private static string Method(string code, string name)
    {
        var decl = Regex.Match(code, $@"^\s*(private|protected|public|internal)\b[^\n;=]*\s{Regex.Escape(name)}\(", RegexOptions.Multiline);
        Assert.True(decl.Success, $"{name} not found");
        var open = code.IndexOf('{', decl.Index);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        throw new InvalidOperationException($"{name} is unbalanced");
    }

    private const string Busy = "Another app is holding the clipboard. Try again in a moment.";

    [Theory]
    [InlineData("Copied angle: 45°", "Couldn't copy angle: 45°. " + Busy)]
    [InlineData("Copied status to clipboard", "Couldn't copy status. " + Busy)]
    [InlineData("Copied #FF0000 to the clipboard.", "Couldn't copy #FF0000. " + Busy)]
    [InlineData("✓ Copied top-level outline roots to clipboard.", "Couldn't copy top-level outline roots. " + Busy)]
    [InlineData("Copied: Curved · Normal", "Couldn't copy Curved · Normal. " + Busy)]
    [InlineData("Copied", "Nothing was copied. " + Busy)]
    public void Failed_TurnsACopyConfirmationIntoTheReason(string message, string expected)
    {
        Assert.Equal(expected, CopyStatusText.Failed(message, Busy));
    }

    [Fact]
    public void Failed_KeepsAResultAndSaysItWasNotCopied()
    {
        // Audits put their summary on the status line and the clipboard; the summary must survive.
        Assert.Equal("3 shapes are off the 8 px grid. Not copied to the clipboard: " + Busy,
            CopyStatusText.Failed("✓ 3 shapes are off the 8 px grid.", Busy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Failed_WithNothingToKeep_SaysNothingWasCopied(string? message)
    {
        Assert.Equal("Nothing was copied. " + Busy, CopyStatusText.Failed(message, Busy));
    }

    [Fact]
    public void Failed_FillsInAMissingReason()
    {
        Assert.StartsWith("Couldn't copy angle. The clipboard is busy", CopyStatusText.Failed("Copied angle", " "));
    }

    [Fact]
    public void Studios_NoLongerDiscardTheClipboardResult()
    {
        foreach (var studio in Studios)
        {
            var code = Read(studio);
            Assert.DoesNotMatch(new Regex(@"_ = [\w\.]*ClipboardWriter\.TrySetTextAsync"), code);
            Assert.DoesNotMatch(new Regex(@"^\s*[\w\.]*ClipboardWriter\.TrySetTextAsync\(", RegexOptions.Multiline), code);
            Assert.DoesNotContain("ClipboardWriter.Set(", code);
            Assert.DoesNotContain("Clipboard.SetContent(", code);
        }
    }

    [Fact]
    public void Studios_CopyAndReport_AwaitsAndReportsTheRealResult()
    {
        foreach (var studio in Studios)
        {
            var code = Read(studio);
            var body = Method(code, "CopyAndReport");
            Assert.Contains("await MarkSmith.Services.ClipboardWriter.TrySetTextAsync(text ?? string.Empty)", body);
            Assert.Contains("CopyStatusText.Failed(message ?? ViewModel.StatusMessage, error)", body);
            Assert.Contains("ApplyButtonTick(tickButton, error", body);
            Assert.True(Regex.Matches(code, @"\bCopyAndReport\(").Count > 30, $"{studio} should route its copies through CopyAndReport");
        }
    }

    [Fact]
    public void SmartArtStudio_CopyButtonsTickFromTheResult()
    {
        var code = Read(Studios[1]);
        Assert.Contains("CopyAndReport(md, \"Copied outline bullets to clipboard.\", CopyOutlineMarkdownButton);", Method(code, "CopyOutlineMarkdown"));
        Assert.Contains("CopySmartArtBlockButton);", Method(code, "CopySmartArtBlock"));
        Assert.Contains("ExportSvgButton);", Method(code, "CopySvgToClipboard"));
        Assert.Contains("ExportSvgButton);", Method(code, "OnCopySvgDataUriClick"));
        Assert.DoesNotContain("ApplyButtonTick(ExportSvgButton, null)", code);
        Assert.DoesNotContain("ApplyButtonTick(CopyOutlineMarkdownButton, null)", code);
        Assert.DoesNotContain("ApplyButtonTick(CopySmartArtBlockButton, null)", code);
    }

    [Fact]
    public void MindMap_ConnectionStyleCopiesReportAndTickHonestly()
    {
        var code = Read(Studios[2]);
        Assert.Contains("$\"Copied connection style: {summary}\", ConnectionStyleDropDownButton", code);
        Assert.Contains("CopyAndReport(config, $\"Copied: {config}\", ConnectionStyleDropDownButton", code);
        Assert.DoesNotContain("non-critical clipboard failure", code);
    }

    [Fact]
    public void ShapeStudio_CategoryCopySaysWhatWasCopied()
    {
        var code = Read(Studios[0]);
        Assert.Contains("$\"Copied '{ViewModel.SelectedPresetCategory ?? \"All\"}' to clipboard\"", code);
    }

    [Theory]
    [InlineData("Controls/OptionRow.xaml.cs")]
    [InlineData("Views/ExtensionSetupView.xaml.cs")]
    [InlineData("Views/ImageInsertControl.cs")]
    [InlineData("Views/SmartArtInsertControl.cs")]
    [InlineData("Views/SettingsView.xaml.cs")]
    [InlineData("Views/SuiteHubView.xaml.cs")]
    [InlineData("MainWindow.xaml.cs")]
    public void SilentCopies_NowConfirm(string file)
    {
        var code = Read(file.Replace('/', Path.DirectorySeparatorChar));
        // A bare awaited copy throws its result away: the click said nothing either way.
        Assert.DoesNotMatch(new Regex(@"^\s*await [\w\.]*ClipboardWriter\.TrySetTextAsync\(", RegexOptions.Multiline), code);
        Assert.DoesNotMatch(new Regex(@"=> await [\w\.]*ClipboardWriter\.TrySetTextAsync\("), code);
    }

    [Fact]
    public void SilentCopies_UseTheBubbleWithTheRightAnchor()
    {
        Assert.Contains("CopyWithBubble(this, Header, \"Copied setting name\")", Read(Path.Combine("Controls", "OptionRow.xaml.cs")));
        Assert.Contains("CopyWithBubble(_alt, _alt.Text, \"Copied alt text\")", Read(Path.Combine("Views", "ImageInsertControl.cs")));
        Assert.Contains("CopyWithBubble(_box, _box.Text, \"Copied outline text\")", Read(Path.Combine("Views", "SmartArtInsertControl.cs")));
        Assert.Contains("ExportSplitButton, ViewModel.StatusText ?? \"Exporting...\", \"Copied export status\"", Read("MainWindow.xaml.cs"));
        var settings = Read(Path.Combine("Views", "SettingsView.xaml.cs"));
        Assert.Contains("CopyFeedback.CopyWithBubble(target, key, \"Copied license key\")", settings);
        Assert.Contains("ApplyButtonTick(b, error)", Method(settings, "OnCopyPluginsFolderPathClick"));
        Assert.Equal(2, Regex.Matches(settings, @"TrySetTextAsync\(plugin\.Id\) is string copyError").Count);
        Assert.Contains("success: error is null", Read(Path.Combine("Views", "SuiteHubView.xaml.cs")));
    }

    [Fact]
    public void SuiteHub_AuditCopiesWarnWhenTheClipboardIsBusy()
    {
        // Every audit put "Copied …" in a green notification whatever the clipboard said.
        var code = Read(Path.Combine("Views", "SuiteHubView.xaml.cs"));
        Assert.True(Regex.Matches(code, @"Services\.CopyStatusText\.Failed\(copyMessage, clipboardError\), success: clipboardError is null").Count >= 80);
    }

    [Fact]
    public void History_HasNoRedundantCopyWrappers()
    {
        var code = Read(Path.Combine("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.DoesNotMatch(new Regex(@"try\s*\{\s*CopyAndNotify\([^\n]*\);\s*\}\s*catch \{ \}"), code);
    }
}
