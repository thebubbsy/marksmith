using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services
{
    /// <summary>
    /// Evaluates spreadsheet-like formulas inside Markdown table cells
    /// (e.g. =SUM(B1:B4), =AVERAGE(C2:C10), =COUNT, =MIN, =MAX, =PRODUCT).
    /// </summary>
    public static class TableFormulaEvaluator
    {
        private static readonly Regex FormulaRegex = new(
            @"^=\s*(SUM|AVERAGE|AVG|COUNT|MIN|MAX|PRODUCT)\s*\(\s*([A-Za-z]+[0-9]+)\s*:\s*([A-Za-z]+[0-9]+)\s*\)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string EvaluateTableMarkdown(string markdownTable)
        {
            if (string.IsNullOrWhiteSpace(markdownTable)) return markdownTable;
            if (!markdownTable.Contains('=')) return markdownTable;

            var rawLines = markdownTable.Split('\n');
            var tableRows = new List<List<string>>();
            var lineIndices = new List<int>();

            for (int i = 0; i < rawLines.Length; i++)
            {
                var line = rawLines[i].Trim();
                if (line.StartsWith('|') && line.EndsWith('|'))
                {
                    // Check if separator line (|---|---|)
                    if (IsSeparatorLine(line)) continue;

                    var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                    tableRows.Add(cells);
                    lineIndices.Add(i);
                }
            }

            if (tableRows.Count == 0) return markdownTable;

            int maxCols = tableRows.Max(r => r.Count);
            var grid = new double?[tableRows.Count, maxCols];

            // 1. First pass: extract literal numeric values
            for (int r = 0; r < tableRows.Count; r++)
            {
                for (int c = 0; c < tableRows[r].Count; c++)
                {
                    var text = tableRows[r][c];
                    if (TryParseNumber(text, out double val))
                    {
                        grid[r, c] = val;
                    }
                }
            }

            // 2. Second pass: evaluate formula cells
            var outputLines = (string[])rawLines.Clone();
            for (int r = 0; r < tableRows.Count; r++)
            {
                for (int c = 0; c < tableRows[r].Count; c++)
                {
                    var cellText = tableRows[r][c];
                    var match = FormulaRegex.Match(cellText);
                    if (match.Success)
                    {
                        string op = match.Groups[1].Value.ToUpperInvariant();
                        string fromCoord = match.Groups[2].Value;
                        string toCoord = match.Groups[3].Value;

                        if (TryParseCoordinate(fromCoord, out int r1, out int c1) &&
                            TryParseCoordinate(toCoord, out int r2, out int c2))
                        {
                            var values = new List<double>();
                            int minR = Math.Min(r1, r2), maxR = Math.Max(r1, r2);
                            int minC = Math.Min(c1, c2), maxC = Math.Max(c1, c2);

                            for (int row = minR; row <= maxR && row < tableRows.Count; row++)
                            {
                                for (int col = minC; col <= maxC && col < maxCols; col++)
                                {
                                    if (grid[row, col].HasValue)
                                    {
                                        values.Add(grid[row, col]!.Value);
                                    }
                                }
                            }

                            double result = ExecuteOperation(op, values);
                            string formattedResult = FormatNumber(result);
                            tableRows[r][c] = formattedResult;
                            grid[r, c] = result;
                        }
                    }
                }
            }

            // 3. Rebuild updated markdown table lines
            int rowIdx = 0;
            for (int i = 0; i < rawLines.Length; i++)
            {
                var line = rawLines[i].Trim();
                if (line.StartsWith('|') && line.EndsWith('|') && !IsSeparatorLine(line))
                {
                    if (rowIdx < tableRows.Count)
                    {
                        outputLines[i] = "| " + string.Join(" | ", tableRows[rowIdx]) + " |";
                        rowIdx++;
                    }
                }
            }

            return string.Join('\n', outputLines);
        }

        private static bool IsSeparatorLine(string line)
        {
            var content = line.Trim('|', ' ', '-', ':');
            return string.IsNullOrEmpty(content) || content.All(ch => ch == '-' || ch == ':' || ch == '|' || ch == ' ');
        }

        private static bool TryParseNumber(string text, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var cleaned = text.Trim('$', '€', '£', '%', ' ', ',');
            return double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out val) ||
                   double.TryParse(cleaned, NumberStyles.Any, CultureInfo.CurrentCulture, out val);
        }

        private static bool TryParseCoordinate(string coord, out int row, out int col)
        {
            row = 0;
            col = 0;
            if (string.IsNullOrWhiteSpace(coord)) return false;

            int letterEnd = 0;
            while (letterEnd < coord.Length && char.IsLetter(coord[letterEnd])) letterEnd++;
            if (letterEnd == 0 || letterEnd == coord.Length) return false;

            string colLetters = coord[..letterEnd].ToUpperInvariant();
            string rowDigits = coord[letterEnd..];

            if (!int.TryParse(rowDigits, out int rNum)) return false;
            row = rNum - 1; // 1-based to 0-based

            int cNum = 0;
            foreach (char ch in colLetters)
            {
                cNum = cNum * 26 + (ch - 'A');
            }
            col = cNum;
            return row >= 0 && col >= 0;
        }

        private static double ExecuteOperation(string op, List<double> values)
        {
            if (values.Count == 0) return 0;
            return op switch
            {
                "SUM" => values.Sum(),
                "AVERAGE" or "AVG" => values.Average(),
                "COUNT" => values.Count,
                "MIN" => values.Min(),
                "MAX" => values.Max(),
                "PRODUCT" => values.Aggregate(1.0, (acc, v) => acc * v),
                _ => 0
            };
        }

        private static string FormatNumber(double val)
        {
            if (Math.Abs(val % 1) < 0.0001)
                return ((long)Math.Round(val)).ToString("N0", CultureInfo.InvariantCulture);
            return val.ToString("N2", CultureInfo.InvariantCulture);
        }
    }
}
