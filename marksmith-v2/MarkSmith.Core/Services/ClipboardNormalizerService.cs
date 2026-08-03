using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    /// <summary>
    /// Normalizes rich text HTML content (e.g. copied from web browsers or MS Word)
    /// into clean, standard Markdown syntax.
    /// </summary>
    public class ClipboardNormalizerService
    {
        /// <summary>
        /// Normalizes HTML or plain text clipboard content into Markdown format.
        /// </summary>
        public string NormalizeHtmlToMarkdown(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
                return string.Empty;

            // Detect if input contains HTML markup
            if (!rawInput.Contains("<") || !rawInput.Contains(">"))
            {
                return rawInput.Trim();
            }

            string html = rawInput;

            // Strip CF_HTML header metadata if present (from Windows Clipboard)
            int htmlStart = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            if (htmlStart < 0) htmlStart = html.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            if (htmlStart >= 0)
            {
                html = html.Substring(htmlStart);
            }

            // Remove script and style blocks
            html = Regex.Replace(html, @"<script[^>]*>[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style[^>]*>[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--[\s\S]*?-->", ""); // HTML comments

            // Code blocks: <pre><code class="language-xyz">...</code></pre> or <pre>...</pre>
            html = Regex.Replace(html, @"<pre[^>]*>\s*<code(?:\s+class=""(?:language-)?([^""]+)"")?[^>]*>([\s\S]*?)</code>\s*</pre>", m =>
            {
                string lang = m.Groups[1].Value.Trim();
                string code = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                return $"\n\n```{(string.IsNullOrEmpty(lang) ? "" : lang)}\n{code}\n```\n\n";
            }, RegexOptions.IgnoreCase);

            html = Regex.Replace(html, @"<pre[^>]*>([\s\S]*?)</pre>", m =>
            {
                string code = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                return $"\n\n```\n{code}\n```\n\n";
            }, RegexOptions.IgnoreCase);

            // Inline code: <code>...</code>
            html = Regex.Replace(html, @"<code[^>]*>([\s\S]*?)</code>", m =>
            {
                string code = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                return $"`{code}`";
            }, RegexOptions.IgnoreCase);

            // Headings: <h1> to <h6>
            for (int level = 6; level >= 1; level--)
            {
                string hashes = new string('#', level);
                html = Regex.Replace(html, $@"<h{level}[^>]*>([\s\S]*?)</h{level}>", m =>
                {
                    string text = StripTags(m.Groups[1].Value).Trim();
                    return $"\n\n{hashes} {text}\n\n";
                }, RegexOptions.IgnoreCase);
            }

            // Bold: <strong> or <b>
            html = Regex.Replace(html, @"<(?:strong|b)[^>]*>([\s\S]*?)</(?:strong|b)>", m =>
            {
                string text = m.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(text) ? "" : $"**{text}**";
            }, RegexOptions.IgnoreCase);

            // Italic: <em> or <i>
            html = Regex.Replace(html, @"<(?:em|i)[^>]*>([\s\S]*?)</(?:em|i)>", m =>
            {
                string text = m.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(text) ? "" : $"*{text}*";
            }, RegexOptions.IgnoreCase);

            // Images: <img src="..." alt="...">
            html = Regex.Replace(html, @"<img[^>]*src=""([^""]+)""(?:[^>]*alt=""([^""]*)"")?[^>]*>", m =>
            {
                string src = m.Groups[1].Value;
                string alt = m.Groups[2].Value;
                return $"![{alt}]({src})";
            }, RegexOptions.IgnoreCase);

            // Hyperlinks: <a href="...">...</a>
            html = Regex.Replace(html, @"<a[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>", m =>
            {
                string href = m.Groups[1].Value.Trim();
                string text = StripTags(m.Groups[2].Value).Trim();
                return string.IsNullOrEmpty(text) ? href : $"[{text}]({href})";
            }, RegexOptions.IgnoreCase);

            // Horizontal rules: <hr>
            html = Regex.Replace(html, @"<hr[^>]*>", "\n\n---\n\n", RegexOptions.IgnoreCase);

            // Blockquotes: <blockquote>...</blockquote>
            html = Regex.Replace(html, @"<blockquote[^>]*>([\s\S]*?)</blockquote>", m =>
            {
                string inner = StripTags(m.Groups[1].Value).Trim();
                string[] lines = inner.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                StringBuilder sb = new StringBuilder("\n\n");
                foreach (var line in lines)
                {
                    sb.AppendLine($"> {line.Trim()}");
                }
                sb.AppendLine();
                return sb.ToString();
            }, RegexOptions.IgnoreCase);

            // Lists: <ul>, <ol>, <li>
            html = Regex.Replace(html, @"<li[^>]*>([\s\S]*?)</li>", m =>
            {
                string text = StripTags(m.Groups[1].Value).Trim();
                return $"\n- {text}";
            }, RegexOptions.IgnoreCase);

            html = Regex.Replace(html, @"</?(?:ul|ol)[^>]*>", "\n\n", RegexOptions.IgnoreCase);

            // Paragraphs and breaks
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<p[^>]*>([\s\S]*?)</p>", m =>
            {
                string text = m.Groups[1].Value.Trim();
                return $"\n\n{text}\n\n";
            }, RegexOptions.IgnoreCase);

            // Strip remaining HTML tags
            html = StripTags(html);

            // Decode HTML entities
            html = WebUtility.HtmlDecode(html);

            // Clean up excessive blank lines
            html = Regex.Replace(html, @"\n{3,}", "\n\n").Trim();

            return html;
        }

        private string StripTags(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return Regex.Replace(input, @"<[^>]+>", "");
        }
    }
}
