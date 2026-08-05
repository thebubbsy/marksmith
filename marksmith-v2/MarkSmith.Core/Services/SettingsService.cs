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

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        AtomicFile.WriteAllText(SettingsPath, json);
    }
}
