using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Block content inside pipe-table cells. GFM makes cells inline-only, so an alert or list written
/// into a cell used to come out as the literal characters "&gt; [!WARNING]". Both pipelines must
/// recover it, and both must leave ordinary cells exactly as they were.
/// </summary>
public class TableCellBlocksTests
{
    // ---- the shared decision ------------------------------------------------------------------

    [Theory]
    [InlineData("> [!WARNING] Mind the gap!")]           // single-line alert
    [InlineData("> [!NOTE]<br>> Second line.")]          // br-joined alert
    [InlineData("- one<br>- two")]                       // br-joined list
    [InlineData("1. first<br>2. second")]                // ordered list
    public void IsBlockCell_Accepts_Unambiguous_Block_Content(string cell)
        => Assert.True(TableCellBlocks.IsBlockCell(cell));

    [Theory]
    [InlineData("just text")]
    [InlineData("a hyphen - mid sentence")]
    [InlineData("1. not a list without a break")]        // one line, no alert marker
    [InlineData("")]
    [InlineData(null)]
    public void IsBlockCell_Leaves_Ordinary_Cells_Alone(string? cell)
        => Assert.False(TableCellBlocks.IsBlockCell(cell));

    [Theory]
    [InlineData("`- one<br>- two`")]                     // whole cell is a code span
    [InlineData("`> [!TIP]<br>> text`")]
    public void IsBlockCell_Ignores_Content_Inside_Code_Spans(string cell)
        => Assert.False(TableCellBlocks.IsBlockCell(cell));

    [Fact]
    public void TryGetBlockMarkdown_Splits_A_Single_Line_Alert_Onto_Two_Lines()
    {
        // GitHub requires the marker to stand alone; a cell has no newline to give it.
        var md = TableCellBlocks.TryGetBlockMarkdown("> [!WARNING] Mind the gap!");
        Assert.NotNull(md);
        Assert.Contains("> [!WARNING]\n", md);
        Assert.Contains("> Mind the gap!", md);
    }

    [Fact]
    public void TryGetBlockMarkdown_Returns_Null_For_Inline_Cells()
        => Assert.Null(TableCellBlocks.TryGetBlockMarkdown("plain text"));

    // ---- HTML preview pipeline ----------------------------------------------------------------

    private static string RenderHtml(string markdown) =>
        new MarkdownHtmlService().Render(markdown, new AppSettings(),
            AppServices.Themes.GetOrDefault("GitHub Light"));

    private const string AlertTable = """
        | Case | Result |
        | :--- | :--- |
        | alert | > [!WARNING] Mind the gap! |
        | list | - one<br>- two |
        | doc | `> [!TIP] shown as syntax` |
        | plain | ordinary text |
        """;

    [Fact]
    public void Html_Renders_An_Alert_Written_Into_A_Cell()
    {
        var html = RenderHtml(AlertTable);
        Assert.Contains("markdown-alert-warning", html);
        Assert.DoesNotContain("&gt; [!WARNING] Mind the gap!", html);
    }

    [Fact]
    public void Html_Renders_A_BrJoined_List_As_A_Real_List()
    {
        var html = RenderHtml(AlertTable);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("<li>two</li>", html);
    }

    [Fact]
    public void Html_Leaves_Code_Span_Cells_And_Plain_Cells_Untouched()
    {
        var html = RenderHtml(AlertTable);
        Assert.Contains("<code>&gt; [!TIP] shown as syntax</code>", html);
        Assert.Contains("ordinary text", html);
    }

    [Fact]
    public void Html_Preserves_Escaped_Pipes_In_A_Rewritten_Row()
    {
        // The row is rebuilt to carry the placeholder, so its other cells must survive the split
        // and rejoin byte-for-byte in meaning.
        var html = RenderHtml("""
            | Escaped | Alert |
            | :--- | :--- |
            | a \| b | > [!TIP] kept |
            """);
        Assert.Contains("a | b", html);
        Assert.Contains("markdown-alert-tip", html);
    }

    // ---- DOCX pipeline ------------------------------------------------------------------------

    private static string ExportDocumentXml(string markdown)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms-cellblocks-{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(markdown, path, new AppSettings())
                .GetAwaiter().GetResult();
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            using var reader = new StreamReader(
                zip.GetEntry("word/document.xml")!.Open());
            return reader.ReadToEnd();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Docx_Renders_A_Cell_Alert_As_A_Native_Callout_Not_Literal_Text()
    {
        var xml = ExportDocumentXml(AlertTable);
        Assert.DoesNotContain("[!WARNING]", xml);
        // The callout is a shaded single-cell table nested inside the outer table's cell.
        Assert.True(xml.Split("<w:tbl>").Length - 1 >= 2,
            "expected a nested callout table inside the outer table");
    }

    [Fact]
    public void Docx_Renders_A_BrJoined_Cell_List_With_Real_Numbering()
    {
        var xml = ExportDocumentXml(AlertTable);
        Assert.Contains("<w:numId", xml);
        Assert.DoesNotContain("&lt;br&gt;", xml);
    }

    [Fact]
    public void Docx_Keeps_Code_Span_Cells_As_Literal_Syntax()
    {
        var xml = ExportDocumentXml(AlertTable);
        Assert.Contains("[!TIP] shown as syntax", xml);
    }
}
