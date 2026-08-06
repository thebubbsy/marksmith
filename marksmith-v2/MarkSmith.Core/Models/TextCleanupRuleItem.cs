using CommunityToolkit.Mvvm.ComponentModel;

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
    }

    [ObservableProperty]
    private string _find;

    partial void OnFindChanged(string value) => _save();

    [ObservableProperty]
    private string _replace;

    partial void OnReplaceChanged(string value) => _save();

    [ObservableProperty]
    private bool _isRegex;

    partial void OnIsRegexChanged(bool value) => _save();

    public TextCleanupRule ToRule() => new() { Find = Find, Replace = Replace, IsRegex = IsRegex };
}
