using System;
using System.Globalization;
using MarkSmith.Models;
using MarkSmith.ViewModels;

namespace MarkSmith.Services;

/// <summary>
/// The sample output Settings shows under a setting, so a row says what it will produce rather
/// than describing it: the file name an export gets, the header and footer bands a PDF prints, the
/// streaming address on the current port. Every preview goes through the same code the export
/// does, so the sample and the real thing can't drift apart.
/// </summary>
public static class SettingsPreviews
{
    /// <summary>The document title the previews use.</summary>
    public const string SampleTitle = "My Report";

    /// <summary>
    /// The file name an export of <see cref="SampleTitle"/> gets with this template and format:
    /// "2026-07-24 My Report.pdf". Blank or token-only results fall back to the title, as the
    /// export does.
    /// </summary>
    public static string FileName(string? template, string? format, DateTime now)
    {
        var extension = OutputFormats.Normalize(format) ?? OutputFormats.Pdf;
        var baseName = MainViewModel.ApplyFileNameTemplate(template, SampleTitle, extension, now);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = SampleTitle;
        return $"{baseName}.{extension}";
    }

    /// <summary>
    /// Text that would be printed at the top and bottom of a PDF page, and how it's aligned
    /// ("left", "center" or "right"). Empty strings mean that band prints nothing.
    /// </summary>
    public sealed record PageBands(string Header, string Footer, string Alignment)
    {
        public bool IsEmpty => Header.Length == 0 && Footer.Length == 0;
    }

    /// <summary>
    /// Page 2 of 10 of <see cref="SampleTitle"/> with these settings. Follows
    /// <see cref="PdfExportService.BuildHeaderFooter"/>: a page-number position with an empty
    /// matching band prints "Page {page} of {pages}", and both bands take the position's alignment.
    /// {date} is the reader's short date, which is what the PDF engine prints.
    /// </summary>
    public static PageBands PdfBands(string? position, string? header, string? footer, DateTime date)
    {
        var settings = new AppSettings
        {
            PdfPageNumberPosition = position ?? "None",
            PdfHeaderTemplate = header ?? "",
            PdfFooterTemplate = footer ?? "",
        };
        var (top, bottom, align) = PdfExportService.ResolveBands(settings);
        return new PageBands(Expand(top, date), Expand(bottom, date), align);
    }

    private static string Expand(string template, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var localDate = template.Replace("{date}", date.ToString("d", CultureInfo.CurrentCulture), StringComparison.Ordinal);
        return PdfExportService.SubstituteTokens(localDate, SampleTitle, 2, 10, date);
    }

    /// <summary>The WebSocket address scripts connect to on this port.</summary>
    public static string StreamingEndpoint(int port) => $"ws://127.0.0.1:{port}/api/stream";
}
