namespace MarkSmith.Services;

// The status-line sentence for a studio copy that didn't reach the clipboard. The Shape, SmartArt
// and Mind Map studios used to discard the clipboard result and then say "Copied …" anyway, so a
// busy clipboard left a false confirmation on the status line.
public static class CopyStatusText
{
    // message: what the status line would have said on success ("Copied angle: 45°", or a result
    // such as "✓ 3 shapes off the grid" whose text is also copied). error: ClipboardWriter's reason.
    public static string Failed(string? message, string error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "The clipboard is busy. Try again in a moment." : error.Trim();
        var text = (message ?? string.Empty).Trim();
        if (text.StartsWith("✓ ", StringComparison.Ordinal)) text = text[2..].TrimStart();

        if (text.StartsWith("Copied", StringComparison.OrdinalIgnoreCase))
        {
            var what = text[6..].TrimStart(':', ' ').TrimEnd('.', ' ');
            foreach (var tail in new[] { " to the clipboard", " to clipboard" })
            {
                if (what.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                {
                    what = what[..^tail.Length].TrimEnd();
                    break;
                }
            }
            return what.Length == 0 ? $"Nothing was copied. {reason}" : $"Couldn't copy {what}. {reason}";
        }

        // A result whose text is also put on the clipboard: keep the result, say the copy failed.
        return text.Length == 0
            ? $"Nothing was copied. {reason}"
            : $"{text.TrimEnd('.', ' ')}. Not copied to the clipboard: {reason}";
    }

    // The status line after a copy that worked. A plain "Copied …" message is kept. A command result
    // whose text is also copied (an audit summary, a layout report) used to say nothing about the
    // copy, so the user didn't know it had replaced what they had on the clipboard; it now says so.
    public static string Succeeded(string? message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0) return "Copied to the clipboard.";
        var bare = text.StartsWith("✓ ", StringComparison.Ordinal) ? text[2..].TrimStart() : text;
        if (bare.StartsWith("Copied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }
        return $"{text.TrimEnd('.', ' ')} · copied to the clipboard";
    }
}
