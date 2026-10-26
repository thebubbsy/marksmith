using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services.Editor;
using Xunit;

namespace MarkSmith.Tests;

// Run #241: the honest copy feedback from run #240 reaches Version History, OCR Studio, the OCR
// overlay, Suite Hub, the extension tip / hint bar and the main window's remaining inventory copies,
// so none of them says "Copied" while another app holds the clipboard. Go to line's preview reads
// the unfolded document, so it shows the line Go lands on when a fold sits above it.
public class Run241HonestCopiesEverywhereTests
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

    // A fire-and-forget copy followed by a success message on the next statement.
    private static readonly Regex FireAndForgetThenClaim = new(
        @"_ = (?:Services\.)?ClipboardWriter\.TrySetTextAsync\([^;]+\);\s*(?:_vm\.ShowNotice|SetFooterStatusText|SetNotification|AnnounceStatus|StageStatusText\.Text)",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("Views", "History", "HistoryWindow.xaml.cs")]
    [InlineData("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs")]
    [InlineData("Views", "SuiteHubView.xaml.cs")]
    [InlineData("Controls", "OcrExperienceOverlay.xaml.cs")]
    [InlineData("MainWindow.xaml.cs")]
    public void NoCopyClaimsSuccessBeforeItHappens(params string[] path)
    {
        var code = Read(path);
        var hits = FireAndForgetThenClaim.Matches(code).Select(m => m.Value).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    [Fact]
    public void History_CopyAndNotify_ReportsTheClipboardError()
    {
        var code = Read("Views", "History", "HistoryWindow.xaml.cs");
        var body = Method(code, "CopyAndNotify");
        Assert.Contains("await Services.ClipboardWriter.TrySetTextAsync(text)", body);
        Assert.Contains("isError: true", body);
        Assert.Contains("Couldn't copy", body);
        // Every History menu copy goes through it (there were 84 fire-and-forget copies).
        Assert.True(Regex.Matches(code, @"CopyAndNotify\(").Count >= 80);
        Assert.DoesNotContain("_ = Services.ClipboardWriter.TrySetTextAsync", code);
    }

    [Fact]
    public void OcrStudio_MenuCopies_ReportAndRefuseEmptyText()
    {
        var code = Read("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs");
        var body = Method(code, "CopyAndReport");
        Assert.Contains("IsNullOrWhiteSpace(text)", body);
        Assert.Contains("Nothing to copy", body);
        Assert.Contains("Couldn't copy", body);
        // The review menus' page/document copies used to be silent awaits with no feedback.
        Assert.DoesNotMatch(@"(?m)^\s*await ClipboardWriter\.TrySetTextAsync\((fullDoc|md)\);", code);
        Assert.True(Regex.Matches(code, @"CopyAndReport\(").Count >= 8);
    }

    [Fact]
    public void OcrStudio_CopyMarkdownButton_TickAndFooterAgree()
    {
        var body = Method(Read("Views", "Ocr", "OcrInspectionStudioWindow.xaml.cs"), "OnCopyMarkdownClick");
        Assert.Contains("var error = await ClipboardWriter.TrySetTextAsync(md)", body);
        Assert.Contains("ApplyIconTick(CopyMarkdownIcon, CopyMarkdownButton, error)", body);
        Assert.Contains("Couldn't copy", body);
    }

    [Fact]
    public void OcrOverlay_CopyFinalMarkdown_IsHonest()
    {
        var body = Method(Read("Controls", "OcrExperienceOverlay.xaml.cs"), "CopyFinalMarkdownToClipboard");
        Assert.Contains("var error = await Services.ClipboardWriter.TrySetTextAsync(text)", body);
        Assert.Contains("ApplyButtonTick(CopyFinalMarkdownButton, error)", body);
        Assert.Contains("Couldn't copy", body);
    }

    [Fact]
    public void SuiteHub_CopyPath_WarnsWhenTheClipboardIsBusy()
    {
        var code = Read("Views", "SuiteHubView.xaml.cs");
        Assert.Contains("var error = await Services.ClipboardWriter.TrySetTextAsync(fullPath)", code);
        Assert.Contains("success: error is null", code);
    }

    [Theory]
    [InlineData("ExtensionTip.xaml.cs", "CopyExtensionInfo")]
    [InlineData("ExtensionHintBar.xaml.cs", "CopyHintInfo")]
    public void ExtensionNudges_TickReflectsTheRealResult(string file, string method)
    {
        var body = Method(Read("Controls", file), method);
        Assert.Contains("var error = await Services.ClipboardWriter.TrySetTextAsync(info)", body);
        Assert.Contains("ApplyButtonTick(GetExtensionButton, error,", body);
        Assert.DoesNotContain("ApplyButtonTick(GetExtensionButton, null", body);
    }

    [Fact]
    public void ExtensionSetup_CopyGuideUrl_CopiesOnce()
    {
        var code = Read("Views", "ExtensionSetupView.xaml.cs");
        Assert.DoesNotContain("_ = ClipboardWriter.TrySetTextAsync(BrowserExtensionPackage.GuideUrl)", code);
        Assert.Contains("CopyFeedback.CopyWithTick(BrowserExtensionPackage.GuideUrl", code);
    }

    [Fact]
    public void MainWindow_InventoryCopies_UseCopyAndAnnounce()
    {
        var code = Read("MainWindow.xaml.cs");
        // Run #267 moved these to CountText ("3 footnotes" instead of "3 footnote(s)").
        Assert.Contains("CopyAndAnnounce(sb.ToString().TrimEnd(), $\"Copied {CountText.Of(footnotes.Count, \"footnote\")}", code);
        Assert.Contains("CopyAndAnnounce(joined, $\"Copied {CountText.Of(diagrams.Count, \"diagram/visual block\")}", code);
        // Run #278: the word and line counts use CountText too ("1 word").
        Assert.Contains("CopyAndAnnounce(md, $\"Copied Markdown ({CountText.Of(stats.Words, \"word\")}", code);
    }

    [Fact]
    public void GoToLine_PreviewReadsTheUnfoldedDocument()
    {
        var body = Method(Read("MainWindow.xaml.cs"), "ShowGoToLineDialogAsync");
        Assert.Contains("var docText = _folds.HasFolds(text) ? _folds.Document(text) : text;", body);
        var preview = body[body.IndexOf("string GetLinePreview", StringComparison.Ordinal)..];
        preview = preview[..preview.IndexOf("(int Line, int Col, string Note) Resolve()", StringComparison.Ordinal)];
        Assert.DoesNotContain("text[", preview.Replace("docText[", ""));
    }

    // The behaviour the preview now relies on: with a fold above it, document line N is found in
    // Document(visible), not in the visible text.
    [Fact]
    public void FoldAboveTarget_DocumentLineDiffersFromVisibleLine()
    {
        const string doc = "# Title\r\r## Summary\rone\rtwo\rthree\r\r## Next\rtarget line";
        var folds = new EditorFolds();
        var visible = folds.Toggle(doc, 3);
        Assert.True(folds.HasFolds(visible));
        var visibleLines = visible.Split('\r');
        var docLines = folds.Document(visible).Split('\r');
        Assert.Equal("target line", docLines[8]);           // document line 9
        Assert.NotEqual("target line", visibleLines.ElementAtOrDefault(8));
        Assert.Equal(9, folds.DocumentLine(visible, visibleLines.Length));
    }
}
