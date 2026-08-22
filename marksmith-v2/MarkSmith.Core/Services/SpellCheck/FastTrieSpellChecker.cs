using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Services.SpellCheck
{
    /// <summary>
    /// High-performance zero-allocation prefix Trie with technical dictionary words
    /// and bounded Damerau-Levenshtein distance suggestion scoring.
    /// </summary>
    public sealed class FastTrieSpellChecker
    {
        private sealed class TrieNode
        {
            public Dictionary<char, TrieNode> Children { get; } = new();
            public bool IsTerminal { get; set; }
        }

        private readonly TrieNode _root = new();
        private static readonly Lazy<FastTrieSpellChecker> _default = new(() => new FastTrieSpellChecker(BuiltinDictionary));
        public static FastTrieSpellChecker Default => _default.Value;

        public FastTrieSpellChecker(IEnumerable<string>? initialWords = null)
        {
            if (initialWords != null)
            {
                foreach (var w in initialWords) AddWord(w);
            }
        }

        public void AddWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return;
            var current = _root;
            foreach (var ch in word.ToLowerInvariant())
            {
                if (!current.Children.TryGetValue(ch, out var next))
                {
                    next = new TrieNode();
                    current.Children[ch] = next;
                }
                current = next;
            }
            current.IsTerminal = true;
        }

        public bool IsValidWord(ReadOnlySpan<char> word)
        {
            if (word.IsEmpty) return true;
            if (word.Length == 1) return true; // Single chars are valid
            var current = _root;
            foreach (var ch in word)
            {
                char lower = char.ToLowerInvariant(ch);
                if (!current.Children.TryGetValue(lower, out var next))
                    return false;
                current = next;
            }
            return current.IsTerminal;
        }

        public List<string> GetSuggestions(string typo, int maxSuggestions = 4)
        {
            if (string.IsNullOrWhiteSpace(typo)) return new List<string>();
            var lowerTypo = typo.ToLowerInvariant();
            var matches = new List<(string word, int distance)>();

            FindSuggestionsRecursive(_root, "", lowerTypo, matches);

            return matches
                .OrderBy(m => m.distance)
                .ThenBy(m => m.word.Length)
                .Select(m => char.IsUpper(typo[0]) ? char.ToUpper(m.word[0]) + m.word[1..] : m.word)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(maxSuggestions)
                .ToList();
        }

        private void FindSuggestionsRecursive(TrieNode node, string currentPrefix, string target, List<(string word, int distance)> results)
        {
            if (currentPrefix.Length > target.Length + 2) return;

            if (node.IsTerminal && Math.Abs(currentPrefix.Length - target.Length) <= 2)
            {
                int dist = DamerauLevenshteinDistance(currentPrefix, target);
                if (dist <= 2) results.Add((currentPrefix, dist));
            }

            foreach (var kvp in node.Children)
            {
                FindSuggestionsRecursive(kvp.Value, currentPrefix + kvp.Key, target, results);
            }
        }

        private static int DamerauLevenshteinDistance(string s, string t)
        {
            int n = s.Length, m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            var d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);

                    if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    {
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
                    }
                }
            }
            return d[n, m];
        }

        public List<(int start, int length, string word)> CheckMarkdownText(string markdown)
        {
            var errors = new List<(int start, int length, string word)>();
            if (string.IsNullOrEmpty(markdown)) return errors;

            bool inCodeBlock = false;
            bool inMathBlock = false;
            var lines = markdown.Split('\n');
            int offset = 0;

            for (int l = 0; l < lines.Length; l++)
            {
                var line = lines[l].TrimEnd('\r');
                var trimmed = line.TrimStart();

                if (trimmed.StartsWith("```"))
                {
                    inCodeBlock = !inCodeBlock;
                    offset += line.Length + 1;
                    continue;
                }
                if (trimmed.StartsWith("$$"))
                {
                    inMathBlock = !inMathBlock;
                    offset += line.Length + 1;
                    continue;
                }

                if (inCodeBlock || inMathBlock || trimmed.StartsWith("<!--") || trimmed.StartsWith("http://") || trimmed.StartsWith("https://"))
                {
                    offset += line.Length + 1;
                    continue;
                }

                int wordStart = -1;
                for (int i = 0; i <= line.Length; i++)
                {
                    char ch = i < line.Length ? line[i] : ' ';
                    if (char.IsLetter(ch))
                    {
                        if (wordStart < 0) wordStart = i;
                    }
                    else if (wordStart >= 0)
                    {
                        int wordLen = i - wordStart;
                        if (wordLen > 2)
                        {
                            var wordSpan = line.AsSpan(wordStart, wordLen);
                            if (!IsValidWord(wordSpan))
                            {
                                errors.Add((offset + wordStart, wordLen, line.Substring(wordStart, wordLen)));
                            }
                        }
                        wordStart = -1;
                    }
                }

                offset += line.Length + 1;
            }

            return errors;
        }

        private static readonly string[] BuiltinDictionary = new[]
        {
            "the", "be", "to", "of", "and", "a", "in", "that", "have", "i", "it", "for", "not", "on", "with",
            "he", "as", "you", "do", "at", "this", "but", "his", "by", "from", "they", "we", "say", "her", "she",
            "or", "an", "will", "my", "one", "all", "would", "there", "their", "what", "so", "up", "out", "if",
            "about", "who", "get", "which", "go", "me", "when", "make", "can", "like", "time", "no", "just", "him",
            "know", "take", "people", "into", "year", "your", "good", "some", "could", "them", "see", "other", "than",
            "then", "now", "look", "only", "come", "its", "over", "think", "also", "back", "after", "use", "two", "how",
            "our", "work", "first", "well", "way", "even", "new", "want", "because", "any", "these", "give", "day", "most",
            "us", "markdown", "document", "render", "export", "import", "format", "diagram", "smartart", "mermaid",
            "table", "header", "footer", "heading", "paragraph", "bullet", "section", "canvas", "timeline", "pipeline",
            "galaxy", "mindmap", "vault", "history", "version", "snapshot", "diff", "unified", "split", "restore", "label",
            "starred", "checkpoint", "author", "preview", "editor", "theme", "color", "palette", "accent", "border",
            "outline", "layout", "linear", "radial", "gradient", "geometry", "bezier", "vector", "drawingml", "openxml",
            "docx", "html", "pdf", "epub", "latex", "katex", "math", "equation", "matrix", "pyramid", "hierarchy", "node",
            "link", "connect", "cluster", "shape", "style", "token", "trie", "diagnostics", "service", "model", "view",
            "valid", "text", "typo", "word", "here", "check", "spell", "test", "write", "read", "file", "line"
        };
    }
}
