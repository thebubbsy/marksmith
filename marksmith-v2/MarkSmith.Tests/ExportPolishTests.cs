using Xunit;
using System.Text;
using MarkSmith.Services;
using MarkSmith.ViewModels;

namespace MarkSmith.Tests;

// Run #16 of the WinUI polish routine: the export flow end to end — readable failure messages,
// HTML exports that work outside the app, no "Report..html", and never exporting over the source.
public class ExportPolishTests
{
    // ---- ExportFailureMessage ----

    [Fact]
    public void LockedFile_SaysWhichFileAndWhatToDo()
    {
        var ex = new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
        var msg = ExportFailureMessage.Describe("DOCX", ex, @"C:\Docs\Report.docx");
        Assert.Equal("DOCX export failed: Report.docx is open in another program. Close it there and export again.", msg);
    }

    [Fact]
    public void AccessDenied_PointsAtTheFolder()
    {
        var msg = ExportFailureMessage.Describe("PDF", new UnauthorizedAccessException("Access denied"), @"C:\Program Files\Report.pdf");
        Assert.Equal(@"PDF export failed: MarkSmith isn't allowed to write to C:\Program Files. Choose another output folder.", msg);
    }

    [Fact]
    public void DiskFull_AndUnknownErrors_ReadAsOneSentence()
    {
        Assert.Equal("EPUB export failed: there isn't enough free space on the drive.",
            ExportFailureMessage.Describe("EPUB", new IOException("full", unchecked((int)0x80070070)), @"C:\a.epub"));
        Assert.Equal("PPTX export failed: Boom.",
            ExportFailureMessage.Describe("PPTX", new AggregateException(new InvalidOperationException("Boom\r\n   at stack")), null));
    }

    [Fact]
    public void ThrowIfLocked_DetectsAnOpenFile_AndIgnoresMissingOnes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lock_{Guid.NewGuid():N}.docx");
        ExportFailureMessage.ThrowIfLocked(path); // missing: fine
        File.WriteAllText(path, "x");
        try
        {
            ExportFailureMessage.ThrowIfLocked(path); // closed: fine
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() => ExportFailureMessage.ThrowIfLocked(path));
                Assert.Contains("is open in another program", ExportFailureMessage.Describe("DOCX", ex, path));
            }
        }
        finally { File.Delete(path); }
    }

    // ---- StandaloneHtml ----

    [Fact]
    public void Inline_EmbedsScriptsAndStyles_KeepingDeferAndOnload()
    {
        var assets = Directory.CreateTempSubdirectory("ms_assets_").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(assets, "fonts"));
            File.WriteAllText(Path.Combine(assets, "lib.js"), "window.libLoaded = true; // </script> inside");
            File.WriteAllText(Path.Combine(assets, "k.css"), "@font-face{src:url(fonts/F.woff2) format('woff2'),url(fonts/F.woff) format('woff')}");
            File.WriteAllBytes(Path.Combine(assets, "fonts", "F.woff2"), new byte[] { 1, 2, 3 });

            var b = WebAssets.Base;
            var html = $"""
                <head><link rel="stylesheet" href="{b}/k.css">
                <script defer src="{b}/lib.js" onload="go()"></script>
                <script src="{b}/missing.js"></script>
                <script src="{b}/../secret.js"></script></head>
                """;

            var result = StandaloneHtml.Inline(html, assets);

            Assert.Contains("<script defer src=\"data:text/javascript;base64,", result);
            Assert.Contains("onload=\"go()\"", result);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("window.libLoaded = true; // </script> inside"));
            Assert.Contains(b64, result);
            Assert.Contains("<style>", result);
            Assert.Contains("url(data:font/woff2;base64,AQID)", result);
            Assert.Contains("url(fonts/F.woff)", result); // not bundled: left for the browser to skip
            // Missing files and anything outside the asset folder are left untouched, not read.
            Assert.Contains($"{b}/missing.js", result);
            Assert.Contains($"{b}/../secret.js", result);
        }
        finally { Directory.Delete(assets, true); }
    }

    [Fact]
    public void Inline_RealRender_LeavesNoAppOnlyReferences()
    {
        var assets = FindRepoAssets();
        if (assets is null) return; // asset folder not reachable from this test layout
        var html = new MarkdownHtmlService().Render(
            "# T\n\n```mermaid\ngraph TD; A-->B\n```\n\nMath $x^2$\n\n```csharp\nvar x = 1;\n```\n",
            new Models.AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.True(StandaloneHtml.ReferencesAppAssets(html));

        var standalone = StandaloneHtml.Inline(html, assets);
        Assert.False(StandaloneHtml.ReferencesAppAssets(standalone));
    }

    // ---- One-line $$ … $$ renders as display maths ----

    [Fact]
    public void OneLineDoubleDollar_RendersAsDisplayMath()
    {
        var html = new MarkdownHtmlService().Render(
            "Before:\n$$\\int_0^1 x^2\\,dx$$\nAfter, inline $x$ stays inline.",
            new Models.AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Contains("<div class=\"math\">", html);
        Assert.Contains("\\[", html);
        Assert.Contains("<span class=\"math\">\\(x\\)</span>", html);
    }

    [Theory]
    [InlineData("```\n$$a$$\n```")]          // code fence: untouched
    [InlineData("Price is $$5 and $$6")]     // not a whole line of maths
    [InlineData("text $$a$$ text")]
    public void LiftOneLineDisplayMath_LeavesOtherDollarsAlone(string md) =>
        Assert.Equal(md, MarkdownHtmlService.LiftOneLineDisplayMath(md));

    [Fact]
    public void LiftOneLineDisplayMath_KeepsListIndentation()
    {
        var lifted = MarkdownHtmlService.LiftOneLineDisplayMath("- item\n\n  $$E=mc^2$$\n");
        Assert.Contains("\n  $$\n  E=mc^2\n  $$\n", lifted);
    }

    private static string? FindRepoAssets()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "MarkSmith.Desktop", "Assets", "web");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- MainViewModel export paths ----

    private static (MainViewModel Vm, string Dir, string Input) FileBackedVm()
    {
        var dir = Directory.CreateTempSubdirectory("ms_export_").FullName;
        var input = Path.Combine(dir, "Report.md");
        File.WriteAllText(input, "# Report\n\nBody text.\n");
        var vm = new MainViewModel
        {
            FileNameTemplate = "{title}",
            OutputFolder = "",
            UsePasteSource = false,
            InputFilePath = input,
        };
        return (vm, dir, input);
    }

    [Fact]
    public async Task HtmlExport_WritesReportDotHtml_NotDoubleDot()
    {
        var (vm, dir, _) = FileBackedVm();
        try
        {
            await vm.ConvertToHtmlAsync();
            var expected = Path.Combine(dir, "Report.html");
            Assert.True(File.Exists(expected), vm.StatusText);
            Assert.False(File.Exists(Path.Combine(dir, "Report..html")));
            Assert.Equal(expected, vm.LastOutputPath);
            Assert.StartsWith("HTML saved: Report.html", vm.StatusText);
            Assert.Equal(expected, vm.StatusOutputPath);
            Assert.False(vm.IsBusy);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task MarkdownExport_NeverOverwritesTheOpenSourceFile()
    {
        var (vm, dir, input) = FileBackedVm();
        try
        {
            var original = File.ReadAllText(input);
            await vm.ConvertToMarkdownAsync();
            Assert.Equal(original, File.ReadAllText(input));
            var exported = Path.Combine(dir, "Report (exported).md");
            Assert.True(File.Exists(exported), vm.StatusText);
            Assert.Equal(exported, vm.LastOutputPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LockedTarget_FailsFast_WithAFriendlyMessage_AndNoOpenLinks()
    {
        var (vm, dir, _) = FileBackedVm();
        var target = Path.Combine(dir, "Report.html");
        File.WriteAllText(target, "old");
        try
        {
            using (new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await vm.ConvertToHtmlAsync();

            Assert.Equal("HTML export failed: Report.html is open in another program. Close it there and export again.", vm.StatusText);
            Assert.Equal(Models.StatusSeverity.Error, vm.StatusSeverity);
            Assert.False(vm.HasStatusOutput);
            Assert.False(vm.IsBusy);
            Assert.Equal("old", File.ReadAllText(target));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("# Smoke export\rHello there.\r\rMore.")]   // WinUI TextBox: bare \r
    [InlineData("# Smoke export\r\nHello there.")]
    [InlineData("# Smoke export\nHello there.")]
    public void ExtractTitle_StopsAtTheFirstLineBreak_WhateverItsKind(string md) =>
        Assert.Equal("Smoke export", Models.HistoryEntry.ExtractTitle(md));

    [Fact]
    public async Task PastedEditorText_ExportsUnderItsHeading()
    {
        var dir = Directory.CreateTempSubdirectory("ms_paste_").FullName;
        try
        {
            var vm = new MainViewModel { FileNameTemplate = "{title}", OutputFolder = dir, UsePasteSource = true };
            vm.PastedMarkdown = "# Smoke export\rHello from the editor.\r\r$$x^2$$";
            await vm.ConvertToHtmlAsync();
            Assert.Equal(Path.Combine(dir, "Smoke export.html"), vm.LastOutputPath);
            Assert.True(File.Exists(vm.LastOutputPath), vm.StatusText);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StatusLinks_HideAgain_WhenTheStatusMovesOn()
    {
        var (vm, dir, _) = FileBackedVm();
        try
        {
            await vm.ConvertToHtmlAsync();
            Assert.True(vm.HasStatusOutput);
            vm.StatusText = "Something else happened.";
            Assert.False(vm.HasStatusOutput);
        }
        finally { Directory.Delete(dir, true); }
    }
}
