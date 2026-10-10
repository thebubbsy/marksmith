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

        /// <summary>
        /// Replaces each formula cell (<c>=SUM(ABOVE)</c>, <c>=AVERAGE(B2:B4)</c>…) of every pipe
        /// table in <paramref name="markdownTable"/> with its value. Each table is its own grid, so
        /// ABOVE and A1 references never reach into another table; rows inside fenced code are
        /// never tables; and only a row whose formula changed is rewritten, so every other line
        /// (an ASCII diagram in a code block, a table cell with an escaped <c>\|</c>) stays exactly
        /// as written.
        /// </summary>
        public static string EvaluateTableMarkdown(string markdownTable)
        {
            if (string.IsNullOrWhiteSpace(markdownTable)) return markdownTable;
            if (!markdownTable.Contains('=')) return markdownTable;

            var lines = markdownTable.Split('\n');
            bool changed = false;
            string? fence = null;
            var table = new List<int>();

            for (int i = 0; i <= lines.Length; i++)
            {
                var line = i < lines.Length ? lines[i] : null;
                var trimmed = line?.Trim() ?? "";
                if (line is not null && fence is null && IsFenceOpen(trimmed, out var opened))
                {
                    changed |= Evaluate(lines, table);
                    fence = opened;
                    continue;
                }
                if (fence is not null)
                {
                    if (line is not null && trimmed.StartsWith(fence, StringComparison.Ordinal) && trimmed.TrimStart(fence[0]).Trim().Length == 0)
                        fence = null;
                    continue;
                }
                if (line is not null && trimmed.Length > 1 && trimmed.StartsWith('|') && trimmed.EndsWith('|'))
                {
                    table.Add(i);
                    continue;
                }
                changed |= Evaluate(lines, table);
            }
            return changed ? string.Join('\n', lines) : markdownTable;
        }

        private static bool IsFenceOpen(string trimmed, out string fence)
        {
            fence = "";
            if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~')) return false;
            int n = 0;
            while (n < trimmed.Length && trimmed[n] == trimmed[0]) n++;
            if (n < 3) return false;
            fence = new string(trimmed[0], n);
            return true;
        }

        // One table: the rows at `rows` (consumed). Returns whether any line was rewritten.
        private static bool Evaluate(string[] lines, List<int> rows)
        {
            if (rows.Count == 0) return false;
            var body = rows.Where(i => !IsSeparatorLine(lines[i].Trim())).ToList();
            rows.Clear();
            var cells = body.Select(i => SplitCells(lines[i].Trim())).ToList();
            if (!cells.Any(r => r.Any(c => c.TrimStart().StartsWith('=')))) return false;

            int maxRows = cells.Count;
            int maxCols = cells.Max(r => r.Count);
            var texts = new string?[maxRows, maxCols];
            var grid = new double?[maxRows, maxCols];
            for (int r = 0; r < maxRows; r++)
                for (int c = 0; c < cells[r].Count; c++)
                {
                    texts[r, c] = cells[r][c];
                    if (TryParseNumber(cells[r][c], out double val)) grid[r, c] = val;
                }

            var results = EvaluateAll(texts, grid);
            if (results.Count == 0) return false;
            foreach (var ((r, c), res) in results) cells[r][c] = res.Formatted;
            foreach (var r in results.Keys.Select(k => k.Row).Distinct())
                lines[body[r]] = "| " + string.Join(" | ", cells[r]) + " |";
            return true;
        }

        /// <summary>One evaluated formula cell: its value, the text shown, and the Word field code.</summary>
        public readonly record struct FormulaResult(double Value, string Formatted, string Instruction);

        /// <summary>
        /// Evaluates every formula cell of one table. <paramref name="texts"/> is the cell text
        /// (row 0 is the header, so "A1" is the first header cell, as in Word and Excel) and
        /// <paramref name="grid"/> the numbers already in it; results are written back into
        /// <paramref name="grid"/>. A formula is evaluated only after every formula it reads, so
        /// a total row may sum a column of <c>=B2*C2</c> cells whatever order they appear in. A
        /// formula still waiting when nothing else can move (a cycle) reads those cells as empty.
        /// </summary>
        public static Dictionary<(int Row, int Col), FormulaResult> EvaluateAll(string?[,] texts, double?[,] grid)
        {
            int maxRows = texts.GetLength(0), maxCols = texts.GetLength(1);
            var results = new Dictionary<(int Row, int Col), FormulaResult>();
            var pending = new bool[maxRows, maxCols];
            var waiting = new List<(int R, int C)>();
            for (int r = 0; r < maxRows; r++)
                for (int c = 0; c < maxCols; c++)
                    if (texts[r, c] is { } t && t.TrimStart().StartsWith('='))
                    {
                        pending[r, c] = true;
                        grid[r, c] = null;
                        waiting.Add((r, c));
                    }

            var force = false;
            while (waiting.Count > 0)
            {
                var progressed = false;
                foreach (var (r, c) in waiting.ToList())
                {
                    if (!force && References(texts[r, c]!, r, c, maxRows, maxCols).Any(p => (p.Row != r || p.Col != c) && pending[p.Row, p.Col]))
                        continue;
                    waiting.Remove((r, c));
                    pending[r, c] = false;
                    progressed = true;
                    if (TryEvaluateCell(texts[r, c]!, r, c, grid, maxRows, maxCols, out var value, out var shown, out var instr))
                    {
                        grid[r, c] = value;
                        results[(r, c)] = new FormulaResult(value, shown, instr);
                    }
                }
                // Only a cycle is left: evaluate it once with the cycle's cells read as empty.
                force = !progressed;
            }
            return results;
        }

        // The cells a formula reads, for ordering; an unknown formula reads nothing.
        private static IEnumerable<(int Row, int Col)> References(string cellText, int r, int c, int maxRows, int maxCols)
        {
            var trimmed = cellText.Trim();
            var pos = PositionalFormulaRegex.Match(trimmed);
            if (pos.Success)
            {
                return pos.Groups["pos"].Value.ToUpperInvariant() switch
                {
                    "ABOVE" => Enumerable.Range(0, r).Select(row => (row, c)),
                    "BELOW" => Enumerable.Range(r + 1, Math.Max(0, maxRows - r - 1)).Select(row => (row, c)),
                    "LEFT" => Enumerable.Range(0, c).Select(col => (r, col)),
                    _ => Enumerable.Range(c + 1, Math.Max(0, maxCols - c - 1)).Select(col => (r, col)),
                };
            }
            var refs = new List<(int, int)>();
            foreach (Match m in CellRefRegex.Matches(trimmed))
                if (TryParseCoordinate(m.Value, out int row, out int col) && row < maxRows && col < maxCols)
                    refs.Add((row, col));
            var range = RangeFormulaRegex.Match(trimmed);
            if (range.Success && refs.Count == 2)
            {
                var (r1, c1) = refs[0];
                var (r2, c2) = refs[1];
                for (int row = Math.Min(r1, r2); row <= Math.Max(r1, r2); row++)
                    for (int col = Math.Min(c1, c2); col <= Math.Max(c1, c2); col++)
                        refs.Add((row, col));
            }
            return refs;
        }

        private static readonly Regex CellRefRegex = new(@"(?<![A-Za-z0-9.])[A-Za-z]{1,3}[0-9]+(?![A-Za-z0-9(])", RegexOptions.Compiled);

        // "=B2*C2", "=(B2+C2)/2 \# 0.00", "=D2*1.1": numbers, cell references, + - * / and brackets.
        private static readonly Regex ArithmeticFormulaRegex = new(
            @"^=\s*(?<expr>[A-Za-z0-9.\s+\-*/()]+?)(?:\s*(?:\\#\s*)?""(?<fmt>[^""]*)""|\s+\\#\s*(?<fmt>\S+))?\s*$",
            RegexOptions.Compiled);

        private static bool TryEvaluateArithmetic(string expr, double?[,] grid, int maxRows, int maxCols, out double value)
        {
            value = 0;
            int i = 0;
            bool ok = true;

            void Skip() { while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++; }

            double Primary()
            {
                Skip();
                if (i >= expr.Length) { ok = false; return 0; }
                if (expr[i] == '-') { i++; return -Primary(); }
                if (expr[i] == '+') { i++; return Primary(); }
                if (expr[i] == '(')
                {
                    i++;
                    var v = Sum();
                    Skip();
                    if (i < expr.Length && expr[i] == ')') i++; else ok = false;
                    return v;
                }
                int start = i;
                if (char.IsDigit(expr[i]) || expr[i] == '.')
                {
                    while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.')) i++;
                    if (double.TryParse(expr[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
                    ok = false; return 0;
                }
                while (i < expr.Length && char.IsLetterOrDigit(expr[i])) i++;
                // An empty cell counts as 0, as in a spreadsheet; anything else that isn't a cell is an error.
                if (start < i && TryParseCoordinate(expr[start..i], out int row, out int col) && row < maxRows && col < maxCols)
                    return grid[row, col] ?? 0;
                ok = false; return 0;
            }

            double Product()
            {
                var v = Primary();
                while (ok)
                {
                    Skip();
                    if (i < expr.Length && expr[i] == '*') { i++; v *= Primary(); }
                    else if (i < expr.Length && expr[i] == '/')
                    {
                        i++;
                        var d = Primary();
                        if (d == 0) { ok = false; return 0; }
                        v /= d;
                    }
                    else break;
                }
                return v;
            }

            double Sum()
            {
                var v = Product();
                while (ok)
                {
                    Skip();
                    if (i < expr.Length && expr[i] == '+') { i++; v += Product(); }
                    else if (i < expr.Length && expr[i] == '-') { i++; v -= Product(); }
                    else break;
                }
                return v;
            }

            value = Sum();
            Skip();
            return ok && i == expr.Length && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        // "| a | `x | y` | b \| c |" -> ["a", "`x | y`", "b \| c"]: a pipe inside a code span or
        // after a backslash is part of the cell, as Markdig reads it.
        private static List<string> SplitCells(string row, bool codeSpans = true)
        {
            var inner = row.Substring(1, row.Length - 2);
            var cells = new List<string>();
            var sb = new StringBuilder();
            int ticks = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char ch = inner[i];
                if (ch == '\\' && i + 1 < inner.Length) { sb.Append(ch).Append(inner[++i]); continue; }
                if (ch == '`' && codeSpans)
                {
                    int run = 1;
                    while (i + run < inner.Length && inner[i + run] == '`') run++;
                    ticks = ticks == 0 ? run : ticks == run ? 0 : ticks;
                    sb.Append('`', run);
                    i += run - 1;
                    continue;
                }
                if (ch == '|' && ticks == 0) { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(ch);
            }
            // A backtick that never closed is a literal one, not a code span.
            if (ticks != 0) return SplitCells(row, codeSpans: false);
            cells.Add(sb.ToString().Trim());
            return cells;
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

            var arith = ArithmeticFormulaRegex.Match(trimmed);
            if (arith.Success && TryEvaluateArithmetic(arith.Groups["expr"].Value, grid, maxRows, maxCols, out result))
            {
                string fmt = arith.Groups["fmt"].Success ? arith.Groups["fmt"].Value : "";
                formattedResult = FormatNumber(result, fmt);
                // Word's table formula fields take the same A1 arithmetic.
                var expr = Regex.Replace(arith.Groups["expr"].Value, @"\s+", "").ToUpperInvariant();
                formulaInstruction = string.IsNullOrEmpty(fmt) ? $"={expr}" : $"={expr} \\# \"{fmt.Trim('"', '\'')}\"";
                return true;
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
            // Only a number reads as one: a formula ("=B2*C2" is not 22), a label ("Q1" is not 1)
            // or a date-like code stays text. Currency signs, %, spaces and brackets are allowed.
            foreach (var ch in s)
                if (!(char.IsDigit(ch) || ch is '.' or ',' or '-' or '+' or '%' or '(' or ')' or ' ' or ' '
                      || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.CurrencySymbol))
                    return false;

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

            // Base-26 bijective conversion (A=1 .. Z=26, AA=27 ...), then shift to 0-based.
            // Using (ch - 'A') without the +1 offset collapses every "AA"-style multi-letter
            // column onto its first letter (AA, AB, ... all mapped to the same index as A),
            // so spreadsheet-style ranges like =SUM(AA1:AA10) silently pointed at column A.
            int cNum = 0;
            foreach (char ch in colLetters)
            {
                cNum = cNum * 26 + (ch - 'A' + 1);
            }
            col = cNum - 1;
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
