using System.Text;
using MarkSmith.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MarkSmith.Services;

// Tier 1 (lossless) source storage for PDF exports — the PDF counterpart of MarksmithSourceStore.
//
// The ORIGINAL Markdown rides inside the PDF's Info dictionary as a base64-encoded custom entry.
// That is deliberately low-key: normal readers never show it (it only appears under a viewer's
// "advanced document properties / custom" tab), it cannot corrupt the visible layout, and it
// travels with the file. ReverseImportService checks for it first (Tier 1) and hands the exact
// source back byte-for-byte, so a PDF-only workflow can still re-open the original Markdown —
// the same cyclical update loop the .docx path already provides.
//
// Branding lives in the standard /Creator entry ("Marksmith by Matthew Bubb" — the application
// that created the document). PDFsharp owns /Producer on save (it wraps user values with its own
// version string), so we leave that one alone rather than fighting it.
//
// PDF string objects are capped at 65,535 bytes (and base64 expands by 4/3), so sources above
// MaxSourceBytes are skipped: the visible text layer still allows a universal re-import, and the
// DOCX custom-XML part remains the unlimited-lossless channel.
public static class PdfSourceStore
{
    /// <summary>Marker for the embedded-source payload; bumped only on a breaking shape change.</summary>
    public const string Schema = "marksmith-pdf-source-v1";

    // Info-dictionary keys (same convention as PdfSignatureService's /Marksmith* entries).
    private const string KeySchema = "/MarksmithSourceSchema";
    private const string KeyMarkdown = "/MarksmithSourceMD"; // base64 — pure ASCII, escaping-safe
    private const string KeyExportedUtc = "/MarksmithSourceExportedUtc";
    private const string KeyVersion = "/MarksmithSourceVersion";

    /// <summary>Max raw source bytes that still fit inside PDF's string-object limit after base64.</summary>
    public const int MaxSourceBytes = 48_000;

    /// <summary>The recovered source plus the metadata needed to judge its freshness.</summary>
    public sealed record EmbeddedSource(
        string Markdown,
        DateTime ExportedUtc,
        string Version,
        bool IsStale,
        DateTime? ModifiedUtc);

    private static string AssemblyVersion =>
        typeof(PdfSourceStore).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Writes standard metadata (title/author/subject/creator) and, when the source fits, embeds
    /// the original Markdown into the PDF's Info dictionary. Called AFTER security is applied so the
    /// entries can never be dropped by the encryption re-save; pass the owner password when the file
    /// is protected so we can open it for modification.
    /// </summary>
    public static void Apply(string pdfPath, string? sourceMarkdown, string? title, AppSettings settings, string? ownerPassword = null)
    {
        using var doc = OpenModify(pdfPath, ownerPassword);
        var info = doc.Info;

        // Standard, professional metadata — invisible in the body, visible in file properties.
        if (!string.IsNullOrWhiteSpace(title)) info.Title = title;
        info.Author = settings.AuthorName; // empty = no attribution (matches DOCX Creator behavior)
        info.Subject = ExportBranding.CreatedIn;
        info.Creator = ExportBranding.Tag; // the application that created the document

        var bytes = Encoding.UTF8.GetBytes(sourceMarkdown ?? "");
        if (bytes.Length is > 0 and <= MaxSourceBytes)
        {
            info.Elements.SetString(KeySchema, Schema);
            info.Elements.SetString(KeyMarkdown, Convert.ToBase64String(bytes));
            info.Elements.SetString(KeyExportedUtc, DateTime.UtcNow.ToString("O"));
            info.Elements.SetString(KeyVersion, AssemblyVersion);
        }
        else
        {
            // Oversized (or absent) source: drop any prior embed so a re-export never carries a
            // stale copy that no longer matches the document.
            info.Elements.Remove(KeySchema);
            info.Elements.Remove(KeyMarkdown);
            info.Elements.Remove(KeyExportedUtc);
            info.Elements.Remove(KeyVersion);
        }

        // Align the modification stamp with the embed time so a fresh export is never judged stale
        // by sub-second rounding; a genuine later edit pushes it well past the tolerance window.
        info.ModificationDate = DateTime.UtcNow;

        Save(doc, pdfPath);
    }

    /// <summary>Reads the embedded source from a PDF file, or null when it was not produced by Marksmith.</summary>
    public static EmbeddedSource? Read(string pdfPath, string? password = null)
    {
        using var doc = string.IsNullOrEmpty(password)
            ? PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import)
            : PdfReader.Open(pdfPath, password, PdfDocumentOpenMode.Import);
        return Read(doc);
    }

    /// <summary>Reads the embedded source from an already-open document (Import or Modify mode).</summary>
    public static EmbeddedSource? Read(PdfDocument doc)
    {
        var info = doc.Info;
        if (info.Elements.GetString(KeySchema) != Schema) return null;

        var b64 = info.Elements.GetString(KeyMarkdown);
        if (string.IsNullOrWhiteSpace(b64)) return null;

        string markdown;
        try
        {
            markdown = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            return null; // corrupt entry — treat as not-a-Marksmith-PDF, never crash a re-import
        }

        var exportedUtc = ParseUtc(info.Elements.GetString(KeyExportedUtc));
        var modified = info.ModificationDate;
        var modifiedUtc = modified == default ? null : (DateTime?)modified.ToUniversalTime();

        // Stale when any editor saved the file after we exported it (2-second tolerance absorbs
        // PDF date rounding). A missing modification stamp = no evidence of an edit = not stale.
        var isStale = exportedUtc is not null && modifiedUtc is not null &&
                      modifiedUtc > exportedUtc.Value.AddSeconds(2);

        return new EmbeddedSource(
            Markdown: markdown,
            ExportedUtc: exportedUtc ?? DateTime.MinValue,
            Version: info.Elements.GetString(KeyVersion) ?? "",
            IsStale: isStale,
            ModifiedUtc: modifiedUtc);
    }

    private static PdfDocument OpenModify(string pdfPath, string? ownerPassword)
    {
        if (!string.IsNullOrEmpty(ownerPassword))
        {
            try { return PdfReader.Open(pdfPath, ownerPassword, PdfDocumentOpenMode.Modify); }
            catch { /* encrypted with an empty owner password — fall through to the plain open */ }
        }
        return PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify);
    }

    private static void Save(PdfDocument doc, string path)
    {
        using var output = new MemoryStream();
        doc.Save(output, false);
        File.WriteAllBytes(path, output.ToArray());
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUniversalTime()
            : null;
}
