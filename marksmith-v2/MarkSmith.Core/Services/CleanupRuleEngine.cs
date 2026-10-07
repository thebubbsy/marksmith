using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Models;

namespace MarkSmith.Services;

/// <summary>What one custom cleanup rule did to a document: how many matches it replaced, or why it
/// couldn't run.</summary>
public readonly record struct CleanupRuleOutcome(int Matches, string? Error)
{
    public static readonly CleanupRuleOutcome Skipped = new(0, null);
}

/// <summary>
/// Applies and validates the user's custom cleanup rules (Style &amp; Export ▸ Content &amp; cleanup),
/// and converts rule text to and from the form the single-line editor boxes show.
/// </summary>
public static class CleanupRuleEngine
{
    /// <summary>A runaway pattern (e.g. <c>(a+)+$</c>) must not freeze the live preview, which runs the
    /// rules on every keystroke.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>True when the rule has nothing to look for. Spaces alone count as blank (a stray space
    /// would otherwise strip every space in the document), but a line break or tab is a real target.</summary>
    public static bool IsBlank(TextCleanupRule rule) => string.IsNullOrEmpty(rule.Find) || rule.Find.Trim(' ').Length == 0;

    /// <summary>Null when the rule can run, otherwise a short sentence saying what's wrong with its pattern.</summary>
    public static string? Validate(TextCleanupRule rule)
    {
        if (!rule.IsRegex || IsBlank(rule)) return null;
        try
        {
            _ = new Regex(rule.Find, RegexOptions.IgnoreCase | RegexOptions.Multiline, MatchTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return FriendlyPatternError(ex);
        }
    }

    /// <summary>Runs one rule over <paramref name="text"/>. Never throws: an invalid pattern or a
    /// pattern that times out comes back as an <see cref="CleanupRuleOutcome.Error"/> and leaves the
    /// text unchanged.</summary>
    public static (string Text, CleanupRuleOutcome Outcome) Apply(string text, TextCleanupRule rule)
    {
        if (IsBlank(rule)) return (text, CleanupRuleOutcome.Skipped);
        var replacement = rule.Replace ?? "";
        try
        {
            if (rule.IsRegex)
            {
                var rx = new Regex(rule.Find, RegexOptions.IgnoreCase | RegexOptions.Multiline, MatchTimeout);
                var count = 0;
                var result = rx.Replace(text, m => { count++; return m.Result(replacement); });
                return (result, new CleanupRuleOutcome(count, null));
            }

            var sb = new StringBuilder(text.Length);
            int at = 0, hits = 0, idx;
            while ((idx = text.IndexOf(rule.Find, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                sb.Append(text, at, idx - at).Append(replacement);
                at = idx + rule.Find.Length;
                hits++;
            }
            if (hits == 0) return (text, new CleanupRuleOutcome(0, null));
            sb.Append(text, at, text.Length - at);
            return (sb.ToString(), new CleanupRuleOutcome(hits, null));
        }
        catch (RegexMatchTimeoutException)
        {
            return (text, new CleanupRuleOutcome(0, "This pattern took too long on this document and was skipped. Try a simpler one."));
        }
        catch (ArgumentException ex)
        {
            return (text, new CleanupRuleOutcome(0, FriendlyPatternError(ex)));
        }
    }

    // .NET says "Invalid pattern '(ab' at offset 3. Not enough )'s." — the pattern is already on
    // screen, so keep the reason and say where in words.
    private static string FriendlyPatternError(ArgumentException ex)
    {
        if (ex is RegexParseException parse)
        {
            var reason = parse.Message;
            var dot = reason.IndexOf(". ", StringComparison.Ordinal);
            if (reason.StartsWith("Invalid pattern", StringComparison.Ordinal) && dot >= 0)
                reason = reason[(dot + 2)..];
            reason = reason.TrimEnd('.');
            return $"Not a valid pattern: {reason} (at character {parse.Offset}).";
        }
        return "Not a valid pattern: " + ex.Message.TrimEnd('.') + ".";
    }

    // ---- Editor text ----
    // The Find/Replace boxes are single-line, so a rule holding a line break used to show as blank
    // ("\n\n\n") or cut off at the break, and editing it then saved the truncated text. The boxes show
    // line breaks and tabs as \n, \r and \t instead, and typing those sequences means the character.

    /// <summary>Text for a single-line box. <paramref name="regexPattern"/> is for a regex Find, where
    /// the regex engine already reads <c>\n</c> as a line break, so backslashes are left as typed.</summary>
    public static string ToDisplay(string? value, bool regexPattern = false)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\r' when i + 1 < value.Length && value[i + 1] == '\n':
                    sb.Append(@"\n"); i++; break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\\' when !regexPattern && i + 1 < value.Length && value[i + 1] is 'n' or 'r' or 't' or '\\':
                    // A literal backslash that would otherwise read as an escape on the way back in.
                    sb.Append(@"\\"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The rule text a box's contents stand for — the inverse of <see cref="ToDisplay"/>.</summary>
    public static string FromDisplay(string? display, bool regexPattern = false)
    {
        if (string.IsNullOrEmpty(display)) return "";
        if (regexPattern) return display;
        var sb = new StringBuilder(display.Length);
        for (var i = 0; i < display.Length; i++)
        {
            var c = display[i];
            if (c == '\\' && i + 1 < display.Length)
            {
                var next = display[i + 1];
                var mapped = next switch { 'n' => "\n", 'r' => "\r", 't' => "\t", '\\' => "\\", _ => null };
                if (mapped is not null) { sb.Append(mapped); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
