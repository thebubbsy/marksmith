using System;
using System.Collections.Generic;

namespace MarkSmith.Services;

/// <summary>
/// Up/Down recall for a one-line box (the find bar's Replace box): the newest entry comes first,
/// Up steps to older entries, Down steps back and finally restores what was typed before the first
/// Up. Entries are kept exactly as typed, spaces included, because a replacement of " " or "a "
/// is meaningful; only the empty string isn't recorded.
/// </summary>
public sealed class RecallHistory
{
    private readonly List<string> _items = new();
    private readonly int _capacity;
    private int _index = -1;
    private string _draft = "";

    public RecallHistory(int capacity = 12) => _capacity = Math.Max(1, capacity);

    public IReadOnlyList<string> Items => _items;

    /// <summary>True while Up has stepped into the history (Down has somewhere to go back to).</summary>
    public bool IsRecalling => _index >= 0;

    /// <summary>Puts <paramref name="entry"/> first (moving an existing copy) and ends any recall.</summary>
    public void Record(string? entry)
    {
        _index = -1;
        _draft = "";
        if (string.IsNullOrEmpty(entry)) return;
        _items.Remove(entry);
        _items.Insert(0, entry);
        if (_items.Count > _capacity) _items.RemoveRange(_capacity, _items.Count - _capacity);
    }

    /// <summary>
    /// The next older entry, or null when there is none (the key should then do its usual job).
    /// <paramref name="current"/> is the box's text, kept as the draft on the first step.
    /// </summary>
    public string? Older(string current)
    {
        if (_items.Count == 0) return null;
        if (_index == -1) _draft = current ?? "";
        if (_index + 1 >= _items.Count) return _items[_index]; // already at the oldest: stay put
        return _items[++_index];
    }

    /// <summary>
    /// How an entry reads in a menu: quoted when it starts or ends with a space (so "a " and "a"
    /// look different and a lone " " isn't a blank row), one line, and cut at 60 characters.
    /// </summary>
    public static string Label(string entry)
    {
        var line = (entry ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        if (line.Length > 60) line = line[..59] + "…";
        return line.Length == 0 || char.IsWhiteSpace(line[0]) || char.IsWhiteSpace(line[^1])
            ? "“" + line + "”"
            : line;
    }

    /// <summary>The next newer entry, then the draft; null when not recalling.</summary>
    public string? Newer()
    {
        if (_index < 0) return null;
        _index--;
        return _index >= 0 ? _items[_index] : _draft;
    }
}
