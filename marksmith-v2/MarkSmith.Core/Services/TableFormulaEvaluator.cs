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
    /// (e.g. =SUM(ABOVE), =AVERAGE(LEFT), =COUNT, =MIN, =MAX, =PRODUCT, =SUM(B1:B4)).
    /// </summary>
    public static class TableFormulaEvaluator
    {
        private static readonly Regex PositionalFormulaRegex = new(
            @"^=\s*(?<op>SUM|AVERAGE|AVG|COUNT|MIN|MAX|PRODUCT)\s*\(\s*(?<pos>ABOVE|LEFT|RIGHT|BELOW)\s*\)(?:\s*(?:\\#\s*)?(?:""(?<fmt>[^""]*)""|(?<fmt>\S+)))?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RangeFormulaRegex = new(
            @"^=\s*(?<op>SUM|AVERAGE|AVG|COUNT|MIN|MAX|PRODUCT)\s*\(\s*(?<from>[A-Za-z]+[0-9]+)\s*:\s*(?<to>[A-Za-z]+[0-9]+)\s*\)(?:\s*(?:\\#\s*)?(?:""(?<fmt>[^""]*)""|(?<fmt>\S+)))?\s*$",
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
                    if (IsSeparatorLine(line)) continue;

                    var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToList();
                    tableRows.Add(cells);
                    lineIndices.Add(i);
                }
            }

            if (tableRows.Count == 0) return markdownTable;

            int maxRows = tableRows.Count;
            int maxCols = tableRows.Max(r => r.Count);
            var grid = new double?[maxRows, maxCols];

            // 1. First pass: extract literal numeric values
            for (int r = 0; r < maxRows; r++)
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
            for (int r = 0; r < maxRows; r++)
            {
                for (int c = 0; c < tableRows[r].Count; c++)
                {
                    var cellText = tableRows[r][c];
                    if (TryEvaluateCell(cellText, r, c, grid, maxRows, maxCols, out double result, out string formattedResult, out _))
                    {
                        tableRows[r][c] = formattedResult;
                        grid[r, c] = result;
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

        public static bool TryEvaluateCell(string cellText, int r, int c, double?[,] grid, int maxRows, int maxCols, out double result, out string formattedResult, out string formulaInstruction)
        {
            result = 0;
            formattedResult = "";
            formulaInstruction = "";

            if (string.IsNullOrWhiteSpace(cellText) || !cellText.TrimStart().StartsWith('='))
                return false;

            var trimmed = cellText.Trim();
            var posMatch = PositionalFormulaRegex.Match(trimmed);
            if (posMatch.Success)
            {
                string op = posMatch.Groups["op"].Value.ToUpperInvariant();
                string pos = posMatch.Groups["pos"].Value.ToUpperInvariant();
                string fmt = posMatch.Groups["fmt"].Success ? posMatch.Groups["fmt"].Value : "";

                var values = new List<double>();
                if (pos == "ABOVE")
                {
                    for (int row = r - 1; row >= 0; row--)
                    {
                        if (grid[row, c].HasValue)
                        {
                            values.Add(grid[row, c]!.Value);
                        }
                    }
                    values.Reverse(); // Preserve top-to-bottom order
                }
                else if (pos == "LEFT")
                {
                    for (int col = c - 1; col >= 0; col--)
                    {
                        if (grid[r, col].HasValue)
                        {
                            values.Add(grid[r, col]!.Value);
                        }
                    }
                    values.Reverse(); // Preserve left-to-right order
                }
                else if (pos == "RIGHT")
                {
                    for (int col = c + 1; col < maxCols; col++)
                    {
                        if (grid[r, col].HasValue)
                        {
                            values.Add(grid[r, col]!.Value);
                        }
                    }
                }
                else if (pos == "BELOW")
                {
                    for (int row = r + 1; row < maxRows; row++)
                    {
                        if (grid[row, c].HasValue)
                        {
                            values.Add(grid[row, c]!.Value);
                        }
                    }
                }

                result = ExecuteOperation(op, values);
                formattedResult = FormatNumber(result, fmt);

                if (!string.IsNullOrEmpty(fmt))
                {
                    var cleanFmt = fmt.Trim('"', '\'');
                    formulaInstruction = $"={op}({pos}) \\# \"{cleanFmt}\"";
                }
                else
                {
                    formulaInstruction = $"={op}({pos})";
                }
                return true;
            }

            var rangeMatch = RangeFormulaRegex.Match(trimmed);
            if (rangeMatch.Success)
            {
                string op = rangeMatch.Groups["op"].Value.ToUpperInvariant();
                string fromCoord = rangeMatch.Groups["from"].Value;
                string toCoord = rangeMatch.Groups["to"].Value;
                string fmt = rangeMatch.Groups["fmt"].Success ? rangeMatch.Groups["fmt"].Value : "";

                if (TryParseCoordinate(fromCoord, out int r1, out int c1) &&
                    TryParseCoordinate(toCoord, out int r2, out int c2))
                {
                    var values = new List<double>();
                    int minR = Math.Min(r1, r2), maxR = Math.Max(r1, r2);
                    int minC = Math.Min(c1, c2), maxC = Math.Max(c1, c2);

                    for (int row = minR; row <= maxR && row < maxRows; row++)
                    {
                        for (int col = minC; col <= maxC && col < maxCols; col++)
                        {
                            if (grid[row, col].HasValue)
                            {
                                values.Add(grid[row, col]!.Value);
                            }
                        }
                    }

                    result = ExecuteOperation(op, values);
                    formattedResult = FormatNumber(result, fmt);

                    if (!string.IsNullOrEmpty(fmt))
                    {
                        var cleanFmt = fmt.Trim('"', '\'');
                        formulaInstruction = $"={op}({fromCoord}:{toCoord}) \\# \"{cleanFmt}\"";
                    }
                    else
                    {
                        formulaInstruction = $"={op}({fromCoord}:{toCoord})";
                    }
                    return true;
                }
            }

            return false;
        }

        private static bool IsSeparatorLine(string line)
        {
            var content = line.Trim('|', ' ', '-', ':');
            return string.IsNullOrEmpty(content) || content.All(ch => ch == '-' || ch == ':' || ch == '|' || ch == ' ');
        }

        public static bool TryParseNumber(string text, out double val)
        {
            val = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();

            // Negative in parentheses e.g. (100) or ($100)
            bool isNegative = false;
            if (s.StartsWith("(") && s.EndsWith(")"))
            {
                isNegative = true;
                s = s.Substring(1, s.Length - 2).Trim();
            }
            else if (s.StartsWith("-"))
            {
                isNegative = true;
                s = s.Substring(1).Trim();
            }

            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                if (char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-')
                {
                    sb.Append(ch);
                }
            }
            var cleaned = sb.ToString().Trim();
            if (cleaned.Length == 0) return false;

            if (cleaned.Contains(',') && cleaned.Contains('.'))
            {
                if (cleaned.IndexOf(',') < cleaned.IndexOf('.'))
                {
                    cleaned = cleaned.Replace(",", "");
                }
                else
                {
                    cleaned = cleaned.Replace(".", "").Replace(',', '.');
                }
            }
            else if (cleaned.Contains(','))
            {
                var parts = cleaned.Split(',');
                if (parts.Length > 1 && parts.Skip(1).All(p => p.Length == 3))
                {
                    cleaned = cleaned.Replace(",", "");
                }
                else
                {
                    cleaned = cleaned.Replace(',', '.');
                }
            }

            if (double.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out val))
            {
                if (isNegative) val = -val;
                return true;
            }
            return false;
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

        public static string FormatNumber(double val, string? formatSwitch = null)
        {
            if (!string.IsNullOrWhiteSpace(formatSwitch))
            {
                var fmt = formatSwitch.Trim('"', '\'', ' ', '\\', '#').Trim();
                if (fmt.Contains("$#,##0.00") || fmt.Contains("$#,##0") || fmt.Contains("0.00") || fmt.Contains("#,##0.00"))
                {
                    if (fmt.Contains("$"))
                    {
                        var absVal = Math.Abs(val);
                        var formatted = absVal.ToString("N2", CultureInfo.InvariantCulture);
                        return val < 0 ? $"(${formatted})" : $"${formatted}";
                    }
                    return val.ToString("N2", CultureInfo.InvariantCulture);
                }
                try
                {
                    return val.ToString(fmt, CultureInfo.InvariantCulture);
                }
                catch { }
            }

            if (Math.Abs(val % 1) < 0.0001)
                return ((long)Math.Round(val)).ToString("0", CultureInfo.InvariantCulture);
            return val.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
