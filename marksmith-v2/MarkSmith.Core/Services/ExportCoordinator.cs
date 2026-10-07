using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.ViewModels;

namespace MarkSmith.Services;

public sealed class ExportCoordinator
{
    private readonly PdfExportService _pdfExport = new();
    private readonly DocxExportService _docxExport = new();
    private readonly PptxExportService _pptxExport = new();
    private readonly EpubExportService _epubExport = new();

    /// <summary>Shared with every other export that uses the preview engine (batch, API, watch folder).</summary>
    public SemaphoreSlim ConvertLock => AppServices.AutomationExport.Lock;

    public async Task ExportToPdfAsync(IWebRenderHost? host, string html, string outPath, AppSettings settings, string? markdown = null)
    {
        if (host is null || !await host.EnsureReadyAsync())
        {
            throw new InvalidOperationException("The preview engine couldn't start.");
        }
        await _pdfExport.ExportAsync(host, html, outPath, settings, markdown);
    }

    public async Task ExportToDocxAsync(string markdown, string outPath, AppSettings settings)
    {
        await _docxExport.ExportAsync(markdown, outPath, settings);
    }

    public async Task ExportToPptxAsync(string markdown, string outPath, AppSettings settings)
    {
        await _pptxExport.ExportAsync(markdown, outPath, settings);
    }

    public async Task ExportToEpubAsync(string markdown, string outPath, AppSettings settings)
    {
        await _epubExport.ExportAsync(markdown, outPath, settings);
    }

    public string ExportToHtml(string markdown, AppSettings settings)
    {
        var theme = AppServices.Themes.GetOrDefault(settings.Theme);
        return AppServices.MarkdownHtml.Render(markdown, settings, theme, null);
    }

    /// <summary>The requested formats in canonical form. "both" is PDF + Word; anything no exporter
    /// writes is dropped; nothing usable falls back to <paramref name="defaultFormat"/>, then PDF.</summary>
    public static string[] ParseFormats(string? format, string? defaultFormat = null)
    {
        var fallback = OutputFormats.Normalize(defaultFormat) ?? OutputFormats.Pdf;
        if (string.IsNullOrWhiteSpace(format)) return new[] { fallback };
        if (format.Trim().Equals("both", StringComparison.OrdinalIgnoreCase)) return new[] { OutputFormats.Pdf, OutputFormats.Docx };
        var fmts = format.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(OutputFormats.Normalize)
            .OfType<string>()
            .Distinct().ToArray();
        return fmts.Length > 0 ? fmts : new[] { fallback };
    }

    private static AutomationExportService Exporter => AppServices.AutomationExport;

    /// <summary>The clipboard watcher, the browser extension and /api/ingest land the document in
    /// the editor; with "auto-export" on, this writes it out in the default format (or the formats
    /// the request asked for) with no clicks.</summary>
    public async Task AutoExportIngestAsync(
        MainViewModel vm,
        OutputOverride? output,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Action<string>? showToast = null,
        Func<Task>? onCompletedRefresh = null)
    {
        await ConvertLock.WaitAsync();
        try
        {
            var md = vm.PastedMarkdown;
            if (string.IsNullOrWhiteSpace(md)) return;

            var settings = AppServices.Settings.Current.CloneWith(output);
            var formats = ParseFormats(output?.Format, settings.TargetFormat);
            if (!AutomationPolicy.Allows(AppServices.License.State, formats))
            {
                vm.StatusText = ProGate.StatusLine(FeatureId.AutoExportIngest, AppServices.License.State) + " " + AutomationPolicy.EmailIsFreeHint;
                vm.StatusSeverity = StatusSeverity.Warning;
                return;
            }
            if (formats.Any(OutputFormats.NeedsRenderHost) && (host is null || !await host.EnsureReadyAsync()))
            {
                vm.StatusText = "Auto-export failed: the preview engine couldn't start, and a PDF needs it. The document is in the editor; try Export again.";
                vm.StatusSeverity = StatusSeverity.Error;
                return;
            }

            using var offscreenScope = beginOffscreen?.Invoke();
            Directory.CreateDirectory(settings.OutputFolder);
            // The conversation's title when the extension sent one, else the detected assistant.
            var title = SafeStem(vm.SuggestedTitle);
            var label = title.Length > 0 ? title : (vm.LastClassification?.SourceName ?? "chat").Replace(" ", "");
            var stem = Path.Combine(settings.OutputFolder, $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}");

            var produced = new List<string>();
            var failures = new List<string>();
            foreach (var fmt in formats)
            {
                var outPath = $"{stem}.{fmt}";
                try
                {
                    var written = await Exporter.ExportAsync(new AutomationExportJob
                    {
                        Markdown = md, Format = fmt, OutputPath = outPath, Settings = settings, Host = host,
                        Classification = vm.LastClassification,
                        SourceLabel = title.Length > 0 ? title : null,
                        EmailSubject = output?.EmailSubject,
                    });
                    produced.Add(written);
                    vm.RecordExport(OutputFormats.Kind(fmt), written, md);
                }
                catch (Exception ex)
                {
                    // One format failing (the PDF open in Acrobat, say) used to abort the whole run
                    // and leave a raw exception message. Keep going and report each reason plainly.
                    failures.Add(ExportFailureMessage.Describe(OutputFormats.Kind(fmt), ex, outPath));
                }
            }

            if (produced.Count > 0)
            {
                vm.LastOutputPath = produced[^1];
                var names = string.Join(", ", produced.Select(Path.GetFileName));
                var message = $"Auto-exported {names}"
                    + (failures.Count > 0 ? " · " + string.Join(" · ", failures) : "")
                    + $" · in {Path.GetDirectoryName(produced[^1])}";
                vm.AnnounceExport(message, produced[^1]);
                vm.StatusSeverity = failures.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success;
                showToast?.Invoke(produced[^1]);
                await PublishToCloudAsync(vm, settings, produced);
            }
            else if (failures.Count > 0)
            {
                vm.StatusText = "Auto-export failed: " + string.Join(" · ", failures);
                vm.StatusSeverity = StatusSeverity.Error;
            }
        }
        catch (Exception ex)
        {
            vm.StatusText = "Auto-export failed: " + ExportFailureMessage.Describe("Auto-export", ex, null);
            vm.StatusSeverity = StatusSeverity.Error;
        }
        finally
        {
            ConvertLock.Release();
            if (onCompletedRefresh is not null)
            {
                await onCompletedRefresh();
            }
        }
    }

    // Cloud auto-publish (Task 9): mirror each produced file into the configured cloud drive (a
    // local sync-folder copy, or a WebDAV PUT). Best-effort: a failed sync never fails the export
    // that just succeeded, but the status line says so instead of implying it was published.
    private static async Task PublishToCloudAsync(MainViewModel vm, AppSettings settings, IReadOnlyList<string> produced)
    {
        if (!settings.CloudAutoPublish || produced.Count == 0) return;
        if (string.IsNullOrWhiteSpace(settings.CloudProviderId))
        {
            vm.StatusText += " · Cloud publish skipped: no cloud provider is set in Settings.";
            vm.StatusSeverity = StatusSeverity.Warning;
            return;
        }

        var publishFailures = new List<string>();
        foreach (var p in produced)
        {
            try
            {
                await AppServices.CloudStorage.PublishAsync(p, settings.CloudProviderId, settings.CloudSubfolder,
                    settings.WebDavEndpoint, settings.WebDavUser, settings.WebDavToken);
            }
            catch (Exception cex)
            {
                publishFailures.Add(Path.GetFileName(p));
                System.Diagnostics.Debug.WriteLine($"Cloud publish failed for {Path.GetFileName(p)}: {cex.Message}");
            }
        }

        if (publishFailures.Count > 0)
        {
            vm.StatusText += $" · Saved here, but the cloud copy failed for {string.Join(", ", publishFailures)}. Check the connection and sign-in in Settings.";
            vm.StatusSeverity = StatusSeverity.Warning;
        }
        else
        {
            vm.StatusText += " · Also published to the cloud.";
        }
    }

    // What each watched file last held, so a save that didn't change the text (or the burst of
    // Changed events one save raises) isn't ingested and exported again.
    private readonly Dictionary<string, string> _watchedContent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A new or changed Markdown file in the watch folder: load it into the editor and,
    /// with "convert automatically" on, export it next to the other exports under its own name.</summary>
    public async Task OnWatchedFileAsync(
        MainViewModel vm,
        string path,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Action<string>? showToast = null,
        Func<Task>? onCompletedRefresh = null)
    {
        var name = Path.GetFileName(path);

        // The file open in the editor is the user's own work in progress: saving it with Ctrl+S
        // used to bounce it back in as an "ingest", re-running the AI clean-up over their edits.
        if (vm.IsOpenDocument(path)) return;

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex)
        {
            vm.StatusText = $"Watch folder: couldn't read {name}. {ExportFailureMessage.Describe("Watch folder", ex, null)}";
            vm.StatusSeverity = StatusSeverity.Error;
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) return; // a file being created; its content arrives in a later event
        lock (_watchedContent)
        {
            if (_watchedContent.TryGetValue(path, out var last) && last == text) return;
            _watchedContent[path] = text;
        }

        vm.IngestMarkdown(text, name);
        if (!vm.WatchFolderAutoConvert) return;

        var settings = AppServices.Settings.Current;
        var fmt = OutputFormats.Normalize(settings.TargetFormat) ?? OutputFormats.Pdf;
        if (!AutomationPolicy.Allows(AppServices.License.State, fmt))
        {
            vm.StatusText = ProGate.StatusLine(FeatureId.WatchFolder, AppServices.License.State) + " " + AutomationPolicy.EmailIsFreeHint;
            vm.StatusSeverity = StatusSeverity.Warning;
            return;
        }
        if (OutputFormats.NeedsRenderHost(fmt) && (host is null || !await host.EnsureReadyAsync()))
        {
            vm.StatusText = $"Watch folder: {name} is in the editor, but the preview engine couldn't start to make the PDF. Use Export to try again.";
            vm.StatusSeverity = StatusSeverity.Error;
            return;
        }

        using var offscreenScope = beginOffscreen?.Invoke();
        var stem = Path.GetFileNameWithoutExtension(path);
        var outPath = Path.Combine(settings.OutputFolder, $"{stem}.{fmt}");
        await ConvertLock.WaitAsync();
        try
        {
            var md = vm.PastedMarkdown;
            var written = await Exporter.ExportAsync(new AutomationExportJob
            {
                Markdown = md, Format = fmt, OutputPath = outPath, Settings = settings, Host = host,
                Classification = vm.LastClassification, SourceLabel = stem,
                BaseDirectory = Path.GetDirectoryName(path),
            });
            vm.LastOutputPath = written;
            vm.RecordExport(OutputFormats.Kind(fmt), written, md, sourcePath: path);
            vm.AnnounceExport($"Watch folder: {name} → {Path.GetFileName(written)} · in {Path.GetDirectoryName(written)}", written);
            vm.StatusSeverity = StatusSeverity.Success;
            showToast?.Invoke(written);
            await PublishToCloudAsync(vm, settings, new[] { written });
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Watch folder ({name}): " + ExportFailureMessage.Describe(OutputFormats.Kind(fmt), ex, outPath);
            vm.StatusSeverity = StatusSeverity.Error;
        }
        finally
        {
            ConvertLock.Release();
            if (onCompletedRefresh is not null)
            {
                await onCompletedRefresh();
            }
        }
    }

    /// <summary>/api/convert: the document in <c>output.Format</c> (or the default output format) as bytes.</summary>
    public async Task<byte[]> ConvertForApiAsync(
        MainViewModel vm,
        string markdown,
        OutputOverride? output,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Func<Task>? onCompletedRefresh = null)
    {
        var settings = AppServices.Settings.Current.CloneWith(output);
        var fmt = OutputFormats.Normalize(output?.Format) ?? OutputFormats.Normalize(settings.TargetFormat) ?? OutputFormats.Pdf;
        if (OutputFormats.NeedsRenderHost(fmt) && (host is null || !await host.EnsureReadyAsync()))
        {
            throw new InvalidOperationException("The preview engine couldn't start.");
        }

        using var offscreenScope = beginOffscreen?.Invoke();
        await ConvertLock.WaitAsync();
        try
        {
            var md = markdown;
            var classification = AppServices.LlmSource.Classify(md);
            (md, _) = AppServices.LlmSource.RepairArtifacts(md, classification);
            if (settings.NormalizeLlm)
                (md, _) = AppServices.LlmSource.NormalizeStyle(md, classification, settings.CustomNormalizationRules);
            return await Exporter.ExportToBytesAsync(new AutomationExportJob
            {
                Markdown = md, Format = fmt, Settings = settings, Host = host, Classification = classification,
                SourceLabel = output?.SourceTitle, EmailSubject = output?.EmailSubject,
            });
        }
        finally
        {
            ConvertLock.Release();
            if (onCompletedRefresh is not null)
            {
                await onCompletedRefresh();
            }
        }
    }

    /// <summary>Batch convert a folder (the Batch convert button and /api/batch). Every document
    /// the editor can open is converted, each into the output folder under its own name.</summary>
    public async Task<BatchConvertResult> BatchConvertForApiAsync(
        MainViewModel vm,
        string folderPath,
        string format,
        OutputOverride? ovr,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Func<Task>? onCompletedRefresh = null,
        bool recursive = false)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        var settings = AppServices.Settings.Current.CloneWith(ovr);
        var fmt = OutputFormats.Normalize(format)
            ?? throw new ArgumentException($"MarkSmith can't batch-convert to \"{format}\". Use pdf, docx, pptx, epub, eml or msg.");
        var outFolder = settings.OutputFolder;
        var files = AutomationExportService.FindBatchSources(folderPath, recursive, outFolder);
        if (files.Length == 0) return new BatchConvertResult { Format = fmt, OutputFolder = outFolder };

        if (OutputFormats.NeedsRenderHost(fmt) && (host is null || !await host.EnsureReadyAsync()))
        {
            throw new InvalidOperationException("The preview engine couldn't start, and a PDF needs it.");
        }

        using var offscreenScope = beginOffscreen?.Invoke();
        try
        {
            return await AppServices.BatchConvert.ConvertFilesAsync(
                host, files, folderPath, outFolder, fmt, settings,
                progressCallback: msg => { if (msg.StartsWith("Converting ", StringComparison.Ordinal)) vm.StatusText = "Batch: " + msg; },
                recorded: (written, md, source) => vm.RecordExport(OutputFormats.Kind(fmt), written, md, sourcePath: source));
        }
        finally
        {
            if (onCompletedRefresh is not null)
            {
                await onCompletedRefresh();
            }
        }
    }

    private static string SafeStem(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.', '-');
        return clean.Length > 60 ? clean[..60].TrimEnd() : clean;
    }
}
