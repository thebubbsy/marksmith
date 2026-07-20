using System;
using System.IO;
using MdToPdf.Services;
using MdToPdf.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace TestMermaid
{
    class Program
    {
        static void Main(string[] args)
        {
            string md = "graph TD\n    A --> B;";
            Console.WriteLine("Testing full docx save with A --> B");
            ThemeDefinition theme = new ThemeDefinition("Test", "#FFFFFF", "#000000", "#000000", "#CCCCCC", "#DDDDDD", "#0000FF", "#00FF00", "#333333");
            
            try {
                bool result = MermaidDocxRenderer.TryRender(md, theme, 1, out Paragraph paragraph, out bool oversized);
                Console.WriteLine($"TryRender returned {result}");
                
                using (var doc = WordprocessingDocument.Create("test.docx", WordprocessingDocumentType.Document))
                {
                    MainDocumentPart mainPart = doc.AddMainDocumentPart();
                    mainPart.Document = new Document(new Body(paragraph));
                    doc.Save();
                }
                Console.WriteLine("Saved successfully!");
            } catch (Exception ex) {
                Console.WriteLine($"THREW: {ex}");
            }
        }
    }
}
