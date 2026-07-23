using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Persistence for user-created themes (the "Create a theme" color-wheel editor in the apps).
// Stored as plain JSON beside settings.json so they survive updates and can be hand-edited or
// shared. State is intentionally STATIC-shared: several services hold their own ThemeCatalog
// instance (DocxExportService keeps a static one), so a theme saved mid-session must be visible
// to every catalog instantly — not only after an app restart.
public static class CustomThemeStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf", "custom-themes.json");

    private static readonly object Gate = new();
    private static List<ThemeDefinition>? _cache;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static IReadOnlyList<ThemeDefinition> All
    {
        get { lock (Gate) return (_cache ??= Load()).ToList(); }
    }

    public static void AddOrUpdate(ThemeDefinition theme)
    {
        lock (Gate)
        {
            _cache ??= Load();
            _cache.RemoveAll(t => t.Name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase));
            _cache.Add(theme);
            Save();
        }
    }

    public static bool Remove(string name)
    {
        lock (Gate)
        {
            _cache ??= Load();
            var removed = _cache.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private static List<ThemeDefinition> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<ThemeDefinition>>(File.ReadAllText(StorePath), JsonOpts) ?? new();
        }
        catch { /* corrupt store: start fresh rather than crash the app at startup */ }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_cache, JsonOpts));
        }
        catch { /* disk-full/locked: keep the in-memory theme working for this session */ }
    }
}
