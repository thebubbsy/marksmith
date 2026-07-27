using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class WordCloudItem
    {
        public string Word { get; set; } = string.Empty;
        public int Frequency { get; set; }
        public double Weight { get; set; }
        public double ScaledFontSizePx { get; set; }
    }

    /// <summary>
    /// Generates weighted word frequency data with font size scaling metrics for word cloud visualization, excluding stop words and markdown formatting.
    /// </summary>
    public class WordCloudGeneratorService
    {
        private static readonly HashSet<string> DefaultStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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
            "is", "are", "was", "were", "been", "has", "had", "should", "may", "might", "must"
        };

        public List<WordCloudItem> Generate(string markdown, int maxWords = 50, double minFontSizePx = 12.0, double maxFontSizePx = 48.0)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return new List<WordCloudItem>();

            // 1. Strip code fences & inline code
            string cleaned = Regex.Replace(markdown, @"```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\n]+`", " ");

            // 2. Strip HTML tags
            cleaned = Regex.Replace(cleaned, @"<[^>]+>", " ");

            // 3. Strip Markdown header markers, links, bold, italic
            cleaned = Regex.Replace(cleaned, @"^#+\s+", "", RegexOptions.Multiline);
            cleaned = Regex.Replace(cleaned, @"\[([^\]]+)\]\([^\)]+\)", "$1");
            cleaned = Regex.Replace(cleaned, @"[\*\_]{1,3}", " ");

            // 4. Tokenize into words
            var tokens = Regex.Matches(cleaned, @"\b[a-zA-Z]{3,}\b")
                              .Cast<Match>()
                              .Select(m => m.Value.ToLowerInvariant())
                              .Where(w => !DefaultStopWords.Contains(w))
                              .ToList();

            if (!tokens.Any())
                return new List<WordCloudItem>();

            // 5. Calculate frequencies
            var frequencies = tokens.GroupBy(w => w)
                                     .Select(g => new { Word = g.Key, Count = g.Count() })
                                     .OrderByDescending(x => x.Count)
                                     .Take(maxWords)
                                     .ToList();

            int maxCount = frequencies.First().Count;
            int minCount = frequencies.Last().Count;
            int countRange = Math.Max(1, maxCount - minCount);
            double fontSizeRange = maxFontSizePx - minFontSizePx;

            var result = new List<WordCloudItem>();
            foreach (var item in frequencies)
            {
                double weight = (double)(item.Count - minCount) / countRange;
                if (maxCount == minCount) weight = 1.0;

                double scaledSize = minFontSizePx + (weight * fontSizeRange);

                result.Add(new WordCloudItem
                {
                    Word = item.Word,
                    Frequency = item.Count,
                    Weight = Math.Round(weight, 3),
                    ScaledFontSizePx = Math.Round(scaledSize, 1)
                });
            }

            return result;
        }
    }
}
