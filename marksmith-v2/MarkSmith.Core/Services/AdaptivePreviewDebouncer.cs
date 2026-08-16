using System;

namespace MarkSmith.Services
{
    /// <summary>
    /// Computes dynamic, complexity-scaled typing debounce durations based on document size
    /// and AST features (math, Mermaid, DrawingML shapes, SmartArt).
    /// Prevents CPU throttling on massive technical documents while preserving ultra-low
    /// latency on simple notes.
    /// </summary>
    public static class AdaptivePreviewDebouncer
    {
        public const int MinDebounceMs = 40;
        public const int DefaultDebounceMs = 80;
        public const int MaxDebounceMs = 250;

        public static int ComputeDebounceMilliseconds(string? markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return MinDebounceMs;

            int len = markdown.Length;
            int debounce = DefaultDebounceMs;

            if (len < 2000)
            {
                debounce = MinDebounceMs;
            }
            else if (len > 50000)
            {
                debounce = 180;
            }
            else if (len > 20000)
            {
                debounce = 120;
            }

            // Check for heavy diagram / math features that require extra solve time
            if (markdown.Contains(":::smartart", StringComparison.OrdinalIgnoreCase) ||
                markdown.Contains("```mermaid", StringComparison.OrdinalIgnoreCase) ||
                markdown.Contains(":::shapes", StringComparison.OrdinalIgnoreCase) ||
                markdown.Contains("$$", StringComparison.Ordinal))
            {
                debounce = Math.Min(MaxDebounceMs, debounce + 60);
            }

            return Math.Clamp(debounce, MinDebounceMs, MaxDebounceMs);
        }

        public static TimeSpan ComputeDebounceTimeSpan(string? markdown)
            => TimeSpan.FromMilliseconds(ComputeDebounceMilliseconds(markdown));
    }
}
