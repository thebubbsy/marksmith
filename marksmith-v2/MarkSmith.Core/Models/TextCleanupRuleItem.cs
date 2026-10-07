using CommunityToolkit.Mvvm.ComponentModel;
using MarkSmith.Services;

namespace MarkSmith.Models;

/// <summary>Editable row for one custom AI-cleanup rule in the Settings pane.</summary>
public sealed partial class TextCleanupRuleItem : ObservableObject
{
    private readonly Action _save;

    public TextCleanupRuleItem(Action save, string? find = null, string? replace = null, bool isRegex = false)
    {
        _save = save;
        _find = find ?? "";
        _replace = replace ?? "";
        _isRegex = isRegex;
        _findText = CleanupRuleEngine.ToDisplay(_find, _isRegex);
        _replaceText = CleanupRuleEngine.ToDisplay(_replace);
        _patternError = CleanupRuleEngine.Validate(ToRule());
    }

    [ObservableProperty]
    private string _find;

    partial void OnFindChanged(string value)
    {
        if (!_fromBox) SetProperty(ref _findText, CleanupRuleEngine.ToDisplay(value, IsRegex), nameof(FindText));
        Revalidate();
        _save();
    }

    [ObservableProperty]
    private string _replace;

    partial void OnReplaceChanged(string value)
    {
        if (!_fromBox) SetProperty(ref _replaceText, CleanupRuleEngine.ToDisplay(value), nameof(ReplaceText));
        _save();
    }

    [ObservableProperty]
    private bool _isRegex;

    partial void OnIsRegexChanged(bool value)
    {
        // Keep what the box shows and reinterpret it: "\n" typed as plain text means a line break,
        // and as a pattern the regex engine reads it the same way.
        _fromBox = true;
        try { Find = CleanupRuleEngine.FromDisplay(FindText, value); }
        finally { _fromBox = false; }
        Revalidate();
        _save();
    }

    // ---- What the single-line boxes show (line breaks and tabs as \n and \t) ----
    // Typing writes through to Find/Replace without echoing back, so the caret never jumps and a
    // half-typed escape such as "\" stays exactly as typed.
    private bool _fromBox;

    private string _findText;
    public string FindText
    {
        get => _findText;
        set
        {
            if (!SetProperty(ref _findText, value ?? "")) return;
            _fromBox = true;
            try { Find = CleanupRuleEngine.FromDisplay(_findText, IsRegex); }
            finally { _fromBox = false; }
        }
    }

    private string _replaceText;
    public string ReplaceText
    {
        get => _replaceText;
        set
        {
            if (!SetProperty(ref _replaceText, value ?? "")) return;
            _fromBox = true;
            try { Replace = CleanupRuleEngine.FromDisplay(_replaceText); }
            finally { _fromBox = false; }
        }
    }

    // ---- Feedback under the row ----

    /// <summary>Why the pattern can't run, checked as you type; null when it's fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatternError), nameof(HasStatus))]
    private string? _patternError;

    public bool HasPatternError => PatternError is not null;

    /// <summary>How the rule did on the document in the preview ("3 matches in this document"), or
    /// empty before the preview has run it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    public bool HasStatus => !HasPatternError && Status.Length > 0;

    private void Revalidate()
    {
        PatternError = CleanupRuleEngine.Validate(ToRule());
        if (CleanupRuleEngine.IsBlank(ToRule())) Status = "";
    }

    /// <summary>Shows the result of the latest preview pass. A run-time failure (a pattern that timed
    /// out) is reported like a bad pattern.</summary>
    public void ShowOutcome(CleanupRuleOutcome? outcome)
    {
        if (outcome is not { } o || CleanupRuleEngine.IsBlank(ToRule())) { Status = ""; return; }
        if (o.Error is not null) { PatternError = o.Error; Status = ""; return; }
        PatternError = CleanupRuleEngine.Validate(ToRule());
        Status = o.Matches switch
        {
            0 => "No matches in this document",
            1 => "1 match in this document",
            var n => $"{n} matches in this document",
        };
    }

    public TextCleanupRule ToRule() => new() { Find = Find, Replace = Replace, IsRegex = IsRegex };
}
