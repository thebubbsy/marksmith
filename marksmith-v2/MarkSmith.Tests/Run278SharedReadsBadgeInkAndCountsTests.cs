using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using MarkSmith.Services.Email;
using Xunit;

namespace MarkSmith.Tests;

// Run #278: files open elsewhere, Galaxy badge ink and counts at one.
// - Exports, the preview and OCR read pictures, logos, e-mails and PDFs with shared access, so an
//   image still open in an editor or a sync client is no longer dropped (File.ReadAllBytes asks
//   for FileShare.Read and fails while anyone holds the file open for writing);
// - Opening an .eml / .msg and batch exports read the same way;
// - Document Galaxy: the node format badge and the collapsed-subtree pill picked white on blue,
//   purple, rose and mid greens (2.3:1 to 3.9:1 at 9 px); they now use the OCR studio's WCAG
//   crossover, and the pill's tooltip says "1 hidden node";
// - Suite Hub workspace audits, the editor's inventory / TOC / word-count copies and the OCR
//   overlay's finish banner say "1 doc", "1 table", "1 word" instead of "1 docs".
public class Run278SharedReadsBadgeInkAndCountsTests
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

    private const string Galaxy = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string Hub = "MarkSmith.Desktop/Views/SuiteHubView.xaml.cs";
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string Overlay = "MarkSmith.Desktop/Controls/OcrExperienceOverlay.xaml.cs";

    // ---- SharedFile (Core) ----

    private static string TempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "ms278_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    // Another app holding the file open for writing (an editor, OneDrive, a capture tool).
    private static FileStream HoldForWriting(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    [Fact]
    public void ReadAllBytes_ReadsAFileAnotherAppHasOpenForWriting()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5 };
        var path = TempFile(bytes);
        try
        {
            using (HoldForWriting(path))
            {
                Assert.Throws<IOException>(() => File.ReadAllBytes(path));
                Assert.Equal(bytes, SharedFile.ReadAllBytes(path));
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OpenRead_IsReadOnlyAndShared()
    {
        var path = TempFile(Encoding.UTF8.GetBytes("hello"));
        try
        {
            using var held = HoldForWriting(path);
            using var stream = SharedFile.OpenRead(path);
            Assert.True(stream.CanRead);
            Assert.False(stream.CanWrite);
            Assert.Equal(5, stream.Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadAllBytes_EmptyAndMissingFiles()
    {
        var path = TempFile(Array.Empty<byte>());
        try { Assert.Empty(SharedFile.ReadAllBytes(path)); }
        finally { File.Delete(path); }
        Assert.Throws<FileNotFoundException>(() => SharedFile.ReadAllBytes(path));
    }

    [Fact]
    public void EmailImport_OpensAnEmlThatIsStillOpenElsewhere()
    {
        var eml = "From: Pat <pat@example.com>\r\nTo: Sam <sam@example.com>\r\nSubject: Quarterly notes\r\n"
                + "Date: Mon, 26 Oct 2026 09:00:00 +1100\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
                + "Hello there.\r\n";
        var dir = Path.Combine(Path.GetTempPath(), "ms278_eml_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "notes.eml");
        File.WriteAllText(path, eml);
        try
        {
            using (HoldForWriting(path))
            {
                var result = EmailImporter.Import(path, mediaDir: Path.Combine(dir, "media"));
                Assert.Equal("Quarterly notes", result.Subject);
                Assert.Contains("Hello there.", result.Markdown);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    public static TheoryData<string, string> SharedReadSites => new()
    {
        { "MarkSmith.Core/Services/DocxExportService.cs", "SharedFile.ReadAllBytes(settings.BrandLogoPath)" },
        { "MarkSmith.Core/Services/DocxExportService.cs", "return SharedFile.ReadAllBytes(path);" },
        { "MarkSmith.Core/Generator/DocxPackageWriter.cs", "SharedFile.OpenRead(imagePath)" },
        { "MarkSmith.Core/Services/Email/EmailHtmlRenderer.cs", "bytes = SharedFile.ReadAllBytes(path);" },
        { "MarkSmith.Core/Services/Email/EmailImporter.cs", "SharedFile.OpenRead(emlPath)" },
        { "MarkSmith.Core/Services/Email/MsgImporter.cs", "SharedFile.OpenRead(msgPath)" },
        { "MarkSmith.Core/Services/EpubExportService.cs", "coverBytes = SharedFile.ReadAllBytes(logoPath);" },
        { "MarkSmith.Core/Services/EpubExportService.cs", "SharedFile.ReadAllBytes(resolved)" },
        { "MarkSmith.Core/Services/MarkdownHtmlService.cs", "var raw = SharedFile.ReadAllBytes(path);" },
        { "MarkSmith.Core/Services/MarkdownHtmlService.cs", "(SharedFile.ReadAllBytes(path), \"image/svg+xml\")" },
        { "MarkSmith.Core/Services/PptxExportService.cs", "var bytes = SharedFile.ReadAllBytes(path);" },
        { "MarkSmith.Core/Services/PptxExportService.cs", "SharedFile.OpenRead(imgPath)" },
        { "MarkSmith.Core/Services/StandaloneHtml.cs", "Convert.ToBase64String(SharedFile.ReadAllBytes(file))" },
        { "MarkSmith.Core/Services/OcrEngineService.cs", "SharedFile.OpenRead(path)" },
        { "MarkSmith.Core/Services/OcrEngineService.cs", "SharedFile.OpenRead(pdfPath)" },
        { "MarkSmith.Core/Services/BatchExportRunner.cs", "await SharedFile.ReadAllTextAsync(file)" },
        { Main, "SharedFile.OpenRead(path).AsRandomAccessStream()" },
    };

    [Theory]
    [MemberData(nameof(SharedReadSites))]
    public void DocumentAssetReads_UseSharedAccess(string file, string call)
    {
        Assert.Contains(call, Read(file));
    }

    [Fact]
    public void ExportersNoLongerReadDocumentImagesWithExclusiveAccess()
    {
        Assert.DoesNotMatch(@"(?<!Shared)File\.ReadAllBytes\(resolved\)", Read("MarkSmith.Core/Services/EpubExportService.cs"));
        Assert.DoesNotMatch(@"(?<!Shared)File\.ReadAllBytes\(path\)", Read("MarkSmith.Core/Services/MarkdownHtmlService.cs"));
        Assert.DoesNotMatch(@"(?<!Shared)File\.OpenRead\(imagePath\)", Read("MarkSmith.Core/Generator/DocxPackageWriter.cs"));
        var main = Read(Main);
        Assert.DoesNotContain("FileAccess.Read, FileShare.ReadWrite)", main);
        Assert.Equal(2, Regex.Matches(main, Regex.Escape("using var stream = MarkSmith.Services.SharedFile.OpenRead(filePath);")).Count);
    }

    // ---- Document Galaxy badge ink ----

    // MindMapStudioViewModel.PaletteColors (orange, cyan, green, blue, purple, rose, yellow, crimson)
    // plus mid greens a user can pick from the colour picker.
    public static TheoryData<string> BranchColours => new()
    {
        "#FF7C4D", "#22D3EE", "#34D399", "#3B82F6", "#A855F7", "#EC4899", "#FBBF24", "#E11D48", "#22C55E", "#10B981",
    };

    [Theory]
    [MemberData(nameof(BranchColours))]
    public void GalaxyBadgeInk_IsTheHigherContrastChoice(string hex)
    {
        byte r = Convert.ToByte(hex[1..3], 16), g = Convert.ToByte(hex[3..5], 16), b = Convert.ToByte(hex[5..7], 16);
        double onSlate = ContrastGuard.GetContrastRatio(hex, "#111827");
        double onWhite = ContrastGuard.GetContrastRatio(hex, "#FFFFFF");
        double picked = ReadableInk.PrefersDarkInk(r, g, b) ? onSlate : onWhite;
        Assert.True(picked >= Math.Min(onSlate, onWhite), hex);
        Assert.True(picked >= 4.0, $"{hex} badge ink is only {picked:F2}:1");
    }

    [Fact]
    public void GalaxyPaletteMatchesTheViewModel()
    {
        var vm = Read("MarkSmith.Core/ViewModels/MindMap/MindMapStudioViewModel.cs");
        foreach (var hex in new[] { "#FF7C4D", "#22D3EE", "#34D399", "#3B82F6", "#A855F7", "#EC4899", "#FBBF24", "#E11D48" })
        {
            Assert.Contains($"\"{hex}\"", vm);
        }
    }

    [Fact]
    public void GalaxyReadableOn_UsesTheWcagCrossover_AndBadgesStartReadable()
    {
        var src = Read(Galaxy);
        Assert.Contains("ReadableInk.PrefersDarkInk(background.R, background.G, background.B)", src);
        Assert.DoesNotContain("0.299 * background.R", src);
        Assert.Contains("BadgeInk = new SolidColorBrush(ReadableOn(ColorFromHex(node.ColorHex))),", src);
        Assert.DoesNotContain("BadgeInk = new SolidColorBrush(Colors.White)", src);
    }

    [Fact]
    public void GalaxyCollapsedPillTooltip_CountsHiddenNodes()
    {
        var src = Read(Galaxy);
        Assert.Contains("Subtree collapsed ({CountText.Of(descendants, \"hidden node\")})", src);
        Assert.DoesNotContain("hidden nodes)", src);
        Assert.Equal("1 hidden node", CountText.Of(1, "hidden node"));
        Assert.Equal("12 hidden nodes", CountText.Of(12, "hidden node"));
    }

    // ---- Counts at one ----

    [Fact]
    public void SuiteHubAudits_CountDocsFilesAndFindingsWithTheRightNoun()
    {
        var src = Read(Hub);
        Assert.DoesNotMatch(@"\{(result|audit)\.\w+\} (docs|files|words|blocks|formulas|errors|tables|links|formats|missing keys|missing files|missing anchors)\b", src);
        foreach (var expected in new[]
        {
            "CountText.Of(result.TotalFormulasFound, \"formula\")",
            "CountText.Of(result.SyntaxErrorCount, \"error\")",
            "CountText.Of(result.MissingRequiredKeysCount, \"missing key\")",
            "CountText.Of(result.MissingAnchorCount, \"missing anchor\")",
            "CountText.Of(result.DocumentsWithTasksCount, \"doc\")",
            "CountText.Of(result.DocumentsWithImagesCount, \"doc\")",
            "CountText.Of(audit.ExistingFiles, \"file\")",
            "CountText.Of(audit.UniqueExtensions, \"format\")",
        })
        {
            Assert.Contains(expected, src);
        }
    }

    [Fact]
    public void EditorInventoryAndCopyMessages_CountWithTheRightNoun()
    {
        var src = Read(Main);
        // Parenthesised counts in status lines; "All {n} footnotes" sits behind a count == 1 branch.
        Assert.DoesNotMatch(@"\(\{[\w.]+:N0\} (links|tables|images|headings|words|lines|languages|blocks|rows|tasks|footnotes)\b", src);
        foreach (var expected in new[]
        {
            "Inserted Link Index ({CountText.Of(links.Count, \"link\")}) at cursor",
            "Copied Table Inventory Summary ({CountText.Of(tables.Count, \"table\")}) to clipboard",
            "Copied Image Asset Inventory Table ({CountText.Of(images.Count, \"image\")}) to clipboard",
            "Copied Markdown ({CountText.Of(stats.Words, \"word\")}, {CountText.Of(stats.Lines, \"line\")}) to clipboard",
            "Copied Code Language Breakdown ({CountText.Of(grouped.Count, \"language\")}, {CountText.Of(blocks.Count, \"block\")}) to clipboard",
            "Inserted Task Progress Summary ({CountText.Of(tasks.Count, \"task\")}) at cursor",
            "Copied Footnote Inventory Table ({CountText.Of(footnotes.Count, \"footnote\")}) to clipboard",
            "({CountText.Of(totalRows, \"row\")}) as TSV for Excel/Sheets",
            "Inserted Table of Contents ({CountText.Of(count, \"heading\")}) at cursor",
            "CopyAndAnnounce(CountText.Of(stats.Words, \"word\"), $\"Copied word count: {CountText.Of(stats.Words, \"word\")}\")",
        })
        {
            Assert.Contains(expected, src);
        }
    }

    [Fact]
    public void OcrOverlayFinishBanner_CountsWordsAndBlocks()
    {
        var src = Read(Overlay);
        Assert.DoesNotContain("{_totalWords} words", src);
        Assert.DoesNotContain("_totalBlocks == 1 ? \"\" : \"s\"", src);
        Assert.Contains("CountText.Of(_totalBlocks, \"Markdown block\")", src);
        Assert.Equal("1 Markdown block", CountText.Of(1, "Markdown block"));
    }
}
