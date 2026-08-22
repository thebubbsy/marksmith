using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    public class TableFormatterService
    {
        private static readonly Regex SeparatorCellRegex = new(@"^:?-+:?$", RegexOptions.Compiled);

        public string FormatTable(string markdownTable)
        {
            if (string.IsNullOrWhiteSpace(markdownTable)) return markdownTable;

            var rawLines = markdownTable.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("|") && l.EndsWith("|"))
                .ToList();

            if (rawLines.Count < 2) return markdownTable;

            // Split rows into cell lists
            var rows = rawLines.Select(line =>
                line.Substring(1, line.Length - 2)
                    .Split('|')
                    .Select(c => c.Trim())
                    .ToList()
            ).ToList();

            int colCount = rows.Max(r => r.Count);
            if (colCount == 0) return markdownTable;

            // Ensure uniform colCount for all rows
            foreach (var r in rows)
            {
                while (r.Count < colCount) r.Add("");
            }

            // Identify separator row (row 1)
            var separatorRow = rows[1];
            bool isSeparator = separatorRow.All(c => SeparatorCellRegex.IsMatch(c));
            if (!isSeparator) return markdownTable;

            // Compute col widths
            int[] colWidths = new int[colCount];
            for (int col = 0; col < colCount; col++)
            {
                int maxLen = 3; // minimum width
                for (int row = 0; row < rows.Count; row++)
                {
                    if (row == 1) continue; // skip separator row in length calculation
                    maxLen = Math.Max(maxLen, rows[row][col].Length);
                }
                colWidths[col] = maxLen;
            }

            var sb = new StringBuilder();

            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("| ");
                for (int c = 0; c < colCount; c++)
                {
                    string cell = rows[r][c];
                    int width = colWidths[c];

                    if (r == 1)
                    {
                        // Separator row
                        bool alignLeft = cell.StartsWith(":");
                        bool alignRight = cell.EndsWith(":");

                        if (alignLeft && alignRight)
                        {
                            sb.Append(":" + new string('-', Math.Max(1, width - 2)) + ":");
                        }
                        else if (alignRight)
                        {
                            sb.Append(new string('-', Math.Max(1, width - 1)) + ":");
                        }
                        else if (alignLeft)
                        {
                            sb.Append(":" + new string('-', Math.Max(1, width - 1)));
                        }
                        else
                        {
                            sb.Append(new string('-', width));
                        }
                    }
                    else
                    {
                        // Data / Header row
                        sb.Append(cell.PadRight(width));
                    }

                    if (c < colCount - 1)
                    {
                        sb.Append(" | ");
                    }
                }
                sb.AppendLine(" |");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
