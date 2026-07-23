using System;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml;

class Program
{
    static void Main(string[] args)
    {
        var pPr = new ParagraphProperties();
        pPr.WordWrap = new WordWrap();
        pPr.ParagraphBorders = new ParagraphBorders();
        pPr.Shading = new Shading();
        pPr.KeepLines = new KeepLines();
        foreach(var c in pPr.ChildElements) {
            Console.WriteLine(c.GetType().Name);
        }
    }
}
