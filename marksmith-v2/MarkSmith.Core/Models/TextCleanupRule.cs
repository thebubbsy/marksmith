namespace MarkSmith.Models;

/// <summary>A user-authored find/replace cleanup rule for the AI normalization pass.</summary>
public sealed class TextCleanupRule
{
    /// <summary>Text to find (plain, case-insensitive) or a regex pattern when <see cref="IsRegex"/>.</summary>
    public string Find { get; set; } = "";

    /// <summary>Replacement (empty = strip the matched text). Regex captures ($1) supported when IsRegex.</summary>
    public string Replace { get; set; } = "";

    /// <summary>When true, Find is treated as a regular expression (case-insensitive).</summary>
    public bool IsRegex { get; set; }
}
