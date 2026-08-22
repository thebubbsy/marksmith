using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Services.Data;

public enum PivotAggregateType
{
    Sum,
    Average,
    Count,
    Min,
    Max
}

public class PivotResult
{
    public List<string> ColumnHeaders { get; } = new();
    public List<List<string>> Rows { get; } = new();
    public string MarkdownTable { get; set; } = string.Empty;
}

/// <summary>
/// Service that generates multidimensional pivot tables and statistical aggregations from tabular Markdown datasets.
/// </summary>
public static class MarkdownPivotTableService
{
    private static readonly System.Text.RegularExpressions.Regex NumericFilterRegex = new(@"[^\d.-]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Computes a pivot table from a 2D dataset.
    /// </summary>
    public static PivotResult Pivot(
        List<string> headers,
        List<List<string>> rows,
        int rowDimensionIdx,
        int colDimensionIdx,
        int valueIdx,
        PivotAggregateType aggType = PivotAggregateType.Sum)
    {
        var result = new PivotResult();
        if (headers == null || rows == null || rows.Count == 0)
            return result;

        string rowDimName = headers[rowDimensionIdx];
        string colDimName = headers[colDimensionIdx];

        // 1. Discover unique row and col keys
        var rowKeys = rows.Select(r => rowDimensionIdx < r.Count ? r[rowDimensionIdx] : "").Distinct().OrderBy(k => k).ToList();
        var colKeys = rows.Select(r => colDimensionIdx < r.Count ? r[colDimensionIdx] : "").Distinct().OrderBy(k => k).ToList();

        // 2. Build headers
        result.ColumnHeaders.Add(rowDimName);
        result.ColumnHeaders.AddRange(colKeys);
        result.ColumnHeaders.Add("Total");

        // 3. Group and aggregate
        var matrix = new Dictionary<(string row, string col), List<double>>();

        foreach (var r in rows)
        {
            string rk = rowDimensionIdx < r.Count ? r[rowDimensionIdx] : "";
            string ck = colDimensionIdx < r.Count ? r[colDimensionIdx] : "";
            string valStr = valueIdx < r.Count ? r[valueIdx] : "0";

            if (double.TryParse(NumericFilterRegex.Replace(valStr, ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                var key = (rk, ck);
                if (!matrix.TryGetValue(key, out var list))
                {
                    list = new List<double>();
                    matrix[key] = list;
                }
                list.Add(val);
            }
        }

        // 4. Generate row values
        var colTotals = new Dictionary<string, List<double>>();

        foreach (var rk in rowKeys)
        {
            var rowCells = new List<string> { rk };
            var rowValues = new List<double>();

            foreach (var ck in colKeys)
            {
                matrix.TryGetValue((rk, ck), out var vals);
                double agg = CalculateAggregate(vals, aggType);
                rowCells.Add(agg.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));

                if (vals != null)
                {
                    rowValues.AddRange(vals);
                    if (!colTotals.TryGetValue(ck, out var cList))
                    {
                        cList = new List<double>();
                        colTotals[ck] = cList;
                    }
                    cList.AddRange(vals);
                }
            }

            double rowTotal = CalculateAggregate(rowValues, aggType);
            rowCells.Add(rowTotal.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            result.Rows.Add(rowCells);
        }

        // 5. Grand Total row
        var grandRow = new List<string> { "**Grand Total**" };
        var allValues = new List<double>();

        foreach (var ck in colKeys)
        {
            colTotals.TryGetValue(ck, out var cVals);
            double cAgg = CalculateAggregate(cVals, aggType);
            grandRow.Add($"**{cAgg.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}**");
            if (cVals != null) allValues.AddRange(cVals);
        }

        double grandTotal = CalculateAggregate(allValues, aggType);
        grandRow.Add($"**{grandTotal.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}**");
        result.Rows.Add(grandRow);

        // 6. Generate Markdown
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", result.ColumnHeaders) + " |");
        sb.AppendLine("| " + string.Join(" | ", result.ColumnHeaders.Select(_ => ":---")) + " |");
        foreach (var r in result.Rows)
        {
            sb.AppendLine("| " + string.Join(" | ", r) + " |");
        }
        result.MarkdownTable = sb.ToString().TrimEnd();

        return result;
    }

    private static double CalculateAggregate(List<double>? vals, PivotAggregateType aggType)
    {
        if (vals == null || vals.Count == 0) return 0.0;
        return aggType switch
        {
            PivotAggregateType.Sum => vals.Sum(),
            PivotAggregateType.Average => vals.Average(),
            PivotAggregateType.Count => vals.Count,
            PivotAggregateType.Min => vals.Min(),
            PivotAggregateType.Max => vals.Max(),
            _ => vals.Sum()
        };
    }
}
