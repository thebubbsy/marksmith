using System.IO;
using System.IO.Compression;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// Throwaway recon: export the built-in sample document to DOCX and dump the raw OOXML parts
// (document.xml + styles.xml + numbering.xml) so we can learn the EXACT structure the forward
// engine emits, to build its inverse (ReverseImportService) against ground truth.
public class DocxRoundTripRecon
{
    private const string Sample = """
        # Quarterly Review — Sample Document

        This is a **sample** so you can try MarkSmith without hunting for a Markdown file.
        Restyle it on the right, then hit **Generate PDF** below.

        > [!TIP]
        > Everything here survives export: the table, the math, and the diagrams.

        ---

        ## Data Tables

        | Region | Revenue | Change |
        |--------|---------|--------|
        | APAC   | $4.2M   | +12%   |
        | EU     | $3.1M   | +5%    |
        | US     | $5.5M   | +9%    |

        ---

        ## Math

        Reserves follow $R = \sum_{i=1}^{n} p_i \cdot L_i$ — and in Word export this becomes a
        real, editable equation, not a picture.

        Block equations work too:
        $$
        \begin{bmatrix}
        1 & 2 & 3 \\
        4 & 5 & 6 \\
        7 & 8 & 9
        \end{bmatrix}
        $$

        ---

        ## Formatting & Code

        *Italic*, **Bold**, ***Bold Italic***, ~~Strikethrough~~, ==Highlight==, and `Inline code`.
        Subscript: H~2~O | Superscript: X^2^

        ```python
        def hello_world():
            print("Syntax highlighting works!")
        ```

        ---

        ## Task Lists

        - [x] Completed task
        - [ ] Incomplete task

        1. First ordered
        2. Second ordered

        - Bullet one
        - Bullet two

        A [link to tables](#data-tables) and some ***final*** text.
        """;

    [Fact]
    public async Task Dump_sample_docx_ooxml()
    {
        var outDir = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Directory.CreateDirectory(outDir);
        var docxPath = Path.Combine(outDir, "rt_recon.docx");

        await new DocxExportService().ExportAsync(Sample, docxPath, new AppSettings());

        using var zip = ZipFile.OpenRead(docxPath);
        foreach (var part in new[] { "word/document.xml", "word/styles.xml", "word/numbering.xml" })
        {
            var entry = zip.GetEntry(part);
            if (entry is null) continue;
            var dest = Path.Combine(outDir, "rt_recon_" + part.Replace("word/", "").Replace("/", "_"));
            using var s = entry.Open();
            using var fs = File.Create(dest);
            s.CopyTo(fs);
        }
    }
}
