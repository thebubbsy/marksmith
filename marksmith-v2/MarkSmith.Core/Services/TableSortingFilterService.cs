using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public enum TableColumnDataType
{
    String,
    Number,
    Currency,
    Date
}

public class ParsedTable
{
    public List<string> Headers { get; } = new();
    public List<List<string>> Rows { get; } = new();
    public List<TableColumnDataType> ColumnTypes { get; } = new();
}

/// <summary>
/// Service for parsing Markdown tables, detecting column data types, sorting rows, and injecting interactive filtering.
/// </summary>
public static class TableSortingFilterService
{
    private static readonly Regex TableRowRegex = new(@"^\s*\|(.+)\|\s*$", RegexOptions.Compiled);
    private static readonly Regex DelimiterRegex = new(@"^\s*\|?\s*(?::?-+:?\s*\|?)+\s*$", RegexOptions.Compiled);
    private static readonly Regex DelimiterCellRegex = new(@"^:?-+:?$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a raw Markdown table into structured rows and infers column data types.
    /// </summary>
    public static ParsedTable Parse(string tableMarkdown)
    {
        var table = new ParsedTable();
        if (string.IsNullOrWhiteSpace(tableMarkdown))
            return table;

        var lines = tableMarkdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        bool isHeader = true;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var match = TableRowRegex.Match(line);
            if (!match.Success) continue;

            var cells = match.Groups[1].Value.Split('|').Select(c => c.Trim()).ToList();

            // Delimiter row: | :--- | :--- | :--- |
            if (cells.Count > 0 && cells.All(c => DelimiterCellRegex.IsMatch(c)))
            {
                isHeader = false;
                continue;
            }

            if (isHeader && table.Headers.Count == 0)
            {
                table.Headers.AddRange(cells);
            }
            else
            {
                table.Rows.Add(cells);
            }
        }

        // Infer column data types
        for (int col = 0; col < table.Headers.Count; col++)
        {
            table.ColumnTypes.Add(InferColumnType(table.Rows, col));
        }

        return table;
    }

    /// <summary>
    /// Sorts table rows by a specific column index.
    /// </summary>
    public static List<List<string>> SortRows(ParsedTable table, int columnIndex, bool ascending = true)
    {
        if (columnIndex < 0 || columnIndex >= table.Headers.Count)
            return table.Rows;

        var type = table.ColumnTypes.Count > columnIndex ? table.ColumnTypes[columnIndex] : TableColumnDataType.String;

        var sorted = table.Rows.OrderBy(row =>
        {
            string val = columnIndex < row.Count ? row[columnIndex] : "";
            return ParseSortKey(val, type);
        });

        return ascending ? sorted.ToList() : sorted.Reverse().ToList();
    }

    private static TableColumnDataType InferColumnType(List<List<string>> rows, int colIndex)
    {
        int numCount = 0, dateCount = 0, currCount = 0, total = 0;

        foreach (var r in rows)
        {
            if (colIndex >= r.Count || string.IsNullOrWhiteSpace(r[colIndex])) continue;
            string val = r[colIndex];
            total++;

            if (val.StartsWith("$") || val.StartsWith("€") || val.StartsWith("£")) currCount++;
            else if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) numCount++;
            else if (DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) dateCount++;
        }

        if (total == 0) return TableColumnDataType.String;
        if (currCount >= total / 2) return TableColumnDataType.Currency;
        if (numCount >= total / 2) return TableColumnDataType.Number;
        if (dateCount >= total / 2) return TableColumnDataType.Date;
        return TableColumnDataType.String;
    }

    private static IComparable ParseSortKey(string val, TableColumnDataType type)
    {
        switch (type)
        {
            case TableColumnDataType.Number:
                if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double num)) return num;
                break;
            case TableColumnDataType.Currency:
                string clean = Regex.Replace(val, @"[^\d.-]", "");
                if (double.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double curr)) return curr;
                break;
            case TableColumnDataType.Date:
                if (DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dt)) return dt;
                break;
        }

        return val;
    }
}
