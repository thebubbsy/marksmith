using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Core.Tests;

public static class E2ETestHelpers
{
    private static readonly ThemeCatalog Themes = new();

    public static async Task<string> ExportDocxToTempFileAsync(string markdown, AppSettings? settings = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-e2e-{Guid.NewGuid():N}.docx");
        var service = new DocxExportService();
        await service.ExportAsync(markdown, path, settings ?? new AppSettings());
        return path;
    }

    public static string ExportDocxXml(string markdown, AppSettings? settings = null, string entryPath = "word/document.xml")
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-e2e-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(markdown, path, settings ?? new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(entryPath);
            if (entry == null) return string.Empty;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public static List<string> GetZipEntries(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    public static string? ReadZipEntry(string docxPath, string entryPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry(entryPath);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    public static List<ValidationErrorInfo> ValidateDocx(string docxPath)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
        return validator.Validate(doc)
            .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
            .ToList();
    }

    public static string RenderHtml(string markdown, AppSettings? settings = null, ThemeDefinition? theme = null)
    {
        var s = settings ?? new AppSettings();
        var t = theme ?? Themes.GetOrDefault(s.Theme ?? "GitHub Light");
        return new MarkdownHtmlService().Render(markdown, s, t);
    }
}
