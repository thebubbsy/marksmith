using System;
using System.IO;
using System.IO.Compression;
using MdToPdf.Models;
using MdToPdf.Services;

class Program
{
    static void Main(string[] args)
    {
        string markdownContent = @"# Quantum Harmonic Oscillator Specification

## 1. Energy Quantization

The energy eigenvalues for a one-dimensional quantum harmonic oscillator are given by:

$$E_n = \hbar \omega \left(n + \frac{1}{2}\right)$$

where $n \in \mathbb{N}_0$ represents the quantum number and $\hbar$ is the reduced Planck constant.

## 2. Wavefunction Normalization

The spatial probability density follows:

$$\psi_n(x) = \frac{1}{\sqrt{2^n n!}} \left(\frac{m\omega}{\pi \hbar}\right)^{1/4} e^{-\frac{m\omega x^2}{2\hbar}} H_n\left(\sqrt{\frac{m\omega}{\hbar}} x\right)$$

Use `qho_solver.py` with `--state=3` to compute wavefunctions.";

        string scratchDir = @"C:\Users\Tony\.gemini\antigravity\scratch\marksmith\scratch";
        Directory.CreateDirectory(scratchDir);

        string brainScratchDir = @"C:\Users\Tony\.gemini\antigravity\brain\dfc6809f-97ad-4066-9c7d-011bf42aa935\scratch";
        Directory.CreateDirectory(brainScratchDir);

        string docxPath = Path.Combine(scratchDir, "doc2.docx");
        string xmlPath = Path.Combine(scratchDir, "Doc2_EngineeringMath_document.xml");
        string brainXmlPath = Path.Combine(brainScratchDir, "Doc2_EngineeringMath_document.xml");

        if (File.Exists(docxPath)) File.Delete(docxPath);
        if (File.Exists(xmlPath)) File.Delete(xmlPath);
        if (File.Exists(brainXmlPath)) File.Delete(brainXmlPath);

        Console.WriteLine("Exporting Markdown to docx via DocxExportService...");
        var exportService = new DocxExportService();
        exportService.ExportAsync(markdownContent, docxPath, new AppSettings()).GetAwaiter().GetResult();
        Console.WriteLine($"Exported docx to: {docxPath}");

        Console.WriteLine("Extracting word/document.xml...");
        using (var zip = ZipFile.OpenRead(docxPath))
        {
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null)
            {
                throw new FileNotFoundException("word/document.xml entry not found in zip!");
            }
            entry.ExtractToFile(xmlPath, overwrite: true);
            entry.ExtractToFile(brainXmlPath, overwrite: true);
        }

        Console.WriteLine($"Extracted XML to: {xmlPath}");
        Console.WriteLine($"Copied XML to: {brainXmlPath}");
    }
}
