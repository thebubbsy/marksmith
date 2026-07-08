using System;
using System.IO;
using System.Collections.Generic;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using MdToPdf.Services;

class Program
{
    static void Main(string[] args)
    {
        var equations = new List<string>
        {
            @"\frac{-b \pm \sqrt{b^2 - 4ac}}{2a}", // Quadratic
            @"\sum_{n=1}^{\infty} \frac{1}{n^2} = \frac{\pi^2}{6}", // Summation
            @"\int_0^\infty e^{-x^2} dx = \frac{\sqrt{\pi}}{2}", // Integral
            @"\lim_{x \to 0} \frac{\sin x}{x} = 1", // Limit under (Current codebase does NOT handle under properly)
            @"\begin{bmatrix} a & b \\ c & d \end{bmatrix}", // Matrix (Current codebase breaks here)
            @"\binom{n}{k} = \frac{n!}{k!(n-k)!}", // Binomial (Current codebase breaks here)
            @"f(x) = \begin{cases} x & \text{if } x > 0 \\ 0 & \text{otherwise} \end{cases}", // Cases (Current codebase breaks here)
            @"\underbrace{a + \dots + a}_{n\text{ times}}" // Underbrace (Current codebase breaks here)
        };

        string docPath = "test.docx";
        if (File.Exists(docPath)) File.Delete(docPath);

        using (WordprocessingDocument wordDoc = WordprocessingDocument.Create(docPath, WordprocessingDocumentType.Document))
        {
            MainDocumentPart mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            foreach (var eq in equations)
            {
                var p = new Paragraph(
                    new ParagraphProperties(new Justification { Val = JustificationValues.Center })
                );

                var oMath = LatexToOmml.Build(eq);
                var oMathPara = new M.Paragraph(oMath);
                p.Append(oMathPara);
                body.Append(p);
                
                // Add a blank paragraph for spacing
                body.Append(new Paragraph(new Run(new Text(" "))));
            }
        }

        Console.WriteLine($"Generated {docPath}");

        var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
        var errors = validator.Validate(WordprocessingDocument.Open(docPath, false));
        int errCount = 0;
        foreach (var error in errors)
        {
            Console.WriteLine($"- ERROR: {error.Description} (Node: {error.Node?.LocalName}, Path: {error.Path?.XPath})");
            errCount++;
        }

        if (errCount == 0)
        {
            Console.WriteLine("OpenXML Validation: PASSED (0 errors).");
        }
        else
        {
            Console.WriteLine($"OpenXML Validation: FAILED ({errCount} errors).");
        }
    }
}
