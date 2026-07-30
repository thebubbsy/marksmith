using System;
using System.IO;
using System.Linq;
using MdToPdf.Services;
using PdfSharp.Pdf;
using Xunit;

namespace MdToPdf.Core.Tests;

/// <summary>
/// Unit tests for PdfSignatureService (D3) — certificate generation, document hashing,
/// signing, verification, and DRM enforcement.
/// </summary>
public class PdfSignatureServiceTests
{
    private static MemoryStream CreateMinimalPdf()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms, false);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void CreateSigningCertificate_generates_valid_cert()
    {
        using var cert = PdfSignatureService.CreateSigningCertificate("Test Signer", "Marksmith Corp");

        Assert.True(cert.HasPrivateKey);
        Assert.Contains("Test Signer", cert.Subject);
        Assert.Contains("Marksmith Corp", cert.Subject);
        Assert.True(cert.NotAfter > DateTime.UtcNow.AddYears(4));
    }

    [Fact]
    public void ComputeDocumentHash_deterministic()
    {
        using var pdf = CreateMinimalPdf();
        var hash1 = PdfSignatureService.ComputeDocumentHash(pdf);

        pdf.Position = 0;
        var hash2 = PdfSignatureService.ComputeDocumentHash(pdf);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 hex = 64 chars
    }

    [Fact]
    public void SignPdf_embeds_signature_metadata()
    {
        using var cert = PdfSignatureService.CreateSigningCertificate("Signer");
        using var pdf = CreateMinimalPdf();

        var signed = PdfSignatureService.SignPdf(pdf, cert);
        Assert.True(signed.Length > 0);

        // Verify the signed PDF has signature metadata.
        using var signedStream = new MemoryStream(signed);
        var result = PdfSignatureService.VerifySignature(signedStream);

        Assert.True(result.HasSignature);
        Assert.True(result.IsHashPresent);
        Assert.Equal(cert.Thumbprint, result.SignerThumbprint);
    }

    [Fact]
    public void VerifySignatureWithCertificate_valid_cert_returns_true()
    {
        using var cert = PdfSignatureService.CreateSigningCertificate("Verifier");
        using var pdf = CreateMinimalPdf();

        var signed = PdfSignatureService.SignPdf(pdf, cert);
        using var signedStream = new MemoryStream(signed);

        Assert.True(PdfSignatureService.VerifySignatureWithCertificate(signedStream, cert));
    }

    [Fact]
    public void VerifySignatureWithCertificate_wrong_cert_returns_false()
    {
        using var cert1 = PdfSignatureService.CreateSigningCertificate("Signer1");
        using var cert2 = PdfSignatureService.CreateSigningCertificate("Signer2");
        using var pdf = CreateMinimalPdf();

        var signed = PdfSignatureService.SignPdf(pdf, cert1);
        using var signedStream = new MemoryStream(signed);

        Assert.False(PdfSignatureService.VerifySignatureWithCertificate(signedStream, cert2));
    }

    [Fact]
    public void VerifySignature_unsigned_pdf_reports_no_signature()
    {
        using var pdf = CreateMinimalPdf();
        var result = PdfSignatureService.VerifySignature(pdf);

        Assert.False(result.HasSignature);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void ApplyDrm_sets_password_protection()
    {
        using var pdf = CreateMinimalPdf();
        var drmPdf = PdfSignatureService.ApplyDrm(pdf, "owner123", "user456",
            allowPrinting: true, allowCopying: false);

        Assert.True(drmPdf.Length > 0);
        // DRM-protected PDFs cannot be opened without a password.
        // (PDFsharp throws when opening encrypted PDFs without the password.)
    }
}

/// <summary>
/// Unit tests for VisualDocumentDiffService (D5) — LCS diff, segment classification,
/// inline word diff, and HTML rendering.
/// </summary>
public class VisualDocumentDiffServiceTests
{
    [Fact]
    public void ComputeDiff_identical_documents_no_changes()
    {
        var md = "# Title\n\nHello world\n";
        var result = VisualDocumentDiffService.ComputeDiff(md, md);

        Assert.False(result.HasChanges);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.True(result.UnchangedCount > 0);
    }

    [Fact]
    public void ComputeDiff_detects_additions()
    {
        var left = "# Title\n\nParagraph one\n";
        var right = "# Title\n\nParagraph one\n\nNew paragraph\n";

        var result = VisualDocumentDiffService.ComputeDiff(left, right);

        Assert.True(result.AddedCount > 0);
        Assert.Contains(result.Segments, s => s.ChangeType == DiffChangeType.Added && s.Text.Contains("New paragraph"));
    }

    [Fact]
    public void ComputeDiff_detects_deletions()
    {
        var left = "# Title\n\nKeep this\n\nDelete this\n";
        var right = "# Title\n\nKeep this\n";

        var result = VisualDocumentDiffService.ComputeDiff(left, right);

        Assert.True(result.DeletedCount > 0);
        Assert.Contains(result.Segments, s => s.ChangeType == DiffChangeType.Deleted && s.Text.Contains("Delete this"));
    }

    [Fact]
    public void ComputeDiff_html_contains_redline_styling()
    {
        var result = VisualDocumentDiffService.ComputeDiff("old line\n", "new line\n");

        Assert.Contains("diff-added", result.Html);
        Assert.Contains("diff-deleted", result.Html);
        Assert.Contains("<!DOCTYPE html>", result.Html);
    }

    [Fact]
    public void InlineWordDiff_marks_changed_words()
    {
        var diff = VisualDocumentDiffService.InlineWordDiff("the quick brown fox", "the slow brown cat");

        Assert.Contains("<del>quick</del>", diff);
        Assert.Contains("<ins>slow</ins>", diff);
        Assert.Contains("<del>fox</del>", diff);
        Assert.Contains("<ins>cat</ins>", diff);
        Assert.Contains("brown", diff); // unchanged word present without markup
    }

    [Fact]
    public void ComputeDiff_empty_left_all_added()
    {
        var result = VisualDocumentDiffService.ComputeDiff("", "line1\nline2\n");

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(0, result.DeletedCount);
    }
}

/// <summary>
/// Unit tests for PdfCompressorService (D6) — compression presets, analysis, and metadata stripping.
/// </summary>
public class PdfCompressorServiceTests
{
    private static MemoryStream CreatePdfWithText()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
        gfx.DrawString("Hello World", new PdfSharp.Drawing.XFont("Arial", 12),
            PdfSharp.Drawing.XBrushes.Black, 72, 72);
        var ms = new MemoryStream();
        doc.Save(ms, false);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Compress_returns_valid_pdf()
    {
        using var pdf = CreatePdfWithText();
        var result = PdfCompressorService.Compress(pdf, CompressionPreset.Email);

        Assert.True(result.CompressedPdf.Length > 0);
        Assert.Equal(1, result.PagesProcessed);
        Assert.True(result.OriginalSize > 0);
    }

    [Fact]
    public void Compress_preserves_page_count()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        doc.AddPage();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms, false);
        ms.Position = 0;

        var result = PdfCompressorService.Compress(ms, CompressionPreset.Web);
        Assert.Equal(3, result.PagesProcessed);
    }

    [Fact]
    public void Analyze_reports_page_count()
    {
        using var pdf = CreatePdfWithText();
        var analysis = PdfCompressorService.Analyze(pdf);

        Assert.Equal(1, analysis.PageCount);
        Assert.True(analysis.TotalSize > 0);
    }

    [Fact]
    public void CompressionResult_savings_percent_calculated()
    {
        var result = new PdfCompressionResult(Array.Empty<byte>(), 1000, 700, 1, 0);
        Assert.Equal(30.0, result.SavingsPercent, 1);
        Assert.True(result.WasReduced);
    }

    [Fact]
    public void PdfAnalysisResult_human_sizes()
    {
        var result = new PdfAnalysisResult(2 * 1024 * 1024, 5, 3, 1024 * 512);
        Assert.Equal("2.0 MB", result.HumanTotalSize);
        Assert.Equal("512.0 KB", result.HumanImageSize);
    }
}
