using System.Text.Json;

namespace MdToPdf.Services;

public sealed class RecentFilesService
{
    private const int MaxRecentFiles = 10;

    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdToPdf");

    private static readonly string RecentFilesPath = Path.Combine(ConfigDir, "recent_files.json");

    public List<string> Load()
    {
        try
        {
            if (File.Exists(RecentFilesPath))
            {
                var json = File.ReadAllText(RecentFilesPath);
                var files = JsonSerializer.Deserialize<List<string>>(json);
                if (files is not null) return files.Take(MaxRecentFiles).ToList();
            }
        }
        catch
        {
            // Corrupt recent-files list — start fresh instead of surfacing an error for a non-critical feature.
        }
        return new List<string>();
    }

    public List<string> AddToRecent(string filePath)
    {
        var files = Load();
        var full = Path.GetFullPath(filePath);
        files.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
        files.Insert(0, full);
        files = files.Take(MaxRecentFiles).ToList();

        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(RecentFilesPath, JsonSerializer.Serialize(files));
        return files;
    }
}
