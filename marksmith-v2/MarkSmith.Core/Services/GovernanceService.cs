using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Stores AI-usage governance events reported by managed browser extensions and produces the
// aggregates the admin dashboard shows. Local JSON store for the single-machine MVP; in a real
// deployment the collector is a central service and this class is the reference for its schema.
public sealed class GovernanceService
{
    private const int MaxEvents = 5000;

    private static readonly string StorePath = Path.Combine(AppPaths.ConfigDir, "governance.json");

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
                // Structural signal, not content: a message that WAS almost entirely a secret,
                // separate from "high risk" (many hit categories) — this is the accidental-vs-
                // deliberate distinction, sortable/filterable without ever exposing the value.
                likelyDeliberate = window.Count(e => e.DlpHitCount > 0 && e.SecretDensity >= 0.6),
                totalTimeMinutes = window.Sum(e => e.TimeSpentSeconds) / 60,
                byAssistant = window.GroupBy(e => e.Assistant)
                    .Select(g => new { assistant = g.Key, count = g.Count(), minutes = g.Sum(e => e.TimeSpentSeconds) / 60 })
                    .OrderByDescending(x => x.count),
                byUser = window.GroupBy(e => e.User)
                    .Select(g => new
                    {
                        user = g.Key,
                        events = g.Count(),
                        dlpHits = g.Sum(e => e.DlpHitCount),
                        minutes = g.Sum(e => e.TimeSpentSeconds) / 60,
                        topAssistant = g.GroupBy(e => e.Assistant).OrderByDescending(x => x.Count()).First().Key,
                    })
                    .OrderByDescending(x => x.dlpHits),
                topFlags = window.SelectMany(e => e.DlpFlags).GroupBy(f => f)
                    .Select(g => new { flag = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count),
                // Remediation worklist, one row per INCIDENT (not per finding): the masked matches
                // it contained, plus the redacted context and density that tell an investigator
                // whether this looks accidental or deliberate — never the raw value, at any grain.
                recentIncidents = window.Where(e => e.DlpHitCount > 0)
                    .OrderByDescending(e => e.Timestamp)
                    .Take(30)
                    .Select(e => new
                    {
                        timestamp = e.Timestamp, user = e.User, assistant = e.Assistant,
                        secretDensity = Math.Round(e.SecretDensity, 2),
                        intentLabel = e.IntentLabel,
                        redactedContext = e.RedactedContext,
                        rawMessage = e.RawMessage,
                        matches = e.DlpMatches.Select(m => new { m.Category, m.Masked, m.Remediation }),
                    }),
            };
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_events, JsonOpts));
        }
        catch { /* best-effort */ }
    }

    private static List<UsageEvent> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<UsageEvent>>(File.ReadAllText(StorePath), JsonOpts) ?? new();
        }
        catch { }
        return new();
    }
}
