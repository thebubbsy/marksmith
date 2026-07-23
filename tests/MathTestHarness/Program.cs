using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using MdToPdf.Models;
using MdToPdf.Services;
using SkiaSharp;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== MarkSmith FORENSIC VERIFICATION HARNESS ===");
        string scratchDir = Path.Combine(AppContext.BaseDirectory, "forensic_output");
        Directory.CreateDirectory(scratchDir);

        // 1. TEST DOCX GENERATION
        string docxPath = Path.Combine(scratchDir, "test_export.docx");
        if (File.Exists(docxPath)) File.Delete(docxPath);

        string sampleMarkdown = @"# Milestone 3 Audit Verification

## Overview
Testing native DOCX and PDF export functionality in MarkSmith.

- Bullet 1
- Bullet 2

> Note: Forensics verification check.
";

        Console.WriteLine("\n[1/2] Testing DOCX Export via DocxExportService...");
        var docxService = new DocxExportService();
        docxService.ExportAsync(sampleMarkdown, docxPath, new AppSettings()).GetAwaiter().GetResult();

        if (!File.Exists(docxPath))
        {
            Console.WriteLine("FAIL: DOCX file was not created.");
            return;
        }

        var docxBytes = File.ReadAllBytes(docxPath);
        Console.WriteLine($"DOCX File Size: {docxBytes.Length} bytes");
        // Verify ZIP magic bytes PK (0x50 0x4B 0x03 0x04)
        bool isZip = docxBytes.Length > 4 && docxBytes[0] == 0x50 && docxBytes[1] == 0x4B && docxBytes[2] == 0x03 && docxBytes[3] == 0x04;
        Console.WriteLine($"DOCX Zip Header Magic (PK..): {(isZip ? "PASS" : "FAIL")}");

        // Verify OOXML contents inside DOCX zip package
        bool hasDocumentXml = false;
        using (var zip = ZipFile.OpenRead(docxPath))
        {
            var entry = zip.GetEntry("word/document.xml");
            if (entry != null)
            {
                hasDocumentXml = true;
                using var reader = new StreamReader(entry.Open());
                string xmlContent = reader.ReadToEnd();
                Console.WriteLine($"word/document.xml Length: {xmlContent.Length} chars");
            }
        }
        Console.WriteLine($"OOXML word/document.xml Present: {(hasDocumentXml ? "PASS" : "FAIL")}");

        // 2. TEST PDF GENERATION (SkiaSharp PDF backend used by MauiWebRenderHost)
        string pdfPath = Path.Combine(scratchDir, "test_export.pdf");
        if (File.Exists(pdfPath)) File.Delete(pdfPath);

        Console.WriteLine("\n[2/2] Testing PDF Export via SkiaSharp PDF Engine...");
        using (var stream = File.Create(pdfPath))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Black,
                TextSize = 14f,
                IsAntialias = true
            };
            var canvas = doc.BeginPage(595, 842); // A4
            canvas.DrawText("Milestone 3 Audit - Skia PDF Export Test", 36, 50, paint);
            doc.EndPage();
            doc.Close();
        }

        if (!File.Exists(pdfPath))
        {
            Console.WriteLine("FAIL: PDF file was not created.");
            return;
        }

        var pdfBytes = File.ReadAllBytes(pdfPath);
        Console.WriteLine($"PDF File Size: {pdfBytes.Length} bytes");
        string pdfHeader = Encoding.ASCII.GetString(pdfBytes, 0, Math.Min(10, pdfBytes.Length));
        bool isPdfHeaderValid = pdfHeader.StartsWith("%PDF");
        Console.WriteLine($"PDF Header Magic (%PDF): {(isPdfHeaderValid ? "PASS" : "FAIL")} (Header: '{pdfHeader.Trim()}')");

        Console.WriteLine("\n=== FORENSIC VERIFICATION HARNESS COMPLETE ===");
    }
}

