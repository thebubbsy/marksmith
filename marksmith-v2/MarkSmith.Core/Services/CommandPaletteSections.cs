using System.Text.Json;

namespace MarkSmith.Services;

/// <summary>One titled run of rows in the command palette. An empty header draws no heading.</summary>
public sealed record PaletteSection<T>(string Header, IReadOnlyList<T> Items);

/// <summary>
/// What the command palette lists. With nothing typed it used to show ~80 commands in one flat
/// run in definition order, so the first screen was always the same nine Export rows; it now opens
/// on the commands you ran last ("Recently used"), then every command under its category heading.
/// Once you type, the list is one ranked run (<see cref="CommandSearch.Rank"/>) with recently used
/// commands winning ties, because those are the ones you are most likely looking for again.
/// </summary>
public static class CommandPaletteSections
{
    public const int RecentLimit = 5;
    public const string RecentHeader = "Recently used";

    public static List<PaletteSection<T>> Build<T>(
        IReadOnlyList<T> items,
        string? query,
        Func<T, string> label,
        Func<T, string> category,
        Func<T, string>? keywords,
        IReadOnlyList<string> recentLabels)
    {
        query = (query ?? "").Trim();
        var recentRank = recentLabels
            .Distinct(StringComparer.Ordinal)
            .Select((l, i) => (l, i))
            .ToDictionary(x => x.l, x => x.i, StringComparer.Ordinal);

        if (query.Length > 0)
        {
            var ranked = items
                .Select((item, index) => (item, index, score: CommandSearch.Score(label(item), category(item), query, keywords?.Invoke(item) ?? "")))
                .Where(x => x.score is not null)
                .OrderByDescending(x => x.score)
                .ThenBy(x => recentRank.TryGetValue(label(x.item), out var r) ? r : int.MaxValue)
                .ThenBy(x => x.index)
                .Select(x => x.item)
                .ToList();
            return ranked.Count == 0 ? new() : new() { new PaletteSection<T>("", ranked) };
        }

        var byLabel = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items) byLabel.TryAdd(label(item), item);
        var recent = recentLabels
            .Distinct(StringComparer.Ordinal)
            .Where(byLabel.ContainsKey)
            .Take(RecentLimit)
            .Select(l => byLabel[l])
            .ToList();
        var recentSet = new HashSet<string>(recent.Select(label), StringComparer.Ordinal);

        var sections = new List<PaletteSection<T>>();
        if (recent.Count > 0) sections.Add(new(RecentHeader, recent));
        // GroupBy keeps first-appearance order, so the categories read in the order commands are
        // declared (Export, File, Edit, …) and the rows inside each keep theirs.
        foreach (var group in items.Where(i => !recentSet.Contains(label(i))).GroupBy(category))
            sections.Add(new(HeaderFor(group.Key), group.ToList()));
        return sections;
    }

    /// <summary>A category's heading: the row tags are singular ("Theme"), a heading names the set.</summary>
    public static string HeaderFor(string category) => category switch
    {
        "Studio" => "Studios",
        "Theme" => "Themes",
        "Recent" => "Recent files",
        _ => category,
    };
}

/// <summary>
/// The labels of the commands last run from the palette, newest first, kept in
/// <c>palette-recent.json</c> in the config folder. Small and forgiving: an unreadable file is an
/// empty list, a failed save is ignored (it's a convenience, never worth an error).
/// </summary>
public sealed class PaletteRecents
{
    public const int Capacity = 12;
    private readonly string _path;
    private readonly List<string> _labels;

    public PaletteRecents(string path)
    {
        _path = path;
        _labels = Load(path);
    }

    public static PaletteRecents ForConfigDir(string configDir) => new(Path.Combine(configDir, "palette-recent.json"));

    public IReadOnlyList<string> Labels => _labels;

    public void Push(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        _labels.Remove(label);
        _labels.Insert(0, label);
        if (_labels.Count > Capacity) _labels.RemoveRange(Capacity, _labels.Count - Capacity);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_labels));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static List<string> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var labels = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new();
            return labels.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal).Take(Capacity).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }
}
