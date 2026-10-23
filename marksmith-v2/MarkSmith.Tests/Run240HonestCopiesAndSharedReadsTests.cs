using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #240: copy commands report a busy clipboard instead of claiming "Copied …"; files another
// app holds open for writing (a CSV in Excel, a Markdown file in an editor) still open, import and
// export; the Document Metrics card follows the theme, gets hover polish and reads as one stop per
// statistic; Go to line says when it clamps and speaks its preview; Theme Builder swatches are
// named and show their hex; and the insert dialogs' preview is read by what it inserts.
public class Run240HonestCopiesAndSharedReadsTests
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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(DesktopDir(), Path.Combine(parts)));

    private static string Main() => Read("MainWindow.xaml.cs");

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

    // Holds the file the way Excel holds an open CSV: open for writing, sharing reads and writes.
    private static FileStream HoldForWriting(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    private static string TempFile(string extension, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"run240-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void SharedFile_ReadsAFileAnotherAppHoldsOpenForWriting()
    {
        var path = TempFile(".csv", "Quarter,Sales\nQ1,10\n");
        try
        {
            using (HoldForWriting(path))
            {
                // The plain read is what used to fail with "being used by another process".
                Assert.ThrowsAny<IOException>(() => File.ReadAllText(path));
                Assert.Equal("Quarter,Sales\nQ1,10\n", SharedFile.ReadAllText(path));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SharedFile_ReadsAsync_WhileHeld_AndDecodesLikeReadAllText()
    {
        var path = TempFile(".md", "# Heading — café\n", new UTF8Encoding(true));
        var utf16 = TempFile(".md", "Unicode ✓\n", Encoding.Unicode);
        try
        {
            using (HoldForWriting(path))
            {
                Assert.Equal("# Heading — café\n", await SharedFile.ReadAllTextAsync(path));
            }
            // Same decoding as File.ReadAllText: BOMs are honoured and stripped.
            Assert.Equal(File.ReadAllText(path), SharedFile.ReadAllText(path));
            Assert.Equal(File.ReadAllText(utf16), await SharedFile.ReadAllTextAsync(utf16));
        }
        finally
        {
            File.Delete(path);
            File.Delete(utf16);
        }
    }

    [Fact]
    public async Task OpeningAMarkdownFileHeldByAnEditor_StillReadsIt()
    {
        var path = TempFile(".md", "# Held open\n\nBody.\n");
        try
        {
            using (HoldForWriting(path))
            {
                var doc = await Plugins.PluginFileReader.ImportAsync(path);
                Assert.Equal("# Held open\n\nBody.\n", doc.Markdown);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DocumentAndCsvReads_GoThroughSharedFile()
    {
        var core = Path.Combine(Directory.GetParent(DesktopDir())!.FullName, "MarkSmith.Core");
        var reader = File.ReadAllText(Path.Combine(core, "Plugins", "PluginFileReader.cs"));
        Assert.DoesNotMatch(new Regex(@"(?<!Shared)File\.ReadAllTextAsync\("), reader);
        var vm = File.ReadAllText(Path.Combine(core, "ViewModels", "MainViewModel.cs"));
        Assert.Contains("Services.SharedFile.ReadAllText(path)", vm);
        Assert.Contains("Services.SharedFile.ReadAllText(InputFilePath)", vm);

        var main = Main();
        Assert.DoesNotMatch(new Regex(@"(?<!Shared)File\.ReadAllTextAsync\(filePath\)"), main);
        Assert.Equal(2, Regex.Matches(main, @"SharedFile\.ReadAllTextAsync\(filePath\)").Count);
    }

    [Fact]
    public void CopyCommands_DoNotAnnounceSuccessBeforeTheCopyHasHappened()
    {
        var main = Main();
        // The old pattern: fire the copy, then say "Copied …" whatever happened.
        Assert.DoesNotMatch(new Regex(@"_ = Services\.ClipboardWriter\.TrySetTextAsync\([^\n]*\);\r?\n\s*AnnounceStatus\("), main);
        Assert.True(Regex.Matches(main, @"\bCopyAndAnnounce\(").Count > 150);

        var helper = Method(main, "CopyAndAnnounce");
        Assert.Contains("var error = await Services.ClipboardWriter.TrySetTextAsync(text);", helper);
        // Success is announced only when there was no error; a failure says why, as a warning,
        // with the severity set after the text (every StatusText change resets it).
        var warn = helper.IndexOf("Models.StatusSeverity.Warning", StringComparison.Ordinal);
        var text = helper.IndexOf("ViewModel.StatusText = $\"Couldn't copy: {error}\";", StringComparison.Ordinal);
        Assert.True(text > 0 && warn > text);
        Assert.Matches(@"if \(error is null\)\s*\{\s*AnnounceStatus\(message\);\s*return;", helper);
    }

    [Fact]
    public void DocumentMetricsFlyout_FollowsTheTheme_AndPolishesItsButtons()
    {
        var flyout = Method(Main(), "ShowWordCountFlyout");
        Assert.DoesNotContain("Application.Current.Resources[", flyout);
        Assert.Contains(".Themed(Border.BackgroundProperty, \"SubtleFillColorSecondaryBrush\")", flyout);
        Assert.Contains("Services.HoverPolish.AttachOnOpen(flyout);", flyout);
        Assert.True(flyout.IndexOf("AttachOnOpen(flyout)", StringComparison.Ordinal) < flyout.IndexOf("flyout.ShowAt(target)", StringComparison.Ordinal));
        Assert.Contains("ToolTipService.SetToolTip(copyWordsBtn,", flyout);
        Assert.Contains("ToolTipService.SetToolTip(copySummaryBtn,", flyout);
    }

    [Fact]
    public void DocumentMetricsStatistics_ReadAsOneStopEach()
    {
        var flyout = Method(Main(), "ShowWordCountFlyout");
        Assert.Contains("AutomationProperties.SetName(valBlock, $\"{label}: {val}\");", flyout);
        Assert.Contains("SetAccessibilityView(lblBlock, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw)", flyout);
    }

    [Fact]
    public void GoToLine_SaysWhenItClamps_AndSpeaksItsPreview()
    {
        var dialog = Method(Main(), "ShowGoToLineDialogAsync");
        Assert.DoesNotContain("Application.Current.Resources[", dialog);
        Assert.Contains("SetLiveSetting(previewText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite)", dialog);
        Assert.Contains("AutomationEvents.LiveRegionChanged", dialog);
        Assert.Contains("Only {totalLines:N0} line", dialog);
        Assert.Contains("Not a line number, so Go stays on line {currentLine}.", dialog);
        // The preview and Go share one reading of the box.
        Assert.Contains("var (targetLine, targetCol, _) = Resolve();", dialog);
        Assert.Single(Regex.Matches(dialog, @"int\.TryParse\(raw, out int l\)"));
    }

    [Fact]
    public void GoToLine_QuickPicks_PutTheCaretBackInTheBox()
    {
        var dialog = Method(Main(), "ShowGoToLineDialogAsync");
        Assert.Matches(@"button\.Click \+= \(_, _\) =>\s*\{\s*lineColBox\.Text = \$""\{line\}:1"";\s*lineColBox\.Focus\(FocusState\.Keyboard\);\s*lineColBox\.SelectAll\(\);", dialog);
        Assert.Contains("ToolTipService.SetToolTip(button,", dialog);
        Assert.Equal(3, Regex.Matches(dialog, @"AddQuickPick\(\$?""(Start|Mid|End)").Count);
    }

    [Fact]
    public void ThemeBuilderSwatches_AreNamed_AndShowTheirHex()
    {
        var builder = Method(Main(), "OnCreateThemeClick");
        Assert.Contains("AutomationProperties.SetName(swatch, $\"{elementLabel} colour, {hex}\");", builder);
        Assert.Contains("ToolTipService.SetToolTip(swatch,", builder);
        Assert.Contains("hexText.Text = hex;", builder);
        // The colour sits in an inner chip so the button's hover/pressed Background can't hide it.
        Assert.Contains("chip.Background = new SolidColorBrush(color);", builder);
        Assert.DoesNotContain("swatch.Background =", builder);
        // Kept in step with the picker, not just set once.
        Assert.Contains("picker.ColorChanged += (_, args) => ShowSwatch(args.NewColor);", builder);
    }

    [Fact]
    public void InsertDialogPreview_IsReadByWhatItInserts()
    {
        var code = Read("Views", "InsertDialogControls.cs");
        Assert.DoesNotContain("SetName(_preview, \"Markdown preview\")", code);
        var refresh = Method(code, "Refresh");
        Assert.Contains("AutomationProperties.SetName(_preview, snippet.Length == 0 ? \"Inserts nothing yet\" : $\"Inserts: {snippet}\");", refresh);
    }
}
