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
    public partial class ClipboardNormalizerService
    {
        [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
        private static partial Regex ScriptTagRegex();

        [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
        private static partial Regex StyleTagRegex();

        [GeneratedRegex(@"<!--[\s\S]*?-->")]
        private static partial Regex HtmlCommentRegex();

        [GeneratedRegex(@"<pre[^>]*>\s*<code(?:\s+class=""(?:language-)?([^""]+)"")?[^>]*>([\s\S]*?)</code>\s*</pre>", RegexOptions.IgnoreCase)]
        private static partial Regex PreCodeRegex();

        [GeneratedRegex(@"<pre[^>]*>([\s\S]*?)</pre>", RegexOptions.IgnoreCase)]
        private static partial Regex PreRegex();

        [GeneratedRegex(@"<code[^>]*>([\s\S]*?)</code>", RegexOptions.IgnoreCase)]
        private static partial Regex CodeRegex();

        [GeneratedRegex(@"<h([1-6])[^>]*>([\s\S]*?)</h\1>", RegexOptions.IgnoreCase)]
        private static partial Regex HeadingRegex();

        [GeneratedRegex(@"<(?:strong|b)[^>]*>([\s\S]*?)</(?:strong|b)>", RegexOptions.IgnoreCase)]
        private static partial Regex StrongRegex();

        [GeneratedRegex(@"<(?:em|i)[^>]*>([\s\S]*?)</(?:em|i)>", RegexOptions.IgnoreCase)]
        private static partial Regex EmRegex();

        [GeneratedRegex(@"<img[^>]*src=""([^""]+)""(?:[^>]*alt=""([^""]*)"")?[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex ImgRegex();

        [GeneratedRegex(@"<a[^>]*href=""([^""]+)""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase)]
        private static partial Regex aRegex();

        [GeneratedRegex(@"<hr[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex HrRegex();

        [GeneratedRegex(@"<blockquote[^>]*>([\s\S]*?)</blockquote>", RegexOptions.IgnoreCase)]
        private static partial Regex BlockquoteRegex();

        [GeneratedRegex(@"<li[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase)]
        private static partial Regex LiRegex();

        [GeneratedRegex(@"</?(?:ul|ol)[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex ListRegex();

        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
        private static partial Regex BrRegex();

        [GeneratedRegex(@"<p[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase)]
        private static partial Regex pRegex();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex StripTagsRegex();

        [GeneratedRegex(@"\n{3,}")]
        private static partial Regex NewlinesRegex();

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
            html = ScriptTagRegex().Replace(html, "");
            html = StyleTagRegex().Replace(html, "");
            html = HtmlCommentRegex().Replace(html, ""); // HTML comments

            // Code blocks: <pre><code class="language-xyz">...</code></pre> or <pre>...</pre>
            html = PreCodeRegex().Replace(html, m =>
            {
                string lang = m.Groups[1].Value.Trim();
                string code = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                return $"\n\n```{(string.IsNullOrEmpty(lang) ? "" : lang)}\n{code}\n```\n\n";
            });

            html = PreRegex().Replace(html, m =>
            {
                string code = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                return $"\n\n```\n{code}\n```\n\n";
            });

            // Inline code: <code>...</code>
            html = CodeRegex().Replace(html, m =>
            {
                string code = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                return $"`{code}`";
            });

            // Headings: <h1> to <h6>
            html = HeadingRegex().Replace(html, m =>
            {
                int level = m.Groups[1].Value[0] - '0';
                string hashes = new string('#', level);
                string text = StripTags(m.Groups[2].Value).Trim();
                return $"\n\n{hashes} {text}\n\n";
            });

            // Bold: <strong> or <b>
            html = StrongRegex().Replace(html, m =>
            {
                string text = m.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(text) ? "" : $"**{text}**";
            });

            // Italic: <em> or <i>
            html = EmRegex().Replace(html, m =>
            {
                string text = m.Groups[1].Value.Trim();
                return string.IsNullOrEmpty(text) ? "" : $"*{text}*";
            });

            // Images: <img src="..." alt="...">
            html = ImgRegex().Replace(html, m =>
            {
                string src = m.Groups[1].Value;
                string alt = m.Groups[2].Value;
                return $"![{alt}]({src})";
            });

            // Hyperlinks: <a href="...">...</a>
            html = aRegex().Replace(html, m =>
            {
                string href = m.Groups[1].Value.Trim();
                string text = StripTags(m.Groups[2].Value).Trim();
                return string.IsNullOrEmpty(text) ? href : $"[{text}]({href})";
            });

            // Horizontal rules: <hr>
            html = HrRegex().Replace(html, "\n\n---\n\n");

            // Blockquotes: <blockquote>...</blockquote>
            html = BlockquoteRegex().Replace(html, m =>
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
            });

            // Lists: <ul>, <ol>, <li>
            html = LiRegex().Replace(html, m =>
            {
                string text = StripTags(m.Groups[1].Value).Trim();
                return $"\n- {text}";
            });

            html = ListRegex().Replace(html, "\n\n");

            // Paragraphs and breaks
            html = BrRegex().Replace(html, "\n");
            html = pRegex().Replace(html, m =>
            {
                string text = m.Groups[1].Value.Trim();
                return $"\n\n{text}\n\n";
            });

            // Strip remaining HTML tags
            html = StripTags(html);

            // Decode HTML entities
            html = WebUtility.HtmlDecode(html);

            // Clean up excessive blank lines
            html = NewlinesRegex().Replace(html, "\n\n").Trim();

            return html;
        }

        private string StripTags(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return StripTagsRegex().Replace(input, "");
        }
    }
}
