using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;

namespace MarkSmith.Services;

public sealed record BatchExportOptions
{
    public string InputDirectory { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string Pattern { get; init; } = "*.md";
    public IReadOnlyList<string> Formats { get; init; } = new[] { "pdf", "docx" };
    public bool Recursive { get; init; } = false;
}

public sealed record BatchExportResult
{
    public int TotalFilesFound { get; init; }
    public int TotalExported { get; init; }
    public int TotalFailed { get; init; }
    public IReadOnlyList<string> ExportedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Export Batch &amp; Command-Line CLI Runner (Task 25). Processes multi-file Markdown directory globs
/// and exports them to target formats (HTML, DOCX, EPUB).
/// </summary>
public sealed class BatchExportRunner
{
    private readonly MarkdownHtmlService _htmlSvc;
    private readonly DocxExportService _docxSvc;
    private readonly EpubExportService _epubSvc;
    private readonly ThemeCatalog _themeCatalog;

    public BatchExportRunner(
        MarkdownHtmlService? htmlSvc = null,
        DocxExportService? docxSvc = null,
        EpubExportService? epubSvc = null)
    {
        _htmlSvc = htmlSvc ?? new MarkdownHtmlService();
        _docxSvc = docxSvc ?? new DocxExportService();
        _epubSvc = epubSvc ?? new EpubExportService();
        // Shared AppServices.Themes singleton instead of a private catalog (see DocxExportService) —
        // batch exports must see custom themes saved mid-session, same as every other exporter.
        _themeCatalog = AppServices.Themes;
    }

    public async Task<BatchExportResult> RunAsync(BatchExportOptions options, AppSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(options.InputDirectory) || !Directory.Exists(options.InputDirectory))
        {
            return new BatchExportResult
            {
                Errors = new[] { $"Input directory '{options.InputDirectory}' does not exist." }
            };
        }

        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var pattern = string.IsNullOrWhiteSpace(options.Pattern) ? "*.md" : options.Pattern;
        var files = Directory.GetFiles(options.InputDirectory, pattern, searchOption);

        var outDir = string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? options.InputDirectory
            : options.OutputDirectory;

        Directory.CreateDirectory(outDir);

        var exported = new List<string>();
        var errors = new List<string>();
        var set = settings ?? new AppSettings();
        var theme = _themeCatalog.GetOrDefault(set.Theme);

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var markdown = await File.ReadAllTextAsync(file);

            foreach (var fmt in options.Formats)
            {
                try
                {
                    var cleanFmt = fmt.Trim('.').ToLowerInvariant();
                    var dest = Path.Combine(outDir, $"{fileName}.{cleanFmt}");

                    if (cleanFmt == "html")
                    {
                        var html = _htmlSvc.Render(markdown, set, theme);
                        await File.WriteAllTextAsync(dest, html);
                        exported.Add(dest);
                    }
                    else if (cleanFmt == "docx")
                    {
                        await _docxSvc.ExportAsync(markdown, dest, set);
                        exported.Add(dest);
                    }
                    else if (cleanFmt == "epub")
                    {
                        await _epubSvc.ExportAsync(markdown, dest, set);
                        exported.Add(dest);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed exporting '{file}' to '{fmt}': {ex.Message}");
                }
            }
        }

        return new BatchExportResult
        {
            TotalFilesFound = files.Length,
            TotalExported = exported.Count,
            TotalFailed = errors.Count,
            ExportedFiles = exported,
            Errors = errors
        };
    }
}
