using System.IO;
using MarkSmith.Models;
using MarkSmith.Services;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace MarkSmith.Core.Tests;

// The PDF counterpart of the DOCX round-trip: PdfSourceStore tucks the original Markdown into the
// PDF's Info dictionary (low-key, invisible to normal readers) so ReverseImportService can recover
// it byte-for-byte — Tier 1, same contract as MarksmithSourceStore for .docx.
public class PdfSourceStoreTests
{
    private static string MakePdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms_pdf_{Guid.NewGuid():N}.pdf");
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    [Fact]
    public void Apply_Then_Read_Returns_Exact_Markdown()
    {
        var path = MakePdf();
        try
        {
            const string md = "# Title\n\nHello **world** with ☺ emoji, (parens), \\backslash\\ and ```code```.";
            PdfSourceStore.Apply(path, md, "My Doc", new AppSettings { AuthorName = "Jane" });

            var r = PdfSourceStore.Read(path);
            Assert.NotNull(r);
            Assert.Equal(md, r!.Markdown);
            Assert.False(r.IsStale);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Apply_Sets_LowKey_Branding_Metadata()
    {
        var path = MakePdf();
        try
        {
            PdfSourceStore.Apply(path, "md", "Deck Title", new AppSettings { AuthorName = "Ada" });

            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal("Deck Title", doc.Info.Title);
            Assert.Equal("Ada", doc.Info.Author);
            Assert.Equal(ExportBranding.CreatedIn, doc.Info.Subject);
            Assert.Equal(ExportBranding.Tag, doc.Info.Creator);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_On_Plain_Pdf_Returns_Null()
    {
        var path = MakePdf();
        try { Assert.Null(PdfSourceStore.Read(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Oversized_Source_Is_Skipped_Without_Breaking_The_Pdf()
    {
        var path = MakePdf();
        try
        {
            var big = new string('x', PdfSourceStore.MaxSourceBytes + 1_000);
            PdfSourceStore.Apply(path, big, "Big", new AppSettings());

            Assert.Null(PdfSourceStore.Read(path)); // embed skipped — not corrupted
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            Assert.Equal(ExportBranding.Tag, doc.Info.Creator); // branding still applied
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Embedded_Source_Survives_Security_Encryption_RoundTrip()
    {
        var path = MakePdf();
        try
        {
            const string md = "# Protected\n\nBody text.";
            PdfSourceStore.Apply(path, md, "Sec", new AppSettings());
            var policy = new PdfSecurityPolicy
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                Permissions = PdfPermissions.All,
            };
            PdfSecurityService.ApplyToFile(path, policy);

            var r = PdfSourceStore.Read(path, "owner");
            Assert.NotNull(r);
            Assert.Equal(md, r!.Markdown);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_Flags_Stale_After_External_Modification()
    {
        var path = MakePdf();
        try
        {
            PdfSourceStore.Apply(path, "# Hi\n\nThere", "T", new AppSettings());

            // Simulate an external edit: re-save with a future modification stamp.
            using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
            {
                doc.Info.ModificationDate = DateTime.UtcNow.AddDays(1);
                using var ms = new MemoryStream();
                doc.Save(ms, false);
                File.WriteAllBytes(path, ms.ToArray());
            }

            var r = PdfSourceStore.Read(path);
            Assert.NotNull(r);
            Assert.True(r!.IsStale);
            Assert.Equal("# Hi\n\nThere", r.Markdown); // source still recoverable
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImportFromPdf_Returns_Embedded_Source_ByteForByte()
    {
        var path = MakePdf();
        try
        {
            const string md = "# From PDF\n\nRecovered exactly — *italics*, **bold**, `code`, and ☺ unicode.";
            PdfSourceStore.Apply(path, md, "PDF", new AppSettings());

            var result = new ReverseImportService().ImportFromPdf(path);
            Assert.Equal(ImportTier.EmbeddedSource, result.Tier);
            Assert.Equal(md, result.Markdown);
            Assert.False(result.IsStale);
        }
        finally { File.Delete(path); }
    }
}
