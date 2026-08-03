using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    /// <summary>
    /// Converts straight quotes, apostrophes, dashes, and ellipses into typographically correct smart punctuation outside code and math blocks.
    /// </summary>
    public class SmartPunctuationService
    {
        private static readonly Regex CodeFenceRegex = new Regex(@"(```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]+`|\$\$[\s\S]*?\$\$|\$[^$\n]+\$|<[^>]+>)", RegexOptions.Compiled);

        public string Process(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return markdown ?? string.Empty;

            var sb = new StringBuilder();
            int lastIdx = 0;

            var matches = CodeFenceRegex.Matches(markdown);
            foreach (Match match in matches)
            {
                if (match.Index > lastIdx)
                {
                    string textToProcess = markdown.Substring(lastIdx, match.Index - lastIdx);
                    sb.Append(ConvertTypography(textToProcess));
                }

                sb.Append(match.Value);
                lastIdx = match.Index + match.Length;
            }

            if (lastIdx < markdown.Length)
            {
                string textToProcess = markdown.Substring(lastIdx);
                sb.Append(ConvertTypography(textToProcess));
            }

            return sb.ToString();
        }

        private string ConvertTypography(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // 1. Convert ellipses
            text = text.Replace("...", "…");

            // 2. Convert em-dash and en-dash
            text = text.Replace("---", "—");
            text = text.Replace("--", "–");

            // 3. Convert double quotes ("word" -> “word”)
            text = Regex.Replace(text, @"(?<=\s|^|\(|\["")""(?=\S)", "“");
            text = Regex.Replace(text, @"(?<=\S)""(?=\s|$|\)|\.|,|\!|\?|;|\:])", "”");
            // Generic double quote fallback
            bool openDouble = true;
            var sbDouble = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    sbDouble.Append(openDouble ? '“' : '”');
                    openDouble = !openDouble;
                }
                else
                {
                    sbDouble.Append(text[i]);
                }
            }
            text = sbDouble.ToString();

            // 4. Convert single quotes and apostrophes ('word' -> ‘word’, it's -> it’s)
            text = Regex.Replace(text, @"(?<=\w)'(?=\w)", "’"); // apostrophe (e.g. it's, don't)
            text = Regex.Replace(text, @"(?<=\s|^|\(|\[)'(?=\S)", "‘"); // open single quote
            text = Regex.Replace(text, @"(?<=\S)'(?=\s|$|\)|\.|,|\!|\?|;|\:])", "’"); // close single quote

            bool openSingle = true;
            var sbSingle = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\'')
                {
                    sbSingle.Append(openSingle ? '‘' : '’');
                    openSingle = !openSingle;
                }
                else
                {
                    sbSingle.Append(text[i]);
                }
            }
            text = sbSingle.ToString();

            return text;
        }
    }
}
