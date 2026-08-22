using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record TableDataSet(List<string> Columns, List<List<string>> Rows);

/// <summary>
/// Service for executing in-memory SQL queries against Markdown datasets and transcluding results as formatted Markdown tables.
/// </summary>
public static class SqlMarkdownTransclusionService
{
    private static readonly Regex SqlFenceRegex = new(
        @"```sql:table(?:\s+data=""([^""]+)"")?\r?\n([\s\S]*?)```",
        RegexOptions.Compiled);

    /// <summary>
    /// Executes a simple in-memory SQL query against a provided TableDataSet.
    /// Supports SELECT cols FROM dataset WHERE col=val ORDER BY col LIMIT n
    /// </summary>
    public static TableDataSet ExecuteQuery(TableDataSet data, string sql)
    {
        if (data == null || data.Rows.Count == 0 || string.IsNullOrWhiteSpace(sql))
            return data ?? new TableDataSet(new List<string>(), new List<List<string>>());

        string cleanSql = sql.Trim().Replace("\r\n", " ").Replace("\n", " ");

        // 1. SELECT clause
        var selectMatch = Regex.Match(cleanSql, @"^SELECT\s+(.+?)\s+FROM", RegexOptions.IgnoreCase);
        List<string> targetCols = new();
        List<int> targetColIndices = new();

        if (selectMatch.Success)
        {
            string colsStr = selectMatch.Groups[1].Value.Trim();
            if (colsStr == "*")
            {
                targetCols.AddRange(data.Columns);
                targetColIndices.AddRange(Enumerable.Range(0, data.Columns.Count));
            }
            else
            {
                var requested = colsStr.Split(',').Select(c => c.Trim()).ToList();
                foreach (var col in requested)
                {
                    int idx = data.Columns.FindIndex(c => c.Equals(col, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        targetCols.Add(data.Columns[idx]);
                        targetColIndices.Add(idx);
                    }
                }
            }
        }
        else
        {
            targetCols.AddRange(data.Columns);
            targetColIndices.AddRange(Enumerable.Range(0, data.Columns.Count));
        }

        var resultRows = new List<List<string>>(data.Rows);

        // 2. WHERE clause: WHERE col = 'val' or WHERE col > val
        var whereMatch = Regex.Match(cleanSql, @"WHERE\s+([a-zA-Z0-9_]+)\s*(=|>|<|>=|<=|!=)\s*(?:'([^']*)'|""([^""]*)""|([^\s;]+))", RegexOptions.IgnoreCase);
        if (whereMatch.Success)
        {
            string colName = whereMatch.Groups[1].Value.Trim();
            string op = whereMatch.Groups[2].Value;
            string filterVal = whereMatch.Groups[3].Success ? whereMatch.Groups[3].Value
                             : whereMatch.Groups[4].Success ? whereMatch.Groups[4].Value
                             : whereMatch.Groups[5].Value;

            int colIdx = data.Columns.FindIndex(c => c.Equals(colName, StringComparison.OrdinalIgnoreCase));
            if (colIdx >= 0)
            {
                resultRows = resultRows.Where(row =>
                {
                    if (colIdx >= row.Count) return false;
                    string cellVal = row[colIdx];
                    if (op == "=") return cellVal.Equals(filterVal, StringComparison.OrdinalIgnoreCase);
                    if (double.TryParse(cellVal, out double numVal) && double.TryParse(filterVal, out double numFilter))
                    {
                        return op == ">" ? numVal > numFilter : numVal < numFilter;
                    }
                    return false;
                }).ToList();
            }
        }

        // 3. ORDER BY clause: ORDER BY col ASC/DESC
        var orderMatch = Regex.Match(cleanSql, @"ORDER\s+BY\s+([a-zA-Z0-9_]+)(?:\s+(ASC|DESC))?", RegexOptions.IgnoreCase);
        if (orderMatch.Success)
        {
            string sortCol = orderMatch.Groups[1].Value.Trim();
            bool desc = orderMatch.Groups[2].Success && orderMatch.Groups[2].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase);

            int sortIdx = data.Columns.FindIndex(c => c.Equals(sortCol, StringComparison.OrdinalIgnoreCase));
            if (sortIdx >= 0)
            {
                resultRows = desc
                    ? resultRows.OrderByDescending(r => sortIdx < r.Count ? r[sortIdx] : "").ToList()
                    : resultRows.OrderBy(r => sortIdx < r.Count ? r[sortIdx] : "").ToList();
            }
        }

        // 4. LIMIT clause: LIMIT n
        var limitMatch = Regex.Match(cleanSql, @"LIMIT\s+(\d+)", RegexOptions.IgnoreCase);
        if (limitMatch.Success && int.TryParse(limitMatch.Groups[1].Value, out int limit))
        {
            resultRows = resultRows.Take(limit).ToList();
        }

        // Project selected columns
        var projectedRows = resultRows.Select(row =>
            targetColIndices.Select(idx => idx < row.Count ? row[idx] : "").ToList()
        ).ToList();

        return new TableDataSet(targetCols, projectedRows);
    }

    /// <summary>
    /// Serializes a TableDataSet into a standard Markdown table.
    /// </summary>
    public static string ToMarkdownTable(TableDataSet data)
    {
        if (data.Columns.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", data.Columns) + " |");
        sb.AppendLine("| " + string.Join(" | ", data.Columns.Select(_ => ":---")) + " |");

        foreach (var r in data.Rows)
        {
            sb.AppendLine("| " + string.Join(" | ", r) + " |");
        }

        return sb.ToString().TrimEnd();
    }
}
