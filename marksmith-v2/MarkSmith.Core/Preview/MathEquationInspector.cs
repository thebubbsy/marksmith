using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Services;

namespace MarkSmith.Preview
{
    public sealed class MathEquationInspector
    {
        public sealed record MathSymbolToken(string Raw, string Kind, string Meaning);

        public sealed record InspectionResult(
            string SourceLatex,
            bool IsDisplayMode,
            bool IsValid,
            List<string> SyntaxIssues,
            List<MathSymbolToken> Tokens,
            string OmmlXml);

        private static readonly Regex SymbolRegex = new(
            @"(\\frac|\\sum|\\int|\\prod|\\sqrt|\\alpha|\\beta|\\gamma|\\delta|\\theta|\\pi|\\sigma|\\omega|\\infty|\+|-|\*|/|=|\\le|\\ge|\\ne|\^|_|[a-zA-Z0-9]+)",
            RegexOptions.Compiled);

        public InspectionResult Inspect(string latex, bool isDisplayMode = false)
        {
            if (string.IsNullOrWhiteSpace(latex))
            {
                return new InspectionResult("", isDisplayMode, true, new List<string>(), new List<MathSymbolToken>(), "<m:oMath/>");
            }

            var cleanLatex = latex.Trim().Trim('$', '\\', '[', ']');
            var issues = new List<string>();
            var tokens = new List<MathSymbolToken>();

            // 1. Delimiter balance check
            int openBraces = 0;
            foreach (char ch in cleanLatex)
            {
                if (ch == '{') openBraces++;
                else if (ch == '}') openBraces--;
                if (openBraces < 0)
                {
                    issues.Add("Unmatched closing brace '}' detected.");
                    openBraces = 0;
                }
            }
            if (openBraces > 0)
            {
                issues.Add($"Unclosed brace: {openBraces} open '{{' brace(s) remaining.");
            }

            // 2. Token extraction
            var matches = SymbolRegex.Matches(cleanLatex);
            foreach (Match m in matches)
            {
                string val = m.Value;
                string kind = "Identifier / Value";
                string meaning = val;

                if (val.StartsWith('\\'))
                {
                    kind = "LaTeX Operator / Symbol";
                    meaning = val switch
                    {
                        "\\frac" => "Fraction (numerator / denominator)",
                        "\\sum" => "Summation operator (∑)",
                        "\\int" => "Integral operator (∫)",
                        "\\sqrt" => "Radical square root (√)",
                        "\\pi" => "Constant Pi (π)",
                        "\\infty" => "Infinity symbol (∞)",
                        "\\alpha" or "\\beta" or "\\gamma" or "\\delta" or "\\theta" or "\\sigma" or "\\omega" => $"Greek letter ({val[1..]})",
                        _ => $"TeX Command {val}"
                    };
                }
                else if (val == "+" || val == "-" || val == "*" || val == "/" || val == "=")
                {
                    kind = "Binary Operator";
                }
                else if (val == "^")
                {
                    kind = "Superscript / Exponent";
                }
                else if (val == "_")
                {
                    kind = "Subscript";
                }

                tokens.Add(new MathSymbolToken(val, kind, meaning));
            }

            // 3. OMML XML generation
            string omml;
            try
            {
                var math = LatexToOmml.Build(cleanLatex);
                omml = math?.OuterXml ?? $"<m:oMath><m:r><m:t>{cleanLatex}</m:t></m:r></m:oMath>";
            }
            catch (Exception ex)
            {
                issues.Add($"OMML conversion notice: {ex.Message}");
                omml = $"<m:oMath><m:r><m:t>{cleanLatex}</m:t></m:r></m:oMath>";
            }

            return new InspectionResult(
                cleanLatex,
                isDisplayMode,
                issues.Count == 0,
                issues,
                tokens,
                omml);
        }
    }
}
