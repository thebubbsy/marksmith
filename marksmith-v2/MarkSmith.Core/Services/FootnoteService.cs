using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    public class FootnoteItem
    {
        public string Key { get; set; } = string.Empty;
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Processes Markdown footnotes ([^1]) and definitions ([^1]: text),
    /// re-indexing them sequentially and appending formatted footnote sections.
    /// </summary>
    public class FootnoteService
    {
        /// <summary>
        /// Processes footnotes in Markdown content, returning transformed HTML snippet or Markdown.
        /// </summary>
        public string ProcessFootnotes(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

            // 1. Extract definitions: [^key]: definition text
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var defPattern = new Regex(@"^\[\^([^\]]+)\]:\s*(.+)$", RegexOptions.Multiline);
            
            string content = defPattern.Replace(markdown, m =>
            {
                string key = m.Groups[1].Value.Trim();
                string text = m.Groups[2].Value.Trim();
                definitions[key] = text;
                return string.Empty; // Remove definition line from body
            });

            if (definitions.Count == 0)
            {
                return markdown;
            }

            // 2. Extract inline references in order of appearance
            var refPattern = new Regex(@"\[\^([^\]]+)\]");
            var referencedKeys = new List<string>();
            var keyToIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string processedBody = refPattern.Replace(content, m =>
            {
                string key = m.Groups[1].Value.Trim();
                if (!definitions.ContainsKey(key))
                {
                    return m.Value; // Keep unresolved reference as-is
                }

                if (!keyToIndexMap.ContainsKey(key))
                {
                    referencedKeys.Add(key);
                    keyToIndexMap[key] = referencedKeys.Count;
                }

                int index = keyToIndexMap[key];
                return $"<sup class=\"footnote-ref\"><a href=\"#fn-{key}\" id=\"fnref-{key}\">[{index}]</a></sup>";
            });

            if (referencedKeys.Count == 0)
            {
                return content.Trim();
            }

            // 3. Build Footnotes Section HTML
            var sb = new StringBuilder();
            sb.AppendLine(processedBody.Trim());
            sb.AppendLine();
            sb.AppendLine("<section class=\"footnotes\" aria-label=\"Footnotes\">");
            sb.AppendLine("  <hr class=\"footnotes-sep\" />");
            sb.AppendLine("  <ol class=\"footnotes-list\">");

            foreach (var key in referencedKeys)
            {
                int index = keyToIndexMap[key];
                string text = definitions[key];
                sb.AppendLine($"    <li id=\"fn-{key}\" class=\"footnote-item\">");
                sb.AppendLine($"      <p>{text} <a href=\"#fnref-{key}\" class=\"footnote-backref\" aria-label=\"Back to content\">↩</a></p>");
                sb.AppendLine("    </li>");
            }

            sb.AppendLine("  </ol>");
            sb.AppendLine("</section>");

            return sb.ToString().Trim();
        }
    }
}
