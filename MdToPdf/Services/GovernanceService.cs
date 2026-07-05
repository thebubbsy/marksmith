using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Stores AI-usage governance events reported by managed browser extensions and produces the
// aggregates the admin dashboard shows. Local JSON store for the single-machine MVP; in a real
// deployment the collector is a central service and this class is the reference for its schema.
public sealed class GovernanceService
{
    private const int MaxEvents = 5000;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf", "governance.json");

    private readonly object _lock = new();
    private readonly List<UsageEvent> _events;

    public GovernanceService()
    {
        _events = Load();
    }

    public void Record(UsageEvent e)
    {
        lock (_lock)
        {
            _events.Insert(0, e);
            if (_events.Count > MaxEvents) _events.RemoveRange(MaxEvents, _events.Count - MaxEvents);
            Save();
        }
    }

    public IReadOnlyList<UsageEvent> Recent(int take = 200)
    {
        lock (_lock) return _events.Take(take).ToList();
    }

    // Rolled-up view for the dashboard: totals, per-assistant + per-user breakdowns, and the
    // DLP incidents that actually matter for a compliance conversation.
    public object Summary()
    {
        lock (_lock)
        {
            var since = DateTime.UtcNow.AddDays(-30);
            var window = _events.Where(e => e.Timestamp >= since).ToList();
            return new
            {
                totalEvents = window.Count,
                users = window.Select(e => e.User).Distinct().Count(),
                dlpIncidents = window.Count(e => e.DlpHitCount > 0),
                highRisk = window.Count(e => e.RiskLevel == "High"),
                byAssistant = window.GroupBy(e => e.Assistant)
                    .Select(g => new { assistant = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count),
                byUser = window.GroupBy(e => e.User)
                    .Select(g => new
                    {
                        user = g.Key,
                        events = g.Count(),
                        dlpHits = g.Sum(e => e.DlpHitCount),
                        topAssistant = g.GroupBy(e => e.Assistant).OrderByDescending(x => x.Count()).First().Key,
                    })
                    .OrderByDescending(x => x.dlpHits),
                topFlags = window.SelectMany(e => e.DlpFlags).GroupBy(f => f)
                    .Select(g => new { flag = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count),
            };
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_events));
        }
        catch { /* best-effort */ }
    }

    private static List<UsageEvent> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<UsageEvent>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { }
        return new();
    }
}
