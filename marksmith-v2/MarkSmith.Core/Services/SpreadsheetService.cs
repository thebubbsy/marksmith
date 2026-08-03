using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using S = DocumentFormat.OpenXml.Spreadsheet;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace MarkSmith.Services;

/// <summary>
/// A spreadsheet cell grid — the intermediate model shared by every import/export path.
/// Headers may be empty (headerless CSV); Rows never includes the header.
/// </summary>
public sealed class TableModel
{
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public int ColumnCount => Math.Max(Headers.Count, Rows.Count > 0 ? Rows.Max(r => r.Count) : 0);
}

/// <summary>
/// One table found in a Markdown document, with its source position (0-based line span) so the
/// UI can match "the table under the cursor" to an extracted table.
/// </summary>
public sealed record ExtractedTable(TableModel Model, int StartLine, int EndLine, string? NearestHeading);

/// <summary>
/// Bidirectional spreadsheet ↔ Markdown table conversion (D4). Pure functions, no UI dependency.
/// Import: CSV / XLSX → TableModel → Markdown pipe table.
/// Export: Markdown → TableModel → native XLSX workbook (or CSV text).
/// Uses DocumentFormat.OpenXml (already referenced) for native .xlsx read/write — zero new packages.
/// </summary>
public static class SpreadsheetService
{
    // Import clamp: very large sheets would make the editor sluggish. The user gets a toast if
    // truncated (the UI layer checks Rows.Count == MaxImportRows).
    public const int MaxImportRows = 500;
    public const int MaxImportCols = 50;

    // ---- CSV → TableModel -----------------------------------------------------------------------

    /// <summary>
    /// Parses CSV text into a TableModel. Auto-detects the delimiter (comma, tab, semicolon) from
    /// the first line. Handles RFC-4180 quoting (embedded delimiters, newlines, doubled quotes).
    /// The first row is treated as the header.
    /// </summary>
    public static TableModel ParseCsv(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TableModel();

        var delimiter = DetectDelimiter(text);
        var records = ParseCsvRecords(text, delimiter);
        if (records.Count == 0) return new TableModel();

        var model = new TableModel { Headers = ClampCols(records[0]) };
        for (int i = 1; i < records.Count && model.Rows.Count < MaxImportRows; i++)
            model.Rows.Add(ClampCols(records[i]));
        return model;
    }

    /// <summary>Detects the most likely delimiter by counting occurrences in the first line.</summary>
    public static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n')[0];
        int commas = firstLine.Count(c => c == ',');
        int tabs = firstLine.Count(c => c == '\t');
        int semicolons = firstLine.Count(c => c == ';');
        if (tabs > commas && tabs >= semicolons) return '\t';
        if (semicolons > commas) return ';';
        return ',';
    }

    // RFC-4180 state-machine parser: handles quoted fields with embedded delimiters, newlines, and
    // escaped quotes (""). Returns a list of records (each a list of field strings).
    private static List<List<string>> ParseCsvRecords(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                }
                else if (c == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                    i++;
                }
                else if (c == '\r')
                {
                    // \r\n or lone \r — both end the record
                    fields.Add(sb.ToString());
                    sb.Clear();
                    records.Add(fields);
                    fields = new List<string>();
                    i++;
                    if (i < text.Length && text[i] == '\n') i++;
                }
                else if (c == '\n')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                    records.Add(fields);
                    fields = new List<string>();
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
        }

        // Flush the last field/record (file may not end with a newline)
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            records.Add(fields);
        }

        // Drop trailing empty records (e.g. from a trailing newline)
        while (records.Count > 0 && records[^1].All(f => f.Length == 0))
            records.RemoveAt(records.Count - 1);

        return records;
    }

    // ---- XLSX → TableModel ----------------------------------------------------------------------

    /// <summary>
    /// Reads all worksheets from an .xlsx stream. Returns (sheetName, TableModel) per sheet.
    /// Cell values are resolved from shared strings and inline strings; formulas yield their cached
    /// result value. The first row of each sheet is treated as the header.
    /// </summary>
    public static List<(string Name, TableModel Model)> ReadXlsx(Stream stream)
    {
        var results = new List<(string, TableModel)>();
        if (stream.CanSeek) stream.Position = 0;
        using var doc = SpreadsheetDocument.Open(stream, false);
        var workbookPart = doc.WorkbookPart;
        if (workbookPart?.Workbook is null) return results;

        var sharedStrings = LoadSharedStrings(workbookPart);

        // OpenXml 3.x: Elements<S.Sheet>() may fail to match deserialized sheet elements.
        // Navigate by local-name to robustly extract sheet metadata.
        const string rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        foreach (var wbChild in workbookPart.Workbook.ChildElements)
        {
            if (wbChild.LocalName != "sheets") continue;
            foreach (var sheetEl in wbChild.ChildElements)
            {
                if (sheetEl.LocalName != "sheet") continue;
                var name = sheetEl.GetAttribute("name", "").Value;
                if (string.IsNullOrEmpty(name)) name = "Sheet";
                var relId = sheetEl.GetAttribute("id", rNs).Value;
                if (string.IsNullOrEmpty(relId)) continue;
                var part = (WorksheetPart)workbookPart.GetPartById(relId);
                var model = ReadWorksheet(part, sharedStrings);
                results.Add((name, model));
            }
        }

        return results;
    }

    private static TableModel ReadWorksheet(WorksheetPart part, List<string> sharedStrings)
    {
        var model = new TableModel();
        var sheetData = part.Worksheet.Elements<S.SheetData>().FirstOrDefault();
        if (sheetData is null) return model;

        var rows = sheetData.Elements<S.Row>().ToList();
        for (int ri = 0; ri < rows.Count && model.Rows.Count < MaxImportRows; ri++)
        {
            var cells = ReadRowCells(rows[ri], sharedStrings);
            if (ri == 0)
                model.Headers = ClampCols(cells);
            else
                model.Rows.Add(ClampCols(cells));
        }

        return model;
    }

    private static List<string> ReadRowCells(S.Row row, List<string> sharedStrings)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements<S.Cell>())
        {
            // Handle sparse cells (gaps like A1, C1 — fill B1 with "")
            var colIndex = CellReferenceToIndex(cell.CellReference?.Value);
            while (colIndex > cells.Count && cells.Count < MaxImportCols)
                cells.Add("");

            if (cells.Count >= MaxImportCols) break;
            cells.Add(GetCellValue(cell, sharedStrings));
        }
        return cells;
    }

    private static string GetCellValue(S.Cell cell, List<string> sharedStrings)
    {
        var value = cell.CellValue?.Text ?? "";
        var dataType = cell.DataType?.Value;

        if (dataType == S.CellValues.SharedString)
        {
            if (int.TryParse(value, out int idx) && idx >= 0 && idx < sharedStrings.Count)
                return sharedStrings[idx];
            return "";
        }

        if (dataType == S.CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? "";

        if (dataType == S.CellValues.Boolean)
            return value == "1" ? "TRUE" : "FALSE";

        // Numeric or general — return the raw value text (numbers, dates as serial, etc.)
        // For date-formatted numeric cells, attempt a friendly conversion.
        if ((dataType is null || dataType == S.CellValues.Number) && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
        {
            // Heuristic: if the cell has a date number format, convert the OLE serial to a date string.
            var formatId = cell.StyleIndex?.Value;
            if (formatId is not null && IsDateFormatId(formatId.Value))
            {
                try
                {
                    var date = DateTime.FromOADate(num);
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                catch { /* not a valid date serial — fall through */ }
            }
        }

        return value;
    }

    // Built-in Excel number-format IDs 14–22 and 45–47 are date/time formats.
    private static bool IsDateFormatId(uint formatId) =>
        formatId is >= 14 and <= 22 or >= 45 and <= 47;

    private static List<string> LoadSharedStrings(WorkbookPart workbookPart)
    {
        var ssPart = workbookPart.SharedStringTablePart;
        if (ssPart?.SharedStringTable is null) return new List<string>();
        return ssPart.SharedStringTable.Elements<S.SharedStringItem>()
            .Select(item => item.InnerText ?? "")
            .ToList();
    }

    // Converts a cell reference like "C5" to a 0-based column index (2).
    private static int CellReferenceToIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return 0;
        int col = 0;
        foreach (char c in cellRef)
        {
            if (c >= 'A' && c <= 'Z') col = col * 26 + (c - 'A' + 1);
            else break;
        }
        return Math.Max(0, col - 1);
    }

    // ---- TableModel → XLSX ----------------------------------------------------------------------

    /// <summary>
    /// Writes one or more tables to an .xlsx stream. Each table becomes a worksheet named by its
    /// (name, model) pair. Header row is bold; numeric cells are stored as numbers; dates as dates.
    /// </summary>
    public static void WriteXlsx(IReadOnlyList<(string Name, TableModel Model)> tables, Stream output)
    {
        using var doc = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var workbookPart = doc.AddWorkbookPart();

        // Shared styles: index 0 = default, index 1 = bold (for headers), index 2 = date.
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();

        var sheetElements = new List<S.Sheet>();
        uint sheetId = 1;

        foreach (var (name, model) in tables)
        {
            var wsPart = workbookPart.AddNewPart<WorksheetPart>();
            var relId = workbookPart.GetIdOfPart(wsPart);

            var sheetData = new S.SheetData();

            // Header row (bold, style index 1)
            if (model.Headers.Count > 0)
            {
                var headerRow = new S.Row { RowIndex = 1 };
                for (int c = 0; c < model.Headers.Count; c++)
                    headerRow.Append(CreateTextCell(IndexToColumnRef(c) + "1", model.Headers[c], styleIndex: 1));
                sheetData.Append(headerRow);
            }

            // Data rows
            for (int r = 0; r < model.Rows.Count; r++)
            {
                var rowIndex = (uint)(r + (model.Headers.Count > 0 ? 2 : 1));
                var row = new S.Row { RowIndex = rowIndex };
                var rowData = model.Rows[r];
                for (int c = 0; c < rowData.Count; c++)
                {
                    var cellRef = IndexToColumnRef(c) + rowIndex;
                    row.Append(CreateTypedCell(cellRef, rowData[c]));
                }
                sheetData.Append(row);
            }

            wsPart.Worksheet = new S.Worksheet(sheetData);
            wsPart.Worksheet.Save();

            var safeName = SanitizeSheetName(name, sheetId);
            sheetElements.Add(new S.Sheet { Id = relId, SheetId = sheetId, Name = safeName });
            sheetId++;
        }

        workbookPart.Workbook = new S.Workbook(new S.Sheets(sheetElements));
        workbookPart.Workbook.Save();
    }

    /// <summary>Convenience: write a single table to a new .xlsx file at the given path.</summary>
    public static void WriteXlsxFile(string name, TableModel model, string path)
    {
        using var fs = File.Create(path);
        WriteXlsx(new[] { (name, model) }, fs);
    }

    // Creates a cell that stores numbers as numbers, dates as dates, and everything else as text.
    private static S.Cell CreateTypedCell(string cellRef, string value)
    {
        if (string.IsNullOrEmpty(value))
            return new S.Cell { CellReference = cellRef, DataType = S.CellValues.String, CellValue = new S.CellValue("") };

        // Numeric?
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double num))
            return new S.Cell { CellReference = cellRef, DataType = S.CellValues.Number, CellValue = new S.CellValue(num.ToString(CultureInfo.InvariantCulture)) };

        // Date? (common ISO and locale formats)
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            // Store as OLE Automation serial with a date number format (style index 2).
            return new S.Cell
            {
                CellReference = cellRef,
                DataType = S.CellValues.Number,
                StyleIndex = 2,
                CellValue = new S.CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture))
            };
        }

        return CreateTextCell(cellRef, value, styleIndex: 0);
    }

    private static S.Cell CreateTextCell(string cellRef, string value, uint styleIndex)
    {
        return new S.Cell
        {
            CellReference = cellRef,
            DataType = S.CellValues.InlineString,
            StyleIndex = styleIndex,
            InlineString = new S.InlineString(new S.Text(value ?? ""))
        };
    }

    // Minimal stylesheet: default font, bold font, date format, and three cell formats (xf):
    //   0 = default, 1 = bold, 2 = date (yyyy-mm-dd).
    private static S.Stylesheet CreateStylesheet()
    {
        var fonts = new S.Fonts(
            new S.Font(new S.FontSize { Val = 11 }, new S.FontName { Val = "Calibri" }),
            new S.Font(new S.Bold(), new S.FontSize { Val = 11 }, new S.FontName { Val = "Calibri" }));

        var fills = new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }));
        var borders = new S.Borders(new S.Border());

        var numFmts = new S.NumberingFormats(
            new S.NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy\\-mm\\-dd" });

        var cellXfs = new S.CellFormats(
            new S.CellFormat { FontId = 0, FillId = 0, BorderId = 0 },                             // 0: default
            new S.CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true },           // 1: bold
            new S.CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 164, ApplyNumberFormat = true }); // 2: date

        return new S.Stylesheet(numFmts, fonts, fills, borders, cellXfs);
    }

    // ---- TableModel → CSV -----------------------------------------------------------------------

    /// <summary>Serializes a TableModel back to RFC-4180 CSV text (comma-delimited).</summary>
    public static string WriteCsv(TableModel model)
    {
        var sb = new StringBuilder();
        if (model.Headers.Count > 0)
        {
            AppendCsvRow(sb, model.Headers);
        }
        foreach (var row in model.Rows)
            AppendCsvRow(sb, row);
        return sb.ToString();
    }

    private static void AppendCsvRow(StringBuilder sb, List<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = fields[i];
            // Quote if the field contains a comma, quote, or newline.
            if (f.Contains(',') || f.Contains('"') || f.Contains('\n') || f.Contains('\r'))
            {
                sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                sb.Append(f);
            }
        }
        sb.Append("\r\n");
    }

    // ---- TableModel → Markdown pipe table -------------------------------------------------------

    /// <summary>
    /// Renders a TableModel as a GitHub-flavoured Markdown pipe table. Escapes literal pipes in
    /// cell text. Always emits a delimiter row (required by GFM).
    /// </summary>
    public static string ToMarkdownTable(TableModel model)
    {
        int cols = model.ColumnCount;
        if (cols == 0) return "";

        var sb = new StringBuilder("\n");

        // Header row (use headers if present, else generic Column N)
        sb.Append('|');
        for (int c = 0; c < cols; c++)
        {
            var header = c < model.Headers.Count && model.Headers[c].Length > 0
                ? model.Headers[c]
                : $"Column {c + 1}";
            sb.Append(' ').Append(EscapePipe(header)).Append(" |");
        }
        sb.Append('\n');

        // Delimiter row
        sb.Append('|');
        for (int c = 0; c < cols; c++) sb.Append(" --- |");
        sb.Append('\n');

        // Body rows
        foreach (var row in model.Rows)
        {
            sb.Append('|');
            for (int c = 0; c < cols; c++)
            {
                var cell = c < row.Count ? row[c] : "";
                sb.Append(' ').Append(EscapePipe(cell)).Append(" |");
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string EscapePipe(string text) =>
        text.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");

    // ---- Markdown → TableModel[] (extraction) ---------------------------------------------------

    // The Markdig pipeline used for extraction — pipe tables + standard features.
    private static readonly MarkdownPipeline ExtractPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    /// <summary>
    /// Extracts all pipe tables from a Markdown document, with their source line positions and the
    /// nearest preceding ATX heading (for naming worksheets on export).
    /// </summary>
    public static List<ExtractedTable> ExtractTables(string markdown)
    {
        var results = new List<ExtractedTable>();
        if (string.IsNullOrWhiteSpace(markdown)) return results;

        var doc = Markdown.Parse(markdown, ExtractPipeline);
        string? lastHeading = null;

        foreach (var block in doc)
        {
            if (block is Markdig.Syntax.HeadingBlock heading)
            {
                lastHeading = GetInlineText(heading.Inline)?.Trim();
            }
            else if (block is MdTable table)
            {
                var model = TableFromMarkdig(table);
                var startLine = table.Line; // 0-based
                var endLine = startLine + CountTableLines(table, markdown, startLine) - 1;
                results.Add(new ExtractedTable(model, startLine, endLine, lastHeading));
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the table whose source span contains the given 0-based line number.
    /// Returns null if the cursor isn't inside any table.
    /// </summary>
    public static ExtractedTable? FindTableAtLine(string markdown, int line)
    {
        var tables = ExtractTables(markdown);
        return tables.FirstOrDefault(t => line >= t.StartLine && line <= t.EndLine);
    }

    private static TableModel TableFromMarkdig(MdTable table)
    {
        var model = new TableModel();
        bool first = true;

        foreach (var rowObj in table)
        {
            if (rowObj is not MdTableRow row) continue;

            // Skip the delimiter row (all cells are empty or contain only dashes/colons)
            var cells = ExtractRowCells(row);
            if (IsDelimiterRow(cells)) continue;

            if (first)
            {
                model.Headers = cells;
                first = false;
            }
            else
            {
                model.Rows.Add(cells);
            }
        }

        return model;
    }

    private static List<string> ExtractRowCells(MdTableRow row)
    {
        var cells = new List<string>();
        foreach (var cellObj in row)
        {
            if (cellObj is not MdTableCell cell) continue;
            // A cell's text is the concatenation of all its inline content.
            // Each child is a LeafBlock (typically ParagraphBlock) whose Inline holds the text.
            var text = string.Join(" ", cell.OfType<LeafBlock>()
                .Select(p => GetInlineText(p.Inline) ?? "")).Trim();
            cells.Add(text);
        }
        return cells;
    }

    private static bool IsDelimiterRow(List<string> cells) =>
        cells.Count > 0 && cells.All(c =>
            c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));

    private static int CountTableLines(MdTable table, string markdown, int startLine)
    {
        // Count consecutive pipe-prefixed lines starting from startLine.
        var lines = markdown.Split('\n');
        int count = 0;
        for (int i = startLine; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('|') || (count == 0 && trimmed.Length > 0))
                count++;
            else
                break;
        }
        return Math.Max(1, count);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Recursively extracts plain text from a Markdig inline tree.</summary>
    private static string? GetInlineText(ContainerInline? inline)
    {
        if (inline is null) return null;
        var sb = new StringBuilder();
        foreach (var child in inline)
        {
            if (child is LiteralInline literal)
                sb.Append(literal.Content);
            else if (child is ContainerInline container)
                sb.Append(GetInlineText(container));
        }
        return sb.ToString();
    }

    private static List<string> ClampCols(List<string> row)
    {
        if (row.Count <= MaxImportCols) return row;
        return row.Take(MaxImportCols).ToList();
    }

    // Converts a 0-based column index to an Excel column reference (0→A, 25→Z, 26→AA).
    private static string IndexToColumnRef(int index)
    {
        var sb = new StringBuilder();
        index++; // 1-based
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    // Excel sheet names: max 31 chars, no []:*?/\ characters.
    private static string SanitizeSheetName(string name, uint fallbackId)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Table" + fallbackId;
        var clean = new string(name.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray());
        if (clean.Length == 0) return "Table" + fallbackId;
        return clean.Length > 31 ? clean[..31] : clean;
    }
}
