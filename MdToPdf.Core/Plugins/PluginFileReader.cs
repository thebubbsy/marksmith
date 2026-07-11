namespace MdToPdf.Plugins;

// The single entry point both shells use to turn an input file into Markdown text. Markdown/text
// files read straight through; anything an installed importer plugin claims (e.g. .rst/.org/.docx
// via the Pandoc plugin) is converted; everything else falls back to a raw read, preserving the
// app's original behavior exactly when no importer is involved.
public static class PluginFileReader
{
    public static async Task<string> ReadAsMarkdownAsync(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext is "md" or "markdown" or "txt" or "")
            return await File.ReadAllTextAsync(path);

        var importer = AppServices.Plugins.FindImporter(ext);
        if (importer != null)
        {
            // Conversion is CPU/subprocess-bound; don't block the UI thread that preview refresh
            // and drag-drop handlers run on.
            var markdown = await Task.Run(() => importer.ImportToMarkdown(path));
            if (markdown != null) return markdown;
        }

        return await File.ReadAllTextAsync(path);
    }
}
