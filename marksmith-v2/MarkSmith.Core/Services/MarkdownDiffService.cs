using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    public enum DiffChangeType
    {
        Unchanged,
        Inserted,
        Deleted
    }

    public class DiffLine
    {
        public DiffChangeType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public int? OldLineNumber { get; set; }
        public int? NewLineNumber { get; set; }
    }

    public class MarkdownDiffResult
    {
        public List<DiffLine> Lines { get; set; } = new List<DiffLine>();
        public int InsertedCount { get; set; }
        public int DeletedCount { get; set; }
        public int UnchangedCount { get; set; }
        public bool HasChanges => InsertedCount > 0 || DeletedCount > 0;
    }

    public class MarkdownDiffService
    {
        public MarkdownDiffResult Diff(string oldText, string newText) => Compare(oldText, newText);

        public MarkdownDiffResult Compare(string oldText, string newText)
        {
            var result = new MarkdownDiffResult();
            var oldLines = SplitLines(oldText ?? string.Empty);
            var newLines = SplitLines(newText ?? string.Empty);

            int[,] matrix = ComputeLcs(oldLines, newLines);

            int i = oldLines.Length;
            int j = newLines.Length;
            var diffStack = new Stack<DiffLine>();

            while (i > 0 || j > 0)
            {
                if (i > 0 && j > 0 && oldLines[i - 1] == newLines[j - 1])
                {
                    diffStack.Push(new DiffLine
                    {
                        Type = DiffChangeType.Unchanged,
                        Content = oldLines[i - 1],
                        OldLineNumber = i,
                        NewLineNumber = j
                    });
                    i--;
                    j--;
                }
                else if (j > 0 && (i == 0 || matrix[i, j - 1] >= matrix[i - 1, j]))
                {
                    diffStack.Push(new DiffLine
                    {
                        Type = DiffChangeType.Inserted,
                        Content = newLines[j - 1],
                        NewLineNumber = j
                    });
                    j--;
                }
                else if (i > 0 && (j == 0 || matrix[i, j - 1] < matrix[i - 1, j]))
                {
                    diffStack.Push(new DiffLine
                    {
                        Type = DiffChangeType.Deleted,
                        Content = oldLines[i - 1],
                        OldLineNumber = i
                    });
                    i--;
                }
            }

            while (diffStack.Count > 0)
            {
                var line = diffStack.Pop();
                result.Lines.Add(line);

                switch (line.Type)
                {
                    case DiffChangeType.Inserted:
                        result.InsertedCount++;
                        break;
                    case DiffChangeType.Deleted:
                        result.DeletedCount++;
                        break;
                    case DiffChangeType.Unchanged:
                        result.UnchangedCount++;
                        break;
                }
            }

            return result;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return Regex.Split(text, @"\r\n|\r|\n");
        }

        private static int[,] ComputeLcs(string[] oldLines, string[] newLines)
        {
            int m = oldLines.Length;
            int n = newLines.Length;
            int[,] c = new int[m + 1, n + 1];

            for (int i = 1; i <= m; i++)
            {
                for (int j = 1; j <= n; j++)
                {
                    if (oldLines[i - 1] == newLines[j - 1])
                    {
                        c[i, j] = c[i - 1, j - 1] + 1;
                    }
                    else
                    {
                        c[i, j] = Math.Max(c[i - 1, j], c[i, j - 1]);
                    }
                }
            }

            return c;
        }
    }
}
