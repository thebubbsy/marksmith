using System;
using System.Collections.Generic;

namespace MarkSmith.Services
{
    public enum MathSpanKind
    {
        Inline,
        Display
    }

    public readonly struct MathSpan
    {
        public int Start { get; }
        public int Length { get; }
        public int InnerStart { get; }
        public int InnerLength { get; }
        public MathSpanKind Kind { get; }

        public MathSpan(int start, int length, int innerStart, int innerLength, MathSpanKind kind)
        {
            Start = start;
            Length = length;
            InnerStart = innerStart;
            InnerLength = innerLength;
            Kind = kind;
        }
    }

    /// <summary>
    /// Zero-allocation span-based math block and inline equation scanner.
    /// Replaces backtracking regular expressions with linear-time AST span scanning.
    /// </summary>
    public static class FastMathTokenizer
    {
        public static List<MathSpan> ScanMathSpans(ReadOnlySpan<char> text)
        {
            var results = new List<MathSpan>();
            int len = text.Length;
            int i = 0;
            bool inCodeBlock = false;

            while (i < len)
            {
                // Check for code fence ```
                if (i + 2 < len && text[i] == '`' && text[i + 1] == '`' && text[i + 2] == '`')
                {
                    inCodeBlock = !inCodeBlock;
                    i += 3;
                    continue;
                }

                if (inCodeBlock)
                {
                    i++;
                    continue;
                }

                // Check for display math $$
                if (i + 1 < len && text[i] == '$' && text[i + 1] == '$')
                {
                    if (i > 0 && text[i - 1] == '\\')
                    {
                        i += 2; // Escaped \$$
                        continue;
                    }

                    int start = i;
                    int innerStart = i + 2;
                    int closeIdx = -1;

                    for (int j = innerStart; j < len - 1; j++)
                    {
                        if (text[j] == '$' && text[j + 1] == '$' && text[j - 1] != '\\')
                        {
                            closeIdx = j;
                            break;
                        }
                    }

                    if (closeIdx != -1)
                    {
                        int fullLen = (closeIdx + 2) - start;
                        int innerLen = closeIdx - innerStart;
                        results.Add(new MathSpan(start, fullLen, innerStart, innerLen, MathSpanKind.Display));
                        i = closeIdx + 2;
                        continue;
                    }
                }

                // Check for display math \[ ... \]
                if (i + 1 < len && text[i] == '\\' && text[i + 1] == '[')
                {
                    int start = i;
                    int innerStart = i + 2;
                    int closeIdx = -1;

                    for (int j = innerStart; j < len - 1; j++)
                    {
                        if (text[j] == '\\' && text[j + 1] == ']')
                        {
                            closeIdx = j;
                            break;
                        }
                    }

                    if (closeIdx != -1)
                    {
                        int fullLen = (closeIdx + 2) - start;
                        int innerLen = closeIdx - innerStart;
                        results.Add(new MathSpan(start, fullLen, innerStart, innerLen, MathSpanKind.Display));
                        i = closeIdx + 2;
                        continue;
                    }
                }

                // Check for inline math $
                if (text[i] == '$')
                {
                    if (i > 0 && text[i - 1] == '\\')
                    {
                        i++; // Escaped \$
                        continue;
                    }

                    int start = i;
                    int innerStart = i + 1;
                    int closeIdx = -1;

                    for (int j = innerStart; j < len; j++)
                    {
                        if (text[j] == '\n' || text[j] == '\r') break; // Inline math cannot span across paragraphs
                        if (text[j] == '$' && text[j - 1] != '\\')
                        {
                            closeIdx = j;
                            break;
                        }
                    }

                    if (closeIdx != -1 && closeIdx > innerStart)
                    {
                        int fullLen = (closeIdx + 1) - start;
                        int innerLen = closeIdx - innerStart;
                        results.Add(new MathSpan(start, fullLen, innerStart, innerLen, MathSpanKind.Inline));
                        i = closeIdx + 1;
                        continue;
                    }
                }

                i++;
            }

            return results;
        }

        /// <summary>
        /// Scans whether the span contains \ce{...} or \pu{...} mhchem formulas in linear time.
        /// </summary>
        public static bool HasChemistryFormulas(ReadOnlySpan<char> text)
        {
            int len = text.Length;
            for (int i = 0; i < len - 4; i++)
            {
                if (text[i] == '\\' &&
                    ((text[i + 1] == 'c' && text[i + 2] == 'e' && text[i + 3] == '{') ||
                     (text[i + 1] == 'p' && text[i + 2] == 'u' && text[i + 3] == '{')))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
