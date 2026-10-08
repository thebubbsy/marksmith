using System.Security.Cryptography;
using System.Text;

namespace MarkSmith.Models;

/// <summary>
/// One entry in a file dialog's file-type list. Every dialog in the app builds its list from
/// these, so the labels read the same everywhere: a sentence-case name ("PNG image"). Windows adds
/// the patterns itself ("PNG image (*.png)") when the user has file extensions showing, so the
/// label never repeats them.
/// </summary>
public sealed record FileType
{
    public string Name { get; }

    /// <summary>Extensions with their dot, lower-case (".md"). Empty for <see cref="AllFiles"/>.</summary>
    public IReadOnlyList<string> Extensions { get; }

    private FileType(string name, IReadOnlyList<string> extensions)
    {
        Name = name;
        Extensions = extensions;
    }

    public static FileType Of(string name, params string[] extensions) => Of(name, (IEnumerable<string>)extensions);

    public static FileType Of(string name, IEnumerable<string> extensions)
    {
        var exts = extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => "." + e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exts.Count == 0) throw new ArgumentException("A file type needs at least one extension.", nameof(extensions));
        return new FileType(name, exts);
    }

    public static FileType AllFiles { get; } = new("All files", Array.Empty<string>());

    public bool IsAllFiles => Extensions.Count == 0;

    /// <summary>The dialog filter pattern: "*.png;*.jpg".</summary>
    public string Spec => IsAllFiles ? "*.*" : string.Join(";", Extensions.Select(e => "*" + e));

    /// <summary>
    /// The pattern a save dialog gets: <see cref="Spec"/> plus "*.*" ("*.svg;*.*"). Windows still
    /// takes the default extension from the first pattern, but the folder isn't filtered. A save
    /// dialog showing a filtered folder froze at "Working on it…" on a real PC (Windhawk injected,
    /// Windows Search disabled; WinForms' SaveFileDialog froze the same way), and the unfiltered
    /// view doesn't.
    /// </summary>
    public string SaveSpec => IsAllFiles ? "*.*" : Spec + ";*.*";

    /// <summary>What the dialog shows in its file-type list (Windows appends the patterns).</summary>
    public string Label => Name;

    public bool Matches(string? path)
    {
        if (IsAllFiles) return true;
        var ext = Path.GetExtension(path ?? "");
        return Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The decisions the native file dialogs make that don't need Windows: which extension a save
/// starts with, the extension a typed name ends up with, and the per-purpose identity that lets
/// each kind of dialog remember its own last folder.
/// </summary>
public static class FileDialogRules
{
    /// <summary>The extension (no dot) a save dialog appends to a bare name: the first real type's.</summary>
    public static string? DefaultExtension(IReadOnlyList<FileType> types)
        => types.FirstOrDefault(t => !t.IsAllFiles)?.Extensions[0].TrimStart('.');

    /// <summary>
    /// A saved file always ends up with an extension: a name typed with none gets the default
    /// one. A name that already has an extension (".csv" in a dialog that defaults to ".xlsx") is
    /// left exactly as typed; the dialog has already asked about overwriting that name.
    /// </summary>
    public static string EnsureExtension(string path, IReadOnlyList<FileType> types)
    {
        if (string.IsNullOrEmpty(path) || Path.HasExtension(path)) return path;
        var ext = DefaultExtension(types);
        return ext is null ? path : path.TrimEnd('.') + "." + ext;
    }

    /// <summary>
    /// A stable GUID per dialog purpose ("images", "exports" …). Windows keeps the last folder
    /// and size per GUID, so picking a logo doesn't land in the folder a table was exported to.
    /// </summary>
    public static Guid ClientGuid(string purpose)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("MarkSmith.FileDialog/" + (purpose ?? "").Trim().ToLowerInvariant()));
        return new Guid(bytes);
    }

    /// <summary>Strips the characters Windows won't allow in a file name, for suggested names.</summary>
    public static string SafeFileName(string? name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (cleaned.Length > 120) cleaned = cleaned[..120].TrimEnd();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
