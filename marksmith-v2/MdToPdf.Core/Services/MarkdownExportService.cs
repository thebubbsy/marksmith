using MdToPdf.Models;

namespace MdToPdf.Services;

// Markdown (.md) export — the natural counterpart to the DOCX -> MD reverse pipeline
// (ReverseImportService). Import a Word file, let Marksmith clean it up, then save the recovered
// source back out as canonical Markdown. It runs the same shared cleanup pipeline every other
// export format applies (newline normalization, admonition/dialect canonicalization, the user's
// emoji/dash preferences, and formatting settings), so the .md that lands on disk matches the
// content that feeds the PDF/DOCX/PPTX/EPUB exporters — a first-class output format, not a raw dump.
public sealed class MarkdownExportService
{
    public const string Extension = "md";

    public Task ExportAsync(string markdown, string mdPath, AppSettings settings) => Task.Run(() =>
    {
        markdown = TextNormalizer.Newlines(markdown);
        markdown = AdmonitionNormalizer.Apply(markdown);
        markdown = DialectNormalizer.Apply(markdown, settings.DashMode);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);

        var dir = Path.GetDirectoryName(mdPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // UTF-8 without a BOM — the convention for Markdown files and what every editor expects.
        File.WriteAllText(mdPath, markdown, new System.Text.UTF8Encoding(false));
    });
}
