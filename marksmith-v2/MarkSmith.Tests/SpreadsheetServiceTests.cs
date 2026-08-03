using System.IO;
using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Unit coverage for SpreadsheetService (D4) — CSV parsing, XLSX read/write round-trip,
/// Markdown table generation, and Markdown table extraction with cursor targeting.
/// </summary>
public class SpreadsheetServiceTests
{
    // ---- CSV parsing ---------------------------------------------------------------------------

    [Fact]
    public void ParseCsv_comma_delimited_basic()
    {
        var csv = "Name,Age,City\nAlice,30,London\nBob,25,Paris";
        var model = SpreadsheetService.ParseCsv(csv);

        Assert.Equal(new[] { "Name", "Age", "City" }, model.Headers);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(new[] { "Alice", "30", "London" }, model.Rows[0]);
        Assert.Equal(new[] { "Bob", "25", "Paris" }, model.Rows[1]);
    }

    [Fact]
    public void ParseCsv_tab_delimited()
    {
        var csv = "Col1\tCol2\nA\tB";
        var model = SpreadsheetService.ParseCsv(csv);

        Assert.Equal(new[] { "Col1", "Col2" }, model.Headers);
        Assert.Equal(new[] { "A", "B" }, model.Rows[0]);
    }

    [Fact]
    public void ParseCsv_semicolon_delimited()
    {
        var csv = "X;Y;Z\n1;2;3";
        var model = SpreadsheetService.ParseCsv(csv);

        Assert.Equal(new[] { "X", "Y", "Z" }, model.Headers);
        Assert.Equal(new[] { "1", "2", "3" }, model.Rows[0]);
    }

    [Fact]
    public void ParseCsv_quoted_fields_with_embedded_commas_and_newlines()
    {
        var csv = "Name,Description\n\"Smith, John\",\"Line 1\nLine 2\"\nPlain,Value";
        var model = SpreadsheetService.ParseCsv(csv);

        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("Smith, John", model.Rows[0][0]);
        Assert.Equal("Line 1\nLine 2", model.Rows[0][1]);
        Assert.Equal("Plain", model.Rows[1][0]);
    }

    [Fact]
    public void ParseCsv_escaped_quotes()
    {
        var csv = "Text\n\"She said \"\"hello\"\"\"";
        var model = SpreadsheetService.ParseCsv(csv);

        Assert.Equal("She said \"hello\"", model.Rows[0][0]);
    }

    [Fact]
    public void ParseCsv_empty_input()
    {
        var model = SpreadsheetService.ParseCsv("");
        Assert.Empty(model.Headers);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void ParseCsv_header_only()
    {
        var model = SpreadsheetService.ParseCsv("A,B,C");
        Assert.Equal(new[] { "A", "B", "C" }, model.Headers);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void ParseCsv_trailing_newline_ignored()
    {
        var csv = "H1,H2\nV1,V2\n";
        var model = SpreadsheetService.ParseCsv(csv);
        Assert.Single(model.Rows);
    }

    // ---- CSV delimiter detection ---------------------------------------------------------------

    [Theory]
    [InlineData("a,b,c", ',')]
    [InlineData("a\tb\tc", '\t')]
    [InlineData("a;b;c", ';')]
    public void DetectDelimiter_identifies_correctly(string line, char expected)
    {
        Assert.Equal(expected, SpreadsheetService.DetectDelimiter(line));
    }

    // ---- CSV writing ---------------------------------------------------------------------------

    [Fact]
    public void WriteCsv_round_trip_preserves_data()
    {
        var model = new TableModel
        {
            Headers = { "Name", "Value" },
            Rows = { new() { "hello, world", "42" }, new() { "say \"hi\"", "0" } }
        };

        var csv = SpreadsheetService.WriteCsv(model);
        var parsed = SpreadsheetService.ParseCsv(csv);

        Assert.Equal(model.Headers, parsed.Headers);
        Assert.Equal(model.Rows[0], parsed.Rows[0]);
        Assert.Equal(model.Rows[1], parsed.Rows[1]);
    }

    [Fact]
    public void WriteCsv_quotes_only_when_needed()
    {
        var model = new TableModel
        {
            Headers = { "Plain", "NeedsQuote" },
            Rows = { new() { "abc", "has,comma" } }
        };

        var csv = SpreadsheetService.WriteCsv(model);
        Assert.Contains("abc", csv);
        Assert.Contains("\"has,comma\"", csv);
        Assert.DoesNotContain("\"abc\"", csv);
    }

    // ---- XLSX round-trip -----------------------------------------------------------------------

    [Fact]
    public void Xlsx_write_then_read_round_trip()
    {
        var model = new TableModel
        {
            Headers = { "Product", "Price", "InStock" },
            Rows =
            {
                new() { "Widget", "9.99", "100" },
                new() { "Gadget", "24.50", "5" },
            }
        };

        using var ms = new MemoryStream();
        SpreadsheetService.WriteXlsx(new[] { ("Data", model) }, ms);
        var bytes = ms.ToArray();
        Assert.True(bytes.Length > 100, $"WriteXlsx produced only {bytes.Length} bytes");

        using var readStream = new MemoryStream(bytes);
        var sheets = SpreadsheetService.ReadXlsx(readStream);
        Assert.Single(sheets);
        Assert.Equal("Data", sheets[0].Name);

        var result = sheets[0].Model;
        Assert.Equal(new[] { "Product", "Price", "InStock" }, result.Headers);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Widget", result.Rows[0][0]);
        Assert.Equal("9.99", result.Rows[0][1]);
        Assert.Equal("Gadget", result.Rows[1][0]);
    }

    [Fact]
    public void Xlsx_multiple_sheets()
    {
        var t1 = new TableModel { Headers = { "A" }, Rows = { new() { "1" } } };
        var t2 = new TableModel { Headers = { "B" }, Rows = { new() { "2" } } };

        using var ms = new MemoryStream();
        SpreadsheetService.WriteXlsx(new[] { ("First", t1), ("Second", t2) }, ms);
        ms.Position = 0;

        var sheets = SpreadsheetService.ReadXlsx(ms);
        Assert.Equal(2, sheets.Count);
        Assert.Equal("First", sheets[0].Name);
        Assert.Equal("Second", sheets[1].Name);
        Assert.Equal("A", sheets[0].Model.Headers[0]);
        Assert.Equal("B", sheets[1].Model.Headers[0]);
    }

    [Fact]
    public void Xlsx_empty_cells_preserved()
    {
        var model = new TableModel
        {
            Headers = { "X", "Y", "Z" },
            Rows = { new() { "a", "", "c" } }
        };

        using var ms = new MemoryStream();
        SpreadsheetService.WriteXlsx(new[] { ("S", model) }, ms);
        ms.Position = 0;

        var result = SpreadsheetService.ReadXlsx(ms)[0].Model;
        Assert.Equal("", result.Rows[0][1]);
    }

    // ---- Markdown table generation -------------------------------------------------------------

    [Fact]
    public void ToMarkdownTable_basic()
    {
        var model = new TableModel
        {
            Headers = { "Name", "Age" },
            Rows = { new() { "Alice", "30" }, new() { "Bob", "25" } }
        };

        var md = SpreadsheetService.ToMarkdownTable(model);

        Assert.Contains("| Name | Age |", md);
        Assert.Contains("| --- | --- |", md);
        Assert.Contains("| Alice | 30 |", md);
        Assert.Contains("| Bob | 25 |", md);
    }

    [Fact]
    public void ToMarkdownTable_escapes_pipes()
    {
        var model = new TableModel
        {
            Headers = { "Expr" },
            Rows = { new() { "a | b" } }
        };

        var md = SpreadsheetService.ToMarkdownTable(model);
        Assert.Contains("a \\| b", md);
    }

    [Fact]
    public void ToMarkdownTable_empty_model_returns_empty()
    {
        Assert.Equal("", SpreadsheetService.ToMarkdownTable(new TableModel()));
    }

    [Fact]
    public void ToMarkdownTable_headerless_uses_generic_columns()
    {
        var model = new TableModel { Rows = { new() { "x", "y" } } };
        var md = SpreadsheetService.ToMarkdownTable(model);
        Assert.Contains("Column 1", md);
        Assert.Contains("Column 2", md);
    }

    // ---- Markdown table extraction -------------------------------------------------------------

    [Fact]
    public void ExtractTables_single_table()
    {
        var md = "# Title\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\nAfter.";
        var tables = SpreadsheetService.ExtractTables(md);

        Assert.Single(tables);
        Assert.Equal(new[] { "A", "B" }, tables[0].Model.Headers);
        Assert.Single(tables[0].Model.Rows);
        Assert.Equal("Title", tables[0].NearestHeading);
    }

    [Fact]
    public void ExtractTables_multiple_tables_with_headings()
    {
        var md = "## First\n\n| X |\n| --- |\n| a |\n\n## Second\n\n| Y |\n| --- |\n| b |\n";
        var tables = SpreadsheetService.ExtractTables(md);

        Assert.Equal(2, tables.Count);
        Assert.Equal("First", tables[0].NearestHeading);
        Assert.Equal("Second", tables[1].NearestHeading);
    }

    [Fact]
    public void ExtractTables_no_tables()
    {
        var md = "# Hello\n\nJust some text.\n";
        Assert.Empty(SpreadsheetService.ExtractTables(md));
    }

    [Fact]
    public void FindTableAtLine_cursor_inside_table()
    {
        var md = "Intro\n\n| H1 | H2 |\n| --- | --- |\n| v1 | v2 |\n\nOutro";
        // Line 2 = header row, line 3 = delimiter, line 4 = data
        var found = SpreadsheetService.FindTableAtLine(md, 3);
        Assert.NotNull(found);
        Assert.Equal(new[] { "H1", "H2" }, found!.Model.Headers);
    }

    [Fact]
    public void FindTableAtLine_cursor_outside_table()
    {
        var md = "Intro\n\n| H1 |\n| --- |\n| v1 |\n\nOutro";
        var found = SpreadsheetService.FindTableAtLine(md, 0); // "Intro" line
        Assert.Null(found);
    }

    // ---- Edge cases ----------------------------------------------------------------------------

    [Fact]
    public void ParseCsv_ragged_rows_padded_on_read()
    {
        // CSV with uneven columns — the model preserves them as-is (padding is a display concern).
        var csv = "A,B,C\n1,2\n3,4,5,6";
        var model = SpreadsheetService.ParseCsv(csv);

        Assert.Equal(3, model.Headers.Count);
        Assert.Equal(2, model.Rows[0].Count); // short row stays short
        Assert.Equal(4, model.Rows[1].Count); // long row stays long
    }

    [Fact]
    public void Xlsx_sheet_name_sanitized()
    {
        var model = new TableModel { Headers = { "A" }, Rows = { new() { "1" } } };
        using var ms = new MemoryStream();
        // Sheet name with illegal characters
        SpreadsheetService.WriteXlsx(new[] { ("My[Sheet]:*?", model) }, ms);
        ms.Position = 0;

        var sheets = SpreadsheetService.ReadXlsx(ms);
        Assert.DoesNotContain("[", sheets[0].Name);
        Assert.DoesNotContain("]", sheets[0].Name);
        Assert.DoesNotContain(":", sheets[0].Name);
    }

    [Fact]
    public void Csv_to_markdown_to_extraction_round_trip()
    {
        var csv = "Name,Score\nAlice,95\nBob,87";
        var model = SpreadsheetService.ParseCsv(csv);
        var md = SpreadsheetService.ToMarkdownTable(model);

        var extracted = SpreadsheetService.ExtractTables(md);
        Assert.Single(extracted);
        Assert.Equal(model.Headers, extracted[0].Model.Headers);
        Assert.Equal(model.Rows.Count, extracted[0].Model.Rows.Count);
        Assert.Equal("Alice", extracted[0].Model.Rows[0][0]);
        Assert.Equal("95", extracted[0].Model.Rows[0][1]);
    }
}
