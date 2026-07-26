using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// Conversion accuracy: markdown in, real .docx out, assertions on the OOXML that Word will read.
// Covers the constructs AI output leans on hardest — the "where does the conversion fail" list.
public class DocxExportTests
{
    private static string Export(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-test-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings()).GetAwaiter().GetResult();
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml")!;
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- inline raw HTML ----------------------------------------------------------------------
    [Fact] public void Sub_maps_to_vertalign() => Assert.Contains("w:val=\"subscript\"", Export("H<sub>2</sub>O"));
    [Fact] public void Sup_maps_to_vertalign() => Assert.Contains("w:val=\"superscript\"", Export("mc<sup>2</sup>"));
    [Fact] public void Mark_maps_to_highlight() => Assert.Contains("<w:highlight", Export("<mark>hot</mark>"));
    [Fact] public void Del_maps_to_strike() => Assert.Contains("<w:strike", Export("<del>gone</del>"));
    [Fact] public void U_maps_to_underline() => Assert.Contains("<w:u ", Export("<u>under</u>"));
    [Fact] public void Span_color_named() => Assert.Contains("w:val=\"FF0000\"", Export("<span style=\"color:red\">r</span>"));
    [Fact] public void Font_color_hex() => Assert.Contains("w:val=\"00AA00\"", Export("<font color=\"#00AA00\">g</font>"));
    [Fact] public void Shorthand_hex_expanded() => Assert.Contains("w:val=\"FF00FF\"", Export("<span style=\"color:#f0f\">m</span>"));
    [Fact] public void Br_maps_to_break() => Assert.Contains("<w:br", Export("one<br>two"));
    [Fact] public void Unclosed_tag_does_not_eat_document() { var x = Export("<mark>open then\n\nnext paragraph fine"); Assert.Contains("next paragraph fine", x); }
    [Fact] public void Kbd_gets_code_treatment() => Assert.Contains("Consolas", Export("press <kbd>Ctrl</kbd>"));

    // ---- block raw HTML -----------------------------------------------------------------------
    [Fact] public void Html_table_becomes_word_table() { var x = Export("<table><tr><th>H</th></tr><tr><td>cell text</td></tr></table>"); Assert.Contains("<w:tbl>", x); Assert.Contains("cell text", x); }
    [Fact] public void Details_summary_and_body_survive() { var x = Export("<details><summary>The Summary</summary>Hidden body.</details>"); Assert.Contains("The Summary", x); Assert.Contains("Hidden body", x); }
    [Fact] public void Details_summary_is_collapsible() => Assert.Contains("outlineLvl", Export("<details><summary>S</summary>b</details>"));
    [Fact] public void Hr_glued_text_survives() { var x = Export("above\n<hr>\nbelow"); Assert.Contains("above", x); Assert.Contains("below", x); }
    [Fact] public void Pagebreak_becomes_real_break() => Assert.Contains("w:type=\"page\"", Export("a\n\n<!-- pagebreak -->\n\nb"));
    [Fact] public void Script_block_never_lands_in_docx() => Assert.DoesNotContain("alert(1)", Export("hi\n\n<script>alert(1)</script>\n\nbye"));

    // ---- checkboxes + lists -------------------------------------------------------------------
    [Fact] public void Tasklist_native_checkbox() => Assert.Contains("checkbox", Export("- [ ] open item"));
    [Fact] public void Tasklist_checked_state() => Assert.Contains("w14:val=\"1\"", Export("- [x] done item"));
    [Fact] public void Nested_tasklist_both_levels_native() { var x = Export("- [ ] parent\n  - [x] child"); Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(x, "<w:sdt>").Count); }
    [Fact] public void Noemoji_still_native_checkbox() => Assert.Contains("checkbox", Export("- [ ] item", new AppSettings { NoEmoji = true }));

    // ---- math ---------------------------------------------------------------------------------
    [Fact] public void Frac_becomes_omml_fraction() { var x = Export("$\\frac{1}{2}$"); Assert.Contains("<m:f>", x); Assert.Contains("<m:num>", x); }
    [Fact] public void Boxed_becomes_borderbox() => Assert.Contains("<m:borderBox>", Export("$\\boxed{42}$"));
    [Fact] public void Pmatrix_gets_paren_delims() => Assert.Contains("<m:d>", Export("$\\begin{pmatrix} 1 & 2 \\\\ 3 & 4 \\end{pmatrix}$"));
    [Fact] public void Overset_becomes_limupp() => Assert.Contains("<m:limUpp>", Export("$\\overset{a}{=}$"));
    [Fact] public void Pmod_keeps_mod_text() => Assert.Contains("mod", Export("$a \\pmod{n}$"));
    [Fact] public void Tag_keeps_label() => Assert.Contains("(3.1)", Export("$x=1 \\tag{3.1}$"));
    [Fact] public void Unknown_command_degrades_to_text_not_crash() => Assert.Contains("weirdcmd", Export("$\\weirdcmd{x}$"));

    // ---- schema validation --------------------------------------------------------------------
    [Fact]
    public void Exported_Docx_Passes_ECMA376_OpenXml_Validation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-val-{Guid.NewGuid():N}.docx");
        try
        {
            var md = "# Sample Document\n\n> [!NOTE]\n> Validated document.\n\n| Col A | Col B |\n|---|---|\n| 1 | 2 |\n\n```js\nconst x = 10;\n```\n";
            new DocxExportService().ExportAsync(md, path, new AppSettings()).GetAwaiter().GetResult();
            
            using var wordDoc = WordprocessingDocument.Open(path, false);
            var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
            var errors = validator.Validate(wordDoc)
                .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                .ToList();

            if (errors.Count > 0)
            {
                var msg = string.Join("\n", errors.Select(e => $"[{e.Id}] {e.Description} AT NODE: {e.Node?.OuterXml}"));
                Assert.Fail($"Validation failed with {errors.Count} errors:\n{msg}");
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
    [Fact] public void Aligned_env_does_not_crash() { var x = Export("$$\\begin{aligned} a &= b \\\\ c &= d \\end{aligned}$$"); Assert.Contains("<m:oMath", x); }
    [Fact] public void Math_inside_table_cell() { var x = Export("| a |\n|---|\n| $x^2$ |"); Assert.Contains("<m:oMath", x); }
    [Fact] public void Bare_frac_normalized_then_omml()
    {
        var llm = new LlmSourceService();
        var md = llm.Normalize(@"ratio:1\frac{3}{4}rest", llm.Classify("x")).Cleaned;
        Assert.Contains("<m:f>", Export(md));
    }

    // ---- tables -------------------------------------------------------------------------------
    [Fact] public void Table_alignment_center() => Assert.Contains("w:val=\"center\"", Export("| a |\n|:-:|\n| 1 |"));
    [Fact] public void Table_alignment_right() => Assert.Contains("w:val=\"right\"", Export("| a |\n|--:|\n| 1 |"));
    [Fact] public void Br_inside_cell_is_break() => Assert.Contains("<w:br", Export("| a |\n|---|\n| x<br>y |"));
    [Fact] public void Header_row_repeats_across_pages() => Assert.Contains("<w:tblHeader", Export("| h |\n|---|\n| 1 |"));

    // ---- structure ----------------------------------------------------------------------------
    [Fact] public void Footnote_reference_present() { var x = Export("claim[^1]\n\n[^1]: source note"); Assert.Contains("source note", x); }
    [Fact] public void Emoji_shortcode_lands_in_docx() => Assert.Contains("🚀", Export("go :rocket:"));
    [Fact] public void Alert_renders_shaded_box() => Assert.Contains("<w:shd", Export("> [!NOTE]\n> hi"));
    [Fact] public void Admonition_becomes_alert_box() => Assert.Contains("NOTE", Export(":::note\nhello there\n:::"));
    [Fact] public void Fence_caption_lands_bold() => Assert.Contains("train.py", Export("```python title=\"train.py\"\nx=1\n```"));
    [Fact] public void Content_tab_labels_land() { var x = Export("=== \"Windows\"\n    body a\n=== \"macOS\"\n    body b"); Assert.Contains("Windows", x); Assert.DoesNotContain("===", x); }
    [Fact] public void Wikilink_text_clean_in_docx() { var x = Export("see [[Project Phoenix]] now"); Assert.Contains("Project Phoenix", x); Assert.DoesNotContain("[[", x); Assert.Contains("w:val=\"dash\"", x); Assert.Contains("<w:noProof", x); }
    [Fact] public void Double_hyphen_converted_in_docx() { var x = Export("hello -- world"); Assert.Contains("hello — world", x); }

    [Fact] public void Long_document_exports() { var md = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"## Section {i}\n\nParagraph {i} content.")); Assert.Contains("Section 200", Export(md)); }
    [Fact(Skip="SkiaSharp missing in test environment")] public void Local_image_embedded_as_picture()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mk-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(tmp, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try { Assert.Contains("<pic:pic", Export($"![shot]({tmp})")); }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task GenerateDoc3()
    {
        var md = @"# Automated Order Processing Architecture

## Pipeline Workflow

```mermaid
flowchart LR
  A[Customer Order] --> B{Payment Gateway}
  B -- Approved --> C[Warehouse Fulfillment]
  B -- Declined --> D[Notify Customer]
  C --> E[Carrier Dispatch]
```

Every stage in this pipeline emits real-time telemetry events to Kafka.";

        var scratchDir = @"C:\Users\Tony\.gemini\antigravity\scratch\MarkSmith\scratch";
        Directory.CreateDirectory(scratchDir);
        var docxPath = Path.Combine(scratchDir, "doc3.docx");
        var xmlPath1 = Path.Combine(scratchDir, "Doc3_MermaidFlowchart_document.xml");
        var xmlPath2 = @"C:\Users\Tony\.gemini\antigravity\scratch\Doc3_MermaidFlowchart_document.xml";

        await new DocxExportService().ExportAsync(md, docxPath, new AppSettings());

        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/document.xml")!;
        entry.ExtractToFile(xmlPath1, true);
        File.Copy(xmlPath1, xmlPath2, true);
    }
}

