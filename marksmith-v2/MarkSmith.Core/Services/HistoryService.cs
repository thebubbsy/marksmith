using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Persistent export log (JSON in LocalAppData, newest first, capped) backing the History flyout.
public sealed class HistoryService
{
    private const int MaxEntries = 200;

    private static readonly string DefaultHistoryPath = Path.Combine(AppPaths.ConfigDir, "history.json");

    private readonly string _historyPath;
    private readonly List<HistoryEntry> _entries;

    public HistoryService() : this(null)
    {
    }

    public HistoryService(string? customPath)
    {
        _historyPath = string.IsNullOrWhiteSpace(customPath) ? DefaultHistoryPath : customPath;
        _entries = Load(_historyPath);
    }

    public IReadOnlyList<HistoryEntry> All => _entries;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Add(HistoryEntry entry)
    {
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public bool Remove(HistoryEntry? entry)
    {
        if (entry is null) return false;
        var removed = _entries.Remove(entry);
        if (!removed)
        {
            var idx = _entries.FindIndex(e =>
                e.Timestamp == entry.Timestamp &&
                string.Equals(e.OutputPath, entry.OutputPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Kind, entry.Kind, StringComparison.Ordinal));
            if (idx >= 0)
            {
                _entries.RemoveAt(idx);
                removed = true;
            }
        }
        if (removed) Save();
        return removed;
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(_historyPath, JsonSerializer.Serialize(_entries, JsonOpts));
        }
        catch { /* history is best-effort; never fail an export over it */ }
    }

    private static List<HistoryEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // Shared read: another MarkSmith window (or a sync client) may be writing history.json.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<List<HistoryEntry>>(stream, JsonOpts) ?? new();
            }
        }
        catch { }
        return new();
    }
}
