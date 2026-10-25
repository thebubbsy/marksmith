using System;

namespace MarkSmith.Services;

// How long a self-closing notice stays up. A fixed 3 or 6 seconds suits "Copied." but cuts off a
// two-line reason ("Couldn't copy the diff patch: the clipboard is in use by another app…") before
// it can be read, so the time grows with the word count at a comfortable reading pace.
public static class NoticeTiming
{
    // About 240 words a minute, after a second to notice the message appeared.
    private const double LeadSeconds = 1.0;
    private const double SecondsPerWord = 0.25;
    private const double CeilingSeconds = 12.0;

    // Never shorter than minimumSeconds (each surface keeps its old timing for short messages), and
    // never longer than 12 seconds unless the minimum itself is longer.
    public static TimeSpan For(string? message, double minimumSeconds)
    {
        minimumSeconds = Math.Max(0, minimumSeconds);
        var seconds = LeadSeconds + WordCount(message) * SecondsPerWord;
        seconds = Math.Min(seconds, Math.Max(CeilingSeconds, minimumSeconds));
        return TimeSpan.FromSeconds(Math.Max(seconds, minimumSeconds));
    }

    public static int WordCount(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? 0
            : message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
