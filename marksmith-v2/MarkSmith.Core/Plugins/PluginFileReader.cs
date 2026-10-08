namespace MarkSmith.Plugins;

/// <summary>An input file as the editor sees it. <see cref="Kind"/> is null for Markdown/text (the
/// file IS the document); otherwise it names what the Markdown was converted from ("Word", "PDF",
/// "HTML", "Email", or an importer plugin's name) — such a file must never be overwritten with
/// Markdown. <see cref="Summary"/> is an optional status-bar line about the import.</summary>
public sealed record ImportedDocument(string Markdown, string? Kind, string? Summary)
{
    public bool IsConverted => Kind is not null;
}

// The single entry point every shell uses to turn an input file into Markdown text. Markdown/text
// files read straight through; Word, PDF, HTML and email files go through the native importers;
// anything an installed importer plugin claims (e.g. .rst/.org via the Pandoc plugin) is
// converted; everything else falls back to a raw read.
public static class PluginFileReader
{
    // Caps concurrent importer subprocesses. Without it, a front-end handling several imports at
    // once (e.g. the local API server, or a multi-file drop) spawns one pandoc process per file
    // simultaneously — a resource-exhaustion vector. Conversions still all complete; only a bounded
    // number run at any instant.
    private static readonly SemaphoreSlim ImportGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>Extensions the app opens natively (without any plugin), lower-case, no dot.</summary>
    public static IReadOnlyList<string> NativeExtensions { get; } =
        new[] { "md", "markdown", "txt", "docx", "pdf", "html", "htm", "eml", "msg" };

    /// <summary>True when <paramref name="path"/> is something the editor can open: Markdown, a
    /// natively imported format, or one an installed importer plugin claims.</summary>
    public static bool CanOpen(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return NativeExtensions.Contains(ext) || AppServices.Plugins.AllImporterExtensions.Contains(ext);
    }

    /// <summary>True when the file is plain Markdown/text that Ctrl+S may write back to.</summary>
    public static bool IsMarkdownFile(string path) =>
        Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is "md" or "markdown" or "txt" or "";

    public static async Task<string> ReadAsMarkdownAsync(string path) => (await ImportAsync(path)).Markdown;

    // Converting a Word file or an email is far from free (and an email import writes its images),
    // yet the preview, the copy commands and the exports each ask for the file's Markdown. One
    // result per (path, size, write time) means every caller after the first gets it for nothing.
    // The cache holds the conversion *task*, so callers that arrive while it is still running (the
    // preview and the editor both ask the moment a file opens) share it instead of racing to
    // write the same image files.
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime Stamp, long Length, Task<ImportedDocument> Doc)> Cache =
        new(Services.PathEquality.Comparer);

    /// <summary>Forget every cached conversion (an import option changed).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock) Cache.Clear();
    }

    public static async Task<ImportedDocument> ImportAsync(string path)
    {
        if (IsMarkdownFile(path))
            return new ImportedDocument(await File.ReadAllTextAsync(path), null, null);

        string full;
        try { full = Path.GetFullPath(path); } catch { full = path; }
        var info = new FileInfo(full);
        if (!info.Exists) return await ConvertAsync(full);

        Task<ImportedDocument> task;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(full, out var hit) && hit.Stamp == info.LastWriteTimeUtc && hit.Length == info.Length
                && !hit.Doc.IsFaulted && !hit.Doc.IsCanceled)
            {
                task = hit.Doc;
            }
            else
            {
                if (Cache.Count > 16) Cache.Clear();
                task = ConvertAsync(full);
                Cache[full] = (info.LastWriteTimeUtc, info.Length, task);
            }
        }
        return await task;
    }

    private static async Task<ImportedDocument> ConvertAsync(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        // DOCX: prefer the native Smart Dual-Mode engine — Tier 1 returns Marksmith's embedded
        // source losslessly, Tier 2 generalizes any other Word file (images, headings, tables,
        // shapes). The engine already cascades to a Pandoc importer internally; we only fall through
        // to the plugin path below if it produces nothing at all.
        if (ext == "docx")
        {
            var result = await new Services.ReverseImportService().ImportFromDocxAsync(path);
            if (result.Tier != Services.ImportTier.None && !string.IsNullOrWhiteSpace(result.Markdown))
                return new ImportedDocument(result.Markdown, "Word", null);
        }

        // PDF: MarkSmith's own embedded source when it has one; otherwise the text with its
        // structure, pictures, and OCR for scanned pages (ReverseImportService.ImportFromPdf).
        if (ext == "pdf")
        {
            var result = await new Services.ReverseImportService().ImportFromPdfAsync(path);
            if (result.Tier != Services.ImportTier.None && !string.IsNullOrWhiteSpace(result.Markdown))
                return new ImportedDocument(result.Markdown, "PDF",
                    result.Warning is null ? null : $"Imported {Path.GetFileName(path)} · {result.Warning}");
        }

        if (ext is "html" or "htm")
        {
            var html = await File.ReadAllTextAsync(path);
            var mediaDir = MediaDirFor(path);
            var docDir = Path.GetDirectoryName(path) ?? ".";
            var saved = new int[1];
            var options = new Services.Import.HtmlToMarkdownOptions
            {
                // Embedded data: images (every self-contained HTML export has them) become files in
                // "<name>_media", so the editor holds a short link rather than a wall of base64.
                ResolveImage = src => SaveDataUri(src, mediaDir, docDir, saved) ?? src,
            };
            var md = await Task.Run(() => Services.Import.HtmlToMarkdown.Convert(html, options));
            return new ImportedDocument(md, "HTML", null);
        }

        if (ext is "eml" or "msg")
        {
            var mode = AppServices.Settings.Current.EmailImportHistory?.ToLowerInvariant() switch
            {
                "remove" => Services.Email.QuotedHistoryMode.Remove,
                "keep" => Services.Email.QuotedHistoryMode.Keep,
                _ => Services.Email.QuotedHistoryMode.Collapse,
            };
            var result = await Task.Run(() => ext == "msg"
                ? Services.Email.MsgImporter.Import(path, mode, MediaDirFor(path))
                : Services.Email.EmailImporter.Import(path, mode, MediaDirFor(path)));
            return new ImportedDocument(result.Markdown, "Email", result.Summary);
        }

        var importer = AppServices.Plugins.FindImporter(ext);
        if (importer != null)
        {
            await ImportGate.WaitAsync();
            try
            {
                // Conversion is CPU/subprocess-bound; don't block the UI thread that preview refresh
                // and drag-drop handlers run on.
                var markdown = await Task.Run(() => importer.ImportToMarkdown(path));
                if (markdown != null) return new ImportedDocument(markdown, importer.Name, null);
            }
            finally { ImportGate.Release(); }
        }

        return new ImportedDocument(await File.ReadAllTextAsync(path), null, null);
    }

    // "<name>_media" beside the file, like the Word importer — unless it sits somewhere transient
    // or read-only (an attachment Outlook opened from its temp folder), where the images would
    // vanish or can't be written: then the app's own imports folder.
    /// <summary>Where an imported document's pictures go: "&lt;name&gt;_media" beside it, or a
    /// private folder when the document's own folder is read-only or temporary.</summary>
    public static string MediaDirFor(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var beside = Path.Combine(dir, stem + "_media");
        if (!IsTransient(dir) && CanWrite(dir)) return beside;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath).ToLowerInvariant())))[..10];
        return Path.Combine(Services.AppPaths.ConfigDir, "imports", stem + "-" + hash);
    }

    /// <summary>How the imported document links to <paramref name="mediaDir"/>: relative when it
    /// sits beside the document, its full path when it's the private fallback folder.</summary>
    public static string MediaLinkFor(string sourcePath, string mediaDir)
    {
        var rel = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".", Path.GetFullPath(mediaDir));
        return (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? Path.GetFullPath(mediaDir) : rel).Replace('\\', '/');
    }

    private static readonly System.Text.RegularExpressions.Regex DataImage = new(
        "^data:image/(png|jpe?g|gif|webp|bmp|svg[+]xml);base64,",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Writes a data: image to the media folder under a content-derived name (the same picture is
    // one file, and re-opening rewrites nothing) and returns the link to use, or null to keep src.
    private static string? SaveDataUri(string src, string mediaDir, string docDir, int[] saved)
    {
        var m = DataImage.Match(src);
        if (!m.Success) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(src[m.Length..].Trim()); } catch { return null; }
        var ext = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => ".jpg",
            var t when t.StartsWith("svg") => ".svg",
            var t => "." + t,
        };
        var name = "image-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..10].ToLowerInvariant() + ext;
        try
        {
            Directory.CreateDirectory(mediaDir);
            var file = Path.Combine(mediaDir, name);
            if (!File.Exists(file)) File.WriteAllBytes(file, bytes);
            saved[0]++;
            var rel = Path.GetRelativePath(docDir, file);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? file : rel.Replace('\\', '/');
        }
        catch { return null; }
    }

    public static bool IsTransient(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || full.Contains(@"\Content.Outlook\", StringComparison.OrdinalIgnoreCase)
                || full.Contains(@"\INetCache\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
}
