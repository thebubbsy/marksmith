using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Persistent export log (JSON in LocalAppData, newest first, capped) backing the History flyout.
public sealed class HistoryService
{
    private const int MaxEntries = 200;

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf", "history.json");

    private readonly List<HistoryEntry> _entries;

    public HistoryService()
    {
        _entries = Load();
    }

    public IReadOnlyList<HistoryEntry> All => _entries;

    public void Add(HistoryEntry entry)
    {
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_entries));
        }
        catch { /* history is best-effort; never fail an export over it */ }
    }

    private static List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath)) ?? new();
        }
        catch { }
        return new();
    }
}
