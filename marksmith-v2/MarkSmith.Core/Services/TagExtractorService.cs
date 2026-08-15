using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    public class TagExtractionResult
    {
        public List<string> Hashtags { get; set; } = new List<string>();
        public List<string> KeyPhrases { get; set; } = new List<string>();
    }

    public class TagExtractorService
    {
        // Compiled once — every Extract call used to rebuild these six interpreted regexes
        // over the whole document.
        private static readonly Regex FenceStrip = new(@"```[\s\S]*?```", RegexOptions.Compiled);
        private static readonly Regex InlineCodeStrip = new(@"`[^`]*`", RegexOptions.Compiled);
        private static readonly Regex UrlStrip = new(@"https?://\S+", RegexOptions.Compiled);
        private static readonly Regex HashtagRe = new(@"(?<=\s|^)#([a-zA-Z0-9_\-]+)", RegexOptions.Compiled);
        private static readonly Regex MdSymbolStrip = new(@"[#*_~`>\[\]\(\)]", RegexOptions.Compiled);
        private static readonly Regex NonWordSplit = new(@"\W+", RegexOptions.Compiled);

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "with",
            "by", "about", "against", "between", "into", "through", "during", "before",
            "after", "above", "below", "from", "up", "down", "of", "off", "over", "under",
            "again", "further", "then", "once", "here", "there", "when", "where", "why",
            "how", "all", "any", "both", "each", "few", "more", "most", "other", "some",
            "such", "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very",
            "s", "t", "can", "will", "just", "don", "should", "now", "is", "are", "was", "were",
            "be", "been", "being", "have", "has", "had", "having", "do", "does", "did", "doing",
            "it", "its", "this", "that", "these", "those"
        };

        public TagExtractionResult Extract(string markdown)
        {
            var result = new TagExtractionResult();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                return result;
            }

            // Strip code fences
            string cleaned = FenceStrip.Replace(markdown, " ");
            // Strip inline code
            cleaned = InlineCodeStrip.Replace(cleaned, " ");
            // Strip URLs
            cleaned = UrlStrip.Replace(cleaned, " ");

            // Extract hashtags (e.g. #tag or #multi-word-tag)
            var tagMatches = HashtagRe.Matches(cleaned);
            var tagsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in tagMatches)
            {
                string tag = match.Groups[1].Value;
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tagsSet.Add(tag);
                }
            }
            result.Hashtags = tagsSet.OrderBy(t => t).ToList();

            // Strip headings formatting, markdown symbols
            string plainText = MdSymbolStrip.Replace(cleaned, " ");
            string[] words = NonWordSplit.Split(plainText.ToLower());

            var wordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words)
            {
                if (word.Length > 2 && !StopWords.Contains(word) && !int.TryParse(word, out _))
                {
                    if (wordCounts.ContainsKey(word))
                        wordCounts[word]++;
                    else
                        wordCounts[word] = 1;
                }
            }

            result.KeyPhrases = wordCounts
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .Take(10)
                .Select(kvp => kvp.Key)
                .ToList();

            return result;
        }
    }
}
