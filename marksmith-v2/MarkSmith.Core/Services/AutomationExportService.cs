using MarkSmith.Models;

namespace MarkSmith.Services;

/// <summary>One unattended export: a document, the format and where it goes.</summary>
public sealed class AutomationExportJob
{
    /// <summary>The document, already cleaned up (repairs and, when on, AI-quirk normalisation).</summary>
    public string Markdown { get; init; } = "";
    /// <summary>Any name <see cref="OutputFormats.Normalize"/> accepts.</summary>
    public string Format { get; init; } = OutputFormats.Pdf;
    public string OutputPath { get; init; } = "";
    public AppSettings Settings { get; init; } = new();
    /// <summary>The preview engine: required for PDF, used for diagrams when present.</summary>
    public IWebRenderHost? Host { get; init; }
    public LlmClassification? Classification { get; init; }
    /// <summary>Source file name or ingest label: the email's {source} token and fallback subject.</summary>
    public string? SourceLabel { get; init; }
    public string? EmailSubject { get; init; }
    /// <summary>Folder relative image links resolve against (a watched or batched file's folder).</summary>
    public string? BaseDirectory { get; init; }
    /// <summary>Word output may go to the "running document" instead of <see cref="OutputPath"/>
    /// when that setting is on. Batch convert turns this off: forty files appended to one
    /// notebook is never what "convert this folder" means.</summary>
    public bool AllowRunningDoc { get; init; } = true;
}

/// <summary>What a batch run did, file by file.</summary>
public sealed class BatchConvertResult
{
    public string Format { get; init; } = OutputFormats.Pdf;
    public string OutputFolder { get; init; } = "";
    public List<string> Produced { get; } = new();
    /// <summary>"name.md: why" for each file that didn't convert.</summary>
    public List<string> Failures { get; } = new();
    public int Total { get; set; }
    public bool Cancelled { get; set; }

    public int Done => Produced.Count;
    public int Failed => Failures.Count;

    /// <summary>The status line for the finished run.</summary>
    public string Summary()
    {
        var label = OutputFormats.Label(Format);
        if (Total == 0) return "There were no documents to convert.";
        var head = Cancelled
            ? $"Batch stopped: converted {Done} of {Total} to {label}"
            : Failed == 0
                ? $"Batch done: {Done} {(Done == 1 ? "file" : "files")} converted to {label}"
                : $"Batch done: {Done} of {Total} converted to {label}, {Failed} failed";
        var detail = Failed == 0 ? "" : " · " + Failures[0] + (Failed > 1 ? $" (+{Failed - 1} more)" : "");
        return head + detail + (Done > 0 ? $" · in {OutputFolder}" : "");
    }

    /// <summary>The JSON body /api/batch has always returned, plus the reasons.</summary>
    public object ToApi() => new { done = Done, failed = Failed, outputFolder = OutputFolder, files = Produced, failures = Failures, message = Total == 0 ? "No documents found." : Summary() };
}

/// <summary>
/// The one way an unattended path turns Markdown into a file: the clipboard / extension / API
/// auto-export, the watch folder, batch convert (a picked folder, dropped files, /api/batch) and
/// /api/convert. They used to carry five copies of this switch, each with a different subset of
/// formats and diagram handling (the watch folder's Word files had no diagrams at all).
/// Every caller holds <see cref="Lock"/> around a call: the PDF and diagram paths share the one
/// preview engine.
/// </summary>
public sealed class AutomationExportService
{
    private readonly PdfExportService _pdf = new();
    private readonly DocxExportService _docx = new();
    private readonly PptxExportService _pptx = new();
    private readonly EpubExportService _epub = new();
    private readonly MermaidHarvestService _mermaid = new();

    /// <summary>Serialises every export that may use the preview engine.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    /// <summary>Writes the job and returns the file actually written (the running document for an
    /// appended Word export). Throws with a reason <see cref="ExportFailureMessage"/> can word.</summary>
    public async Task<string> ExportAsync(AutomationExportJob job, CancellationToken ct = default)
    {
        var fmt = OutputFormats.Normalize(job.Format)
            ?? throw new ArgumentException($"MarkSmith can't export to \"{job.Format}\".", nameof(job));
        var settings = job.Settings;
        var md = job.Markdown ?? "";
        var theme = AppServices.Themes.GetOrDefault(settings.Theme);
        var hasMermaid = md.Contains("```mermaid", StringComparison.Ordinal);
        bool isEmail = fmt is OutputFormats.Eml or OutputFormats.Msg;
        // Only start the preview engine when this export draws with it (an email's PDF copy does).
        var host = job.Host is not null && (fmt == OutputFormats.Pdf || hasMermaid || (isEmail && settings.EmailAttachPdf)) && await job.Host.EnsureReadyAsync()
            ? job.Host : null;
        var appendDocx = fmt == OutputFormats.Docx && job.AllowRunningDoc
            && settings.AppendToRunningDoc && !string.IsNullOrWhiteSpace(settings.RunningDocPath);
        var outPath = appendDocx ? settings.RunningDocPath : job.OutputPath;

        if (!appendDocx) ExportFailureMessage.ThrowIfLocked(outPath);
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        ct.ThrowIfCancellationRequested();

        switch (fmt)
        {
            case OutputFormats.Pdf:
            {
                if (host is null) throw new InvalidOperationException("The preview engine couldn't start, and a PDF needs it.");
                var html = AppServices.MarkdownHtml.Render(md, settings, theme, job.Classification);
                await _pdf.ExportAsync(host, html, outPath, settings, md);
                break;
            }
            case OutputFormats.Docx:
            {
                IReadOnlyList<byte[]?>? pngs = null;
                IReadOnlyList<Mermaid.HarvestedDiagram?>? geometry = null;
                IReadOnlyList<Mermaid.GenericDiagram?>? generic = null;
                if (hasMermaid && host is not null)
                {
                    if (settings.MermaidDocxMode == 1)
                    {
                        var mode = settings.OversizedDiagramMode;
                        if (mode == 1 || mode is >= 3 and <= 7)
                        {
                            var harvested = await _mermaid.HarvestMermaidGeometryAsync(host, md, settings, theme);
                            geometry = harvested.Any(g => g is { IsEmpty: false }) ? harvested : null;
                        }
                        generic = await _mermaid.HarvestGenericGeometryAsync(host, md, settings, theme);
                    }
                    pngs = await _mermaid.RenderMermaidPngsAsync(host, md, settings, theme);
                }
                if (appendDocx)
                    await _docx.ExportAppendAsync(md, outPath, settings, pngs, geometry, generic);
                else
                    await _docx.ExportAsync(md, outPath, settings, pngs,
                        settings.NormalizeLlm ? job.Classification?.AppliedFixes : null, geometry, generic);
                break;
            }
            case OutputFormats.Pptx:
                await _pptx.ExportAsync(md, outPath, settings);
                break;
            case OutputFormats.Epub:
            {
                IReadOnlyList<byte[]?>? pngs = hasMermaid && host is not null
                    ? await _mermaid.RenderMermaidPngsAsync(host, md, settings, theme)
                    : null;
                await _epub.ExportAsync(md, outPath, settings, null, pngs);
                break;
            }
            default: // eml / msg — free on every plan
            {
                List<byte[]?>? pngs = null;
                if (hasMermaid && host is not null)
                {
                    // Drawn in the email's white palette so a dark theme doesn't put a dark slab
                    // in a light message (same as Email draft).
                    var prepared = Email.EmailHtmlRenderer.Prepare(md, settings, theme);
                    pngs = await _mermaid.RenderMermaidPngsAsync(host, prepared, settings, Email.EmailPalette.From(theme).DiagramTheme());
                }
                var attachments = await BuildEmailAttachmentsAsync(md, job, host, theme, outPath, ct);
                var doc = Email.EmailComposer.Compose(new Email.EmailComposeRequest
                {
                    Markdown = md,
                    SourceLabel = job.SourceLabel,
                    Subject = job.EmailSubject,
                    BaseDirectory = job.BaseDirectory,
                    MermaidPngs = pngs,
                    Attachments = attachments,
                }, settings, theme);
                if (fmt == OutputFormats.Msg) Email.MsgWriter.Write(doc, outPath);
                else Email.EmlWriter.Write(doc, outPath);
                break;
            }
        }
        return outPath;
    }

    /// <summary>
    /// The PDF and Word copies Settings › Email asks for, the same ones Email draft attaches.
    /// Automation emails used to carry none. A copy that can't be made is left off and the email
    /// still goes: the PDF needs the preview engine, and the Word copy is Pro (the email is free).
    /// A trial's Word exports are the user's to spend: unattended emails don't attach one on a
    /// trial, or three dropped files would use the whole trial up unseen.
    /// </summary>
    private async Task<List<Email.EmailAttachment>> BuildEmailAttachmentsAsync(
        string md, AutomationExportJob job, IWebRenderHost? host, Models.ThemeDefinition theme, string outPath, CancellationToken ct)
    {
        var settings = job.Settings;
        var list = new List<Email.EmailAttachment>();
        bool wantPdf = settings.EmailAttachPdf && host is not null;
        bool wantDocx = settings.EmailAttachDocx && AppServices.License.IsPro;
        if (!wantPdf && !wantDocx) return list;

        var stem = Email.EmailOutbox.SafeStem(string.IsNullOrWhiteSpace(job.SourceLabel) ? Path.GetFileNameWithoutExtension(outPath) : job.SourceLabel);
        var temp = Directory.CreateTempSubdirectory("MarkSmith-email-").FullName;
        try
        {
            if (wantPdf)
            {
                try
                {
                    var pdf = Path.Combine(temp, stem + ".pdf");
                    await _pdf.ExportAsync(host!, AppServices.MarkdownHtml.Render(md, settings, theme, job.Classification), pdf, settings, md);
                    list.Add(new Email.EmailAttachment(stem + ".pdf", await File.ReadAllBytesAsync(pdf, ct), "application/pdf"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* left off; the email still goes */ }
            }
            ct.ThrowIfCancellationRequested();
            if (wantDocx)
            {
                try
                {
                    IReadOnlyList<byte[]?>? pngs = md.Contains("```mermaid", StringComparison.Ordinal) && host is not null
                        ? await _mermaid.RenderMermaidPngsAsync(host, md, settings, theme)
                        : null;
                    var docx = Path.Combine(temp, stem + ".docx");
                    await _docx.ExportAsync(md, docx, settings, pngs);
                    list.Add(new Email.EmailAttachment(stem + ".docx", await File.ReadAllBytesAsync(docx, ct),
                        "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* left off; the email still goes */ }
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
        return list;
    }

    /// <summary>Exports to a temporary file and returns its bytes (the local API's /api/convert).</summary>
    public async Task<byte[]> ExportToBytesAsync(AutomationExportJob job)
    {
        var fmt = OutputFormats.Normalize(job.Format) ?? OutputFormats.Pdf;
        var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.{fmt}");
        var written = await ExportAsync(new AutomationExportJob
        {
            Markdown = job.Markdown, Format = fmt, OutputPath = tmp, Settings = job.Settings, Host = job.Host,
            Classification = job.Classification, SourceLabel = job.SourceLabel, EmailSubject = job.EmailSubject,
            BaseDirectory = job.BaseDirectory, AllowRunningDoc = job.AllowRunningDoc,
        });
        try { return await File.ReadAllBytesAsync(written); }
        finally { if (written == tmp) TryDelete(tmp); }
    }

    /// <summary>Documents batch convert picks up in <paramref name="folder"/>: everything the editor
    /// can open (Markdown, text, Word, HTML, email…), sorted, skipping the output folder itself so
    /// a batch into a subfolder of its source can't feed on its own results.</summary>
    public static string[] FindBatchSources(string folder, bool recursive, string? outputFolder = null)
    {
        var skip = string.IsNullOrWhiteSpace(outputFolder) ? null : Path.GetFullPath(outputFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var source = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(Plugins.PluginFileReader.CanOpen)
            .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)) // Office lock files
            .Where(f => skip is null || string.Equals(skip, source, StringComparison.OrdinalIgnoreCase)
                        || !Path.GetFullPath(f).StartsWith(skip, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// Converts <paramref name="files"/> one by one into <paramref name="outputFolder"/>, keeping
    /// each file's folder relative to <paramref name="baseFolder"/> (null = flat). One file failing
    /// never stops the rest, and an output that would overwrite one of its own sources (a .docx
    /// batch-converted to Word into its own folder) gets " (converted)" added instead.
    /// </summary>
    public async Task<BatchConvertResult> ConvertFilesAsync(
        IReadOnlyList<string> files,
        string? baseFolder,
        string outputFolder,
        string format,
        AppSettings settings,
        IWebRenderHost? host,
        Func<string, string>? prepare = null,
        Action<int, int, string>? progress = null,
        Action<string, string, string>? recorded = null,
        CancellationToken ct = default)
    {
        var fmt = OutputFormats.Normalize(format)
            ?? throw new ArgumentException($"MarkSmith can't batch-convert to \"{format}\". Pick PDF, Word, PowerPoint, EPUB or an email draft.", nameof(format));
        var result = new BatchConvertResult { Format = fmt, OutputFolder = outputFolder, Total = files.Count };
        Directory.CreateDirectory(outputFolder);
        var sources = new HashSet<string>(files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
            var file = files[i];
            var name = Path.GetFileName(file);
            progress?.Invoke(i + 1, files.Count, name);

            if (OutputFormats.ProFeature(fmt) == FeatureId.DocxExport && !AppServices.License.CanExportDocx)
            {
                // "Trial quota exhausted" only when there was a trial: a free user who never
                // started one was told their trial had run out.
                var state = AppServices.License.State;
                result.Failures.Add(state.TrialUsed
                    ? $"{name}: DOCX export trial quota exhausted. {ProGate.ApiLine(FeatureId.DocxExport, state)}"
                    : $"{name}: {ProGate.ApiLine(FeatureId.DocxExport, state)}");
                continue;
            }

            string? outPath = null;
            try { await Lock.WaitAsync(ct); }
            catch (OperationCanceledException) { result.Cancelled = true; break; }
            try
            {
                var raw = await Plugins.PluginFileReader.ReadAsMarkdownAsync(file);
                var md = prepare is null ? raw : prepare(raw);
                var relDir = baseFolder is null ? "" : Path.GetDirectoryName(Path.GetRelativePath(baseFolder, file)) ?? "";
                outPath = UniqueOutputPath(Path.Combine(outputFolder, relDir), Path.GetFileNameWithoutExtension(file), fmt, sources, claimed);
                var written = await ExportAsync(new AutomationExportJob
                {
                    Markdown = md, Format = fmt, OutputPath = outPath, Settings = settings, Host = host,
                    SourceLabel = Path.GetFileNameWithoutExtension(file), BaseDirectory = Path.GetDirectoryName(file),
                    AllowRunningDoc = false,
                }, ct);
                result.Produced.Add(written);
                recorded?.Invoke(written, md, file);
            }
            catch (OperationCanceledException) { result.Cancelled = true; break; }
            catch (Exception ex)
            {
                result.Failures.Add($"{name}: {ExportFailureMessage.Describe(OutputFormats.Kind(fmt), ex, outPath)}");
            }
            finally { Lock.Release(); }
        }
        return result;
    }

    // "<stem>.<fmt>", or "<stem> (converted).<fmt>" / "(converted 2)" when that is one of the batch's
    // own sources or another file of this batch already claimed it (a.md and a.txt both → a.pdf).
    private static string UniqueOutputPath(string dir, string stem, string fmt, HashSet<string> sources, HashSet<string> claimed)
    {
        var path = Path.GetFullPath(Path.Combine(dir, $"{stem}.{fmt}"));
        for (var n = 1; sources.Contains(path) || claimed.Contains(path); n++)
            path = Path.GetFullPath(Path.Combine(dir, n == 1 ? $"{stem} (converted).{fmt}" : $"{stem} (converted {n}).{fmt}"));
        claimed.Add(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file; the OS cleans %TEMP% */ }
    }
}
