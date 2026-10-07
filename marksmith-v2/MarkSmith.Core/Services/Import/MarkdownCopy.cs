namespace MarkSmith.Services.Import;

/// <summary>Ctrl+S on a document that was converted on open (Word, PDF, HTML, email). Writing
/// Markdown over the original would destroy it, so the edits go to "&lt;name&gt;.md" beside it —
/// or into <c>fallbackDir</c> when the original sits somewhere temporary or read-only, like an
/// attachment Outlook opened from its cache — never over an existing file. The importers'
/// "&lt;name&gt;_media" folder travels with the copy when it lands elsewhere, so relative image
/// links keep working.</summary>
public static class MarkdownCopy
{
    public static string Save(string sourcePath, string markdown, string fallbackDir)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);

        var targetDir = sourceDir;
        if (sourceDir.Length == 0 || Plugins.PluginFileReader.IsTransient(sourceDir) || !CanWrite(sourceDir))
        {
            targetDir = fallbackDir;
            Directory.CreateDirectory(targetDir);
        }

        var target = Path.Combine(targetDir, stem + ".md");
        for (int i = 2; File.Exists(target); i++) target = Path.Combine(targetDir, $"{stem} ({i}).md");

        var media = Path.Combine(sourceDir, stem + "_media");
        if (!string.Equals(Path.GetFullPath(targetDir), sourceDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(media))
            CopyDirectory(media, Path.Combine(targetDir, stem + "_media"));

        File.WriteAllText(target, markdown);
        return target;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".marksmith-write-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Copy(f, dest);
        }
        foreach (var d in Directory.GetDirectories(from))
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
