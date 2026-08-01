using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class CodeBlockOptions
    {
        public string Language { get; set; } = string.Empty;
        public bool ShowLineNumbers { get; set; }
        public HashSet<int> HighlightedLines { get; set; } = new HashSet<int>();
    }

    /// <summary>
    /// Parses fence attributes (e.g. ```csharp {1,3-5} showLineNumbers)
    /// to synthesize line numbers and line highlighting in rendered code blocks.
    /// </summary>
    public class CodeBlockHighlighterService
    {
        /// <summary>
        /// Parses metadata options from fence header line.
        /// e.g. "csharp {1,3-5} showLineNumbers"
        /// </summary>
        public CodeBlockOptions ParseFenceOptions(string fenceHeader)
        {
            var options = new CodeBlockOptions();
            if (string.IsNullOrWhiteSpace(fenceHeader)) return options;

            string header = fenceHeader.Trim();

            // Language is the first token
            var matchLang = Regex.Match(header, @"^([a-zA-Z0-9_\-\+]+)");
            if (matchLang.Success)
            {
                options.Language = matchLang.Groups[1].Value;
            }

            // Line numbers flag
            if (header.Contains("showLineNumbers", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("line-numbers", StringComparison.OrdinalIgnoreCase))
            {
                options.ShowLineNumbers = true;
            }

            // Highlighted lines range: {1,3-5}
            var rangeMatch = Regex.Match(header, @"\{([\d\-\,\s]+)\}");
            if (rangeMatch.Success)
            {
                string rangeSpec = rangeMatch.Groups[1].Value;
                string[] parts = rangeSpec.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var part in parts)
                {
                    string p = part.Trim();
                    if (p.Contains("-"))
                    {
                        string[] range = p.Split('-');
                        if (range.Length == 2 && int.TryParse(range[0], out int start) && int.TryParse(range[1], out int end))
                        {
                            for (int i = start; i <= end; i++)
                            {
                                options.HighlightedLines.Add(i);
                            }
                        }
                    }
                    else if (int.TryParse(p, out int lineNum))
                    {
                        options.HighlightedLines.Add(lineNum);
                    }
                }
            }

            return options;
        }

        /// <summary>
        /// Renders code block HTML with optional line numbers and line highlights.
        /// </summary>
        public string RenderCodeBlock(string rawCode, CodeBlockOptions options)
        {
            if (rawCode == null) rawCode = string.Empty;
            options ??= new CodeBlockOptions();

            string[] lines = rawCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();

            sb.AppendLine($"<pre class=\"code-block {(string.IsNullOrEmpty(options.Language) ? "" : "language-" + options.Language)}\">");
            sb.AppendLine("  <code>");

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNum = i + 1;
                bool isHighlighted = options.HighlightedLines.Contains(lineNum);
                string encodedLine = WebUtility.HtmlEncode(lines[i]);

                string lineClass = "code-line" + (isHighlighted ? " highlighted-line" : "");
                sb.Append($"    <div class=\"{lineClass}\">");

                if (options.ShowLineNumbers || options.HighlightedLines.Count > 0)
                {
                    sb.Append($"<span class=\"line-num\">{lineNum,3}</span> ");
                }

                sb.Append($"<span class=\"line-content\">{encodedLine}</span>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("  </code>");
            sb.AppendLine("</pre>");

            return sb.ToString().Trim();
        }
    }
}
