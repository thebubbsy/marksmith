using System.Text.Json;
using MarkSmith.Models;

namespace MarkSmith.Services;

public sealed class SettingsService
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith");

    private static readonly string SettingsPath = Path.Combine(ConfigDir, "settings.json");

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        Current = Load();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (settings is not null)
                {
                    // One-time migration: the poster-grid oversized-diagram strategy was removed
                    // (old mode 4 — it changed the page size, overriding the page-width/A4/
                    // continuous-page settings). See MigrateOversizedDiagramModes for the gate.
                    MigrateOversizedDiagramModes(json, settings);

                    // One-time seed: a saved-but-empty rule list (from before the examples existed)
                    // gets the three example cleanup rules ONCE; deleting every rule afterwards stays
                    // deleted because the seeded flag is persisted.
                    if (!settings.CustomNormalizationRulesSeeded)
                    {
                        if (settings.CustomNormalizationRules is null || settings.CustomNormalizationRules.Count == 0)
                            settings.CustomNormalizationRules = AppSettings.DefaultCustomNormalizationRules();
                        settings.CustomNormalizationRulesSeeded = true;
                    }

                    // One-time migration: TargetFormat is now the single "default output format".
                    // Fold the old DefaultExportFormat choice into it when the user never set
                    // TargetFormat explicitly (it still holds its default "pdf").
                    if (settings.TargetFormat == "pdf" && json.Contains("\"DefaultExportFormat\""))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("DefaultExportFormat", out var def) &&
                            def.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            settings.TargetFormat = def.GetString()!;
                        }
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash on startup.
        }
        return new AppSettings();
    }

    /// <summary>One-time oversized-diagram migration from the removed poster-grid numbering.
    /// Old mode 4 (Grid — it changed the page size) becomes 1 (Keep Original Size); old modes
    /// 5-8 shift down one slot. Runs ONLY for settings JSON that predates the SettingsVersion
    /// key. Discriminator = presence of the key as a root PROPERTY in the raw JSON: a missing
    /// key deserializes to the constructor default, so gating on the deserialized value could
    /// never tell an old file from a fresh default; and a raw-substring check could false-positive
    /// on a string VALUE containing "SettingsVersion" (e.g. a custom cleanup-rule name), so the
    /// key is tested via JsonDocument. Files that carry the key were written under the current
    /// schema — their Aggressive Shrink (4) / Compress modes (5/6/7) are never rewritten.</summary>
    internal static void MigrateOversizedDiagramModes(string rawJson, AppSettings settings)
    {
        bool hasSettingsVersion;
        using (var doc = JsonDocument.Parse(rawJson))
        {
            hasSettingsVersion = doc.RootElement.TryGetProperty("SettingsVersion", out _);
        }
        if (!hasSettingsVersion)
        {
            if (settings.OversizedDiagramMode > 4) settings.OversizedDiagramMode--;
            else if (settings.OversizedDiagramMode == 4) settings.OversizedDiagramMode = 1;
            settings.SettingsVersion = 2;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        AtomicFile.WriteAllText(SettingsPath, json);
    }
}
