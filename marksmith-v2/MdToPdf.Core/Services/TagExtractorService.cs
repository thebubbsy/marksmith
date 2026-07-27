using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class TagExtractionResult
    {
        public List<string> Hashtags { get; set; } = new List<string>();
        public List<string> KeyPhrases { get; set; } = new List<string>();
    }

    public class TagExtractorService
    {
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
            string cleaned = Regex.Replace(markdown, @"```[\s\S]*?```", " ");
            // Strip inline code
            cleaned = Regex.Replace(cleaned, @"`[^`]*`", " ");
            // Strip URLs
            cleaned = Regex.Replace(cleaned, @"https?://\S+", " ");

            // Extract hashtags (e.g. #tag or #multi-word-tag)
            var tagMatches = Regex.Matches(cleaned, @"(?<=\s|^)#([a-zA-Z0-9_\-]+)");
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
            string plainText = Regex.Replace(cleaned, @"[#*\_~`>\[\]\(\)]", " ");
            string[] words = Regex.Split(plainText.ToLower(), @"\W+");

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
