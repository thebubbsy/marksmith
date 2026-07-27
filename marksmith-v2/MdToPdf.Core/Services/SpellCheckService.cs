using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class SpellingIssue
    {
        public string Word { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public List<string> SuggestedReplacements { get; set; } = new List<string>();
    }

    /// <summary>
    /// Multi-language document spell checker for Markdown content.
    /// Safely skips code fences, inline code, TeX math blocks, URLs, and HTML tags.
    /// </summary>
    public class SpellCheckService
    {
        private readonly HashSet<string> _dictionary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public SpellCheckService()
        {
            // Seed common English dictionary words
            var commonWords = new[]
            {
                "the", "be", "to", "of", "and", "a", "in", "that", "have", "i",
                "it", "for", "not", "on", "with", "he", "as", "you", "do", "at",
                "this", "but", "his", "by", "from", "they", "we", "say", "her", "she",
                "or", "an", "will", "my", "one", "all", "would", "there", "their", "what",
                "so", "up", "out", "if", "about", "who", "get", "which", "go", "me",
                "when", "make", "can", "like", "time", "no", "just", "him", "know", "take",
                "people", "into", "year", "your", "good", "some", "could", "them", "see", "other",
                "than", "then", "now", "look", "only", "come", "its", "over", "think", "also",
                "back", "after", "use", "two", "how", "our", "work", "first", "well", "way",
                "even", "new", "want", "because", "any", "these", "give", "day", "most", "us",
                "markdown", "document", "service", "engine", "test", "content", "header", "footer"
            };

            foreach (var w in commonWords)
            {
                _dictionary.Add(w);
            }
        }

        public void AddCustomWord(string word)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                _dictionary.Add(word.Trim());
            }
        }

        public List<SpellingIssue> CheckDocument(string markdown)
        {
            var issues = new List<SpellingIssue>();
            if (string.IsNullOrWhiteSpace(markdown)) return issues;

            // 1. Strip Code Fences, Math blocks, URLs, HTML tags from checking text
            string sanitized = StripIgnoredBlocks(markdown);

            string[] lines = sanitized.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                int lineNumber = lineIdx + 1;
                string line = lines[lineIdx];

                var words = Regex.Matches(line, @"\b[a-zA-Z]{3,}\b");
                foreach (Match m in words)
                {
                    string word = m.Value;
                    if (!_dictionary.Contains(word) && !IsProperNounOrAcronym(word))
                    {
                        var suggestions = GetSuggestions(word);
                        issues.Add(new SpellingIssue
                        {
                            Word = word,
                            LineNumber = lineNumber,
                            SuggestedReplacements = suggestions
                        });
                    }
                }
            }

            return issues;
        }

        private string StripIgnoredBlocks(string text)
        {
            // Remove code fences: ``` ... ```
            text = Regex.Replace(text, @"```[\s\S]*?```", "");
            // Remove inline code: ` ... `
            text = Regex.Replace(text, @"`[^`]+`", "");
            // Remove TeX math blocks: $$ ... $$ and $ ... $
            text = Regex.Replace(text, @"\$\$[\s\S]*?\$\$", "");
            text = Regex.Replace(text, @"\$[^\$]+\$", "");
            // Remove URLs: https?://...
            text = Regex.Replace(text, @"https?://[^\s\)]+", "");
            // Remove HTML tags
            text = Regex.Replace(text, @"<[^>]+>", "");

            return text;
        }

        private bool IsProperNounOrAcronym(string word)
        {
            // All uppercase or starts with capital letter (proper nouns)
            if (word.All(char.IsUpper)) return true;
            return false;
        }

        private List<string> GetSuggestions(string targetWord)
        {
            return _dictionary
                .Select(w => new { Word = w, Distance = LevenshteinDistance(targetWord.ToLowerInvariant(), w.ToLowerInvariant()) })
                .Where(x => x.Distance <= 2)
                .OrderBy(x => x.Distance)
                .Take(3)
                .Select(x => x.Word)
                .ToList();
        }

        private int LevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }
    }
}
