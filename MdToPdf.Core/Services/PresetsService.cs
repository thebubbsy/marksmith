using System.Text.Json;
using MdToPdf.Models;

namespace MdToPdf.Services;

// Persists the user's saved export presets to presets.json alongside the other app data.
public sealed class PresetsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf");
    private static readonly string Path_ = Path.Combine(Dir, "presets.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public List<ExportPreset> Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<List<ExportPreset>>(File.ReadAllText(Path_)) ?? new();
        }
        catch { /* corrupt list — start fresh rather than error on a non-critical feature */ }
        return new();
    }

    public void Save(IEnumerable<ExportPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(presets, Opts));
        }
        catch { /* best-effort */ }
    }
}
