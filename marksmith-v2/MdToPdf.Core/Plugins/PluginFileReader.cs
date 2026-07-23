namespace MdToPdf.Plugins;

// The single entry point both shells use to turn an input file into Markdown text. Markdown/text
// files read straight through; anything an installed importer plugin claims (e.g. .rst/.org/.docx
// via the Pandoc plugin) is converted; everything else falls back to a raw read, preserving the
// app's original behavior exactly when no importer is involved.
public static class PluginFileReader
{
    // Caps concurrent importer subprocesses. Without it, a front-end handling several imports at
    // once (e.g. the local API server, or a multi-file drop) spawns one pandoc process per file
    // simultaneously — a resource-exhaustion vector. Conversions still all complete; only a bounded
    // number run at any instant.
    private static readonly SemaphoreSlim ImportGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    public static async Task<string> ReadAsMarkdownAsync(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is "md" or "markdown" or "txt" or "")
            return await File.ReadAllTextAsync(path);

        var importer = AppServices.Plugins.FindImporter(ext);
        if (importer != null)
        {
            await ImportGate.WaitAsync();
            try
            {
                // Conversion is CPU/subprocess-bound; don't block the UI thread that preview refresh
                // and drag-drop handlers run on.
                var markdown = await Task.Run(() => importer.ImportToMarkdown(path));
                if (markdown != null) return markdown;
            }
            finally { ImportGate.Release(); }
        }

        return await File.ReadAllTextAsync(path);
    }
}
