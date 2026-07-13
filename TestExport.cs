using Markdig;
using System.IO;
using MdToPdf.Services;

class Program {
    static void Main() {
        var md = "| Endpoint | Example | Notes |\n|---|---|---|\n| POST /auth | {\"user\": \"kt\"} | 🔐 Rate-limited<br>10 req/min |";
        var bytes = DocxExportService.Export(md, new AppSettings(), new ThemeDefinition());
        File.WriteAllBytes("test_export.docx", bytes);
    }
}
