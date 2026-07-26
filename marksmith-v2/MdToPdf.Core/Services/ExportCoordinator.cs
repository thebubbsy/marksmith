using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MdToPdf.Models;
using MdToPdf.ViewModels;

namespace MdToPdf.Services;

public sealed class ExportCoordinator
{
    private readonly SemaphoreSlim _convertLock = new(1, 1);
    private readonly MermaidHarvestService _mermaidHarvest = new();
    private readonly PdfExportService _pdfExport = new();
    private readonly DocxExportService _docxExport = new();
    private readonly PptxExportService _pptxExport = new();
    private readonly EpubExportService _epubExport = new();

    public SemaphoreSlim ConvertLock => _convertLock;

    public static string[] ParseFormats(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return new[] { "pdf" };
        if (format.Trim().Equals("both", StringComparison.OrdinalIgnoreCase)) return new[] { "pdf", "docx" };
        var fmts = format.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .Where(f => f is "pdf" or "docx" or "pptx" or "epub")
            .Distinct().ToArray();
        return fmts.Length > 0 ? fmts : new[] { "pdf" };
    }

    public async Task AutoExportIngestAsync(
        MainViewModel vm,
        OutputOverride? output,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Action<string>? showToast = null,
        Func<Task>? onCompletedRefresh = null)
    {
        await _convertLock.WaitAsync();
        try
        {
            var md = vm.PastedMarkdown;
            if (string.IsNullOrWhiteSpace(md)) return;

            if (host is null || !await host.EnsureReadyAsync())
            {
                vm.StatusText = "Auto-generate failed: the preview engine couldn't start. Try the export again.";
                vm.StatusSeverity = StatusSeverity.Error;
                return;
            }

            using var offscreenScope = beginOffscreen?.Invoke();
            var settings = AppServices.Settings.Current.CloneWith(output);
            Directory.CreateDirectory(settings.OutputFolder);
            var label = (vm.LastClassification?.SourceName ?? "chat").Replace(" ", "");
            var stem = Path.Combine(settings.OutputFolder, $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}");

            var produced = new List<string>();
            var pending = new List<string>();
            var formats = ParseFormats(output?.Format);

            IReadOnlyList<byte[]?>? mermaidImgs = null;
            IReadOnlyList<Mermaid.HarvestedDiagram?>? mermaidGeo = null;
            IReadOnlyList<Mermaid.GenericDiagram?>? mermaidGen = null;

            if (formats.Contains("docx") && md.Contains("```mermaid", StringComparison.Ordinal))
            {
                var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(host, md, settings, theme);
                if (settings.MermaidDocxMode == 1)
                {
                    mermaidGeo = await _mermaidHarvest.HarvestMermaidGeometryAsync(host, md, settings, theme);
                    mermaidGen = await _mermaidHarvest.HarvestGenericGeometryAsync(host, md, settings);
                }
            }

            foreach (var fmt in formats)
            {
                var outPath = $"{stem}.{fmt}";
                try
                {
                    switch (fmt)
                    {
                        case "pdf":
                            var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                            var html = AppServices.MarkdownHtml.Render(md, settings, theme, vm.LastClassification);
                            await _pdfExport.ExportAsync(host, html, outPath, settings);
                            break;
                        case "docx":
                            if (settings.AppendToRunningDoc && !string.IsNullOrWhiteSpace(settings.RunningDocPath))
                            {
                                await _docxExport.ExportAppendAsync(md, settings.RunningDocPath, settings, mermaidImgs, mermaidGeo, mermaidGen);
                                outPath = settings.RunningDocPath;
                            }
                            else
                            {
                                await _docxExport.ExportAsync(md, outPath, settings, mermaidImgs,
                                    settings.NormalizeLlm ? vm.LastClassification?.AppliedFixes : null, mermaidGeo, mermaidGen);
                            }
                            break;
                        case "pptx":
                            await _pptxExport.ExportAsync(md, outPath, settings);
                            break;
                        case "epub":
                            await _epubExport.ExportAsync(md, outPath, settings);
                            break;
                    }
                    produced.Add(outPath);
                    vm.RecordExport(fmt.ToUpperInvariant(), outPath, md);
                }
                catch (NotImplementedException)
                {
                    pending.Add(fmt.ToUpperInvariant());
                }
            }

            if (produced.Count > 0)
            {
                vm.LastOutputPath = produced[^1];
                vm.StatusText = $"Auto-generated: {string.Join(", ", produced.Select(Path.GetFileName))}"
                    + (pending.Count > 0 ? $"  ({string.Join("/", pending)} coming soon)" : "");
                vm.StatusSeverity = StatusSeverity.Success;
                showToast?.Invoke(produced[^1]);
            }
            else if (pending.Count > 0)
            {
                vm.StatusText = $"{string.Join("/", pending)} export is on the roadmap — not yet available.";
                vm.StatusSeverity = StatusSeverity.Warning;
            }

            // Cloud auto-publish (Task 9): mirror each produced file into the configured cloud drive
            // (a local sync-folder copy, or a WebDAV PUT). Best-effort — a failed sync must never
            // fail the export that just succeeded.
            if (settings.CloudAutoPublish && produced.Count > 0 && !string.IsNullOrWhiteSpace(settings.CloudProviderId))
            {
                foreach (var p in produced)
                {
                    try
                    {
                        await AppServices.CloudStorage.PublishAsync(p, settings.CloudProviderId, settings.CloudSubfolder,
                            settings.WebDavEndpoint, settings.WebDavUser, settings.WebDavToken);
                    }
                    catch (Exception cex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Cloud publish failed for {Path.GetFileName(p)}: {cex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Auto-generate failed: {ex.Message}";
            vm.StatusSeverity = StatusSeverity.Error;
        }
        finally
        {
            _convertLock.Release();
            if (onCompletedRefresh is not null)
            {
                await onCompletedRefresh();
            }
        }
    }

    public async Task OnWatchedFileAsync(
        MainViewModel vm,
        string path,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Action<string>? showToast = null,
        Func<Task>? onCompletedRefresh = null)
    {
        vm.IngestFile(path);
        if (!vm.WatchFolderAutoConvert) return;
        if (!AppServices.License.CanAutomate)
        {
            vm.StatusText = "Hands-free watch-folder conversion is a Marksmith Pro feature. Upgrade in Settings ⚙.";
            vm.StatusSeverity = StatusSeverity.Warning;
            return;
        }
        if (host is null || !await host.EnsureReadyAsync()) return;

        using var offscreenScope = beginOffscreen?.Invoke();

        try
        {
            await _convertLock.WaitAsync();
            try
            {
                var html = vm.BuildPreviewHtml(vm.PastedMarkdown);
                var folder = AppServices.Settings.Current.OutputFolder;
                Directory.CreateDirectory(folder);
                var outPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".pdf");
                await _pdfExport.ExportAsync(host, html, outPath, AppServices.Settings.Current);

                vm.LastOutputPath = outPath;
                vm.RecordExport("PDF", outPath, vm.PastedMarkdown);
                vm.StatusText = $"Auto-converted: {outPath}";
                vm.StatusSeverity = StatusSeverity.Success;
                showToast?.Invoke(outPath);
            }
            finally
            {
                _convertLock.Release();
                if (onCompletedRefresh is not null)
                {
                    await onCompletedRefresh();
                }
            }
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Auto-convert failed for {Path.GetFileName(path)}: {ex.Message}";
            vm.StatusSeverity = StatusSeverity.Error;
        }
    }

    public async Task<byte[]> ConvertForApiAsync(
        MainViewModel vm,
        string markdown,
        OutputOverride? output,
        IWebRenderHost? host,
        Func<IDisposable>? beginOffscreen = null,
        Func<Task>? onCompletedRefresh = null)
    {
        if (host is null || !await host.EnsureReadyAsync())
        {
            throw new InvalidOperationException("The preview engine couldn't start.");
        }

        using var offscreenScope = beginOffscreen?.Invoke();
        await _convertLock.WaitAsync();
        try
        {
            var settings = AppServices.Settings.Current.CloneWith(output);
            var md = markdown;
            var classification = AppServices.LlmSource.Classify(md);
            (md, _) = AppServices.LlmSource.RepairArtifacts(md, classification);
            if (settings.NormalizeLlm)
                (md, _) = AppServices.LlmSource.NormalizeStyle(md, classification);
            var theme = AppServices.Themes.GetOrDefault(settings.Theme);
            var html = AppServices.MarkdownHtml.Render(md, settings, theme, classification);
            var fmt = settings.TargetFormat.ToLowerInvariant();
            if (output?.Format is { Length: > 0 } outputFmt)
                fmt = outputFmt.ToLowerInvariant();

            if (fmt == "docx")
            {
                IReadOnlyList<byte[]?>? mermaidImgs = null;
                IReadOnlyList<Mermaid.HarvestedDiagram?>? mermaidGeo = null;
                IReadOnlyList<Mermaid.GenericDiagram?>? mermaidGen = null;
                if (md.Contains("```mermaid", StringComparison.Ordinal))
                {
                    mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(host, md, settings, theme);
                    if (settings.MermaidDocxMode == 1)
                    {
                        mermaidGeo = await _mermaidHarvest.HarvestMermaidGeometryAsync(host, md, settings, theme);
                        mermaidGen = await _mermaidHarvest.HarvestGenericGeometryAsync(host, md, settings);
                    }
                }

                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.docx");
                if (settings.AppendToRunningDoc && !string.IsNullOrWhiteSpace(settings.RunningDocPath))
                {
                    await _docxExport.ExportAppendAsync(md, settings.RunningDocPath, settings, mermaidImgs, mermaidGeo, mermaidGen);
                    tmp = settings.RunningDocPath;
                }
                else
                {
                    await _docxExport.ExportAsync(md, tmp, settings, mermaidImgs,
                        settings.NormalizeLlm ? classification.AppliedFixes : null, mermaidGeo, mermaidGen);
                }
                var bytes = await File.ReadAllBytesAsync(tmp);
                if (tmp != settings.RunningDocPath) File.Delete(tmp);
                return bytes;
            }
            else if (fmt == "pptx")
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.pptx");
                await _pptxExport.ExportAsync(md, tmp, settings);
                var bytes = await File.ReadAllBytesAsync(tmp);
                File.Delete(tmp);
                return bytes;
            }
            else if (fmt == "epub")
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.epub");
                await _epubExport.ExportAsync(md, tmp, settings);
                var bytes = await File.ReadAllBytesAsync(tmp);
                File.Delete(tmp);
                return bytes;
            }
            else
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.pdf");
                await _pdfExport.ExportAsync(host, html, tmp, settings);
                var bytes = await File.ReadAllBytesAsync(tmp);
                File.Delete(tmp);
                return bytes;
            }
        }
        finally
        {
            _convertLock.Release();
            if (onCompletedRefresh is not null)
            {
                await onCompletedRefresh();
            }
        }
    }

    public async Task<object> BatchConvertForApiAsync(
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

        var files = Directory.GetFiles(folderPath, "*.md", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            return new { done = 0, failed = 0, message = "No .md files found." };
        }

        var settings = AppServices.Settings.Current.CloneWith(ovr);
        var fmt = format.ToLowerInvariant();
        var outFolder = settings.OutputFolder;
        Directory.CreateDirectory(outFolder);

        if (fmt == "pdf")
        {
            if (host is null || !await host.EnsureReadyAsync())
            {
                throw new InvalidOperationException("WebView2 initialization failed.");
            }
        }

        using var offscreenScope = beginOffscreen?.Invoke();
        int done = 0, failed = 0;
        var processedFiles = new List<string>();

        foreach (var f in files)
        {
            await _convertLock.WaitAsync();
            try
            {
                var md = vm.PrepareMarkdown(await Plugins.PluginFileReader.ReadAsMarkdownAsync(f));
                // Mirror the source's relative folder structure under the output folder so a
                // recursive batch doesn't collide same-named files from different subfolders.
                // (For a top-level-only batch relDir is "" and this is the same flat path as before.)
                var relDir = Path.GetDirectoryName(Path.GetRelativePath(folderPath, f)) ?? "";
                var outDir = Path.Combine(outFolder, relDir);
                Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "." + fmt);

                switch (fmt)
                {
                    case "pdf":
                        var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                        var html = AppServices.MarkdownHtml.Render(md, settings, theme, null);
                        // host is guaranteed non-null here: the fmt=="pdf" guard above throws otherwise.
                        await _pdfExport.ExportAsync(host!, html, outPath, settings);
                        break;
                    case "docx":
                        IReadOnlyList<byte[]?>? mermaidImgs = null;
                        IReadOnlyList<Mermaid.HarvestedDiagram?>? mermaidGeo = null;
                        IReadOnlyList<Mermaid.GenericDiagram?>? mermaidGen = null;
                        // Mermaid rendering/geometry harvesting needs a live web host. In headless/API
                        // batch runs host can be null — skip harvesting instead of crashing; the DOCX
                        // exporter falls back to parser-based ShapeForge or a code block for diagrams.
                        if (host is not null && md.Contains("```mermaid", StringComparison.Ordinal))
                        {
                            var docxTheme = AppServices.Themes.GetOrDefault(settings.Theme);
                            mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(host, md, settings, docxTheme);
                            if (settings.MermaidDocxMode == 1)
                            {
                                mermaidGeo = await _mermaidHarvest.HarvestMermaidGeometryAsync(host, md, settings, docxTheme);
                                mermaidGen = await _mermaidHarvest.HarvestGenericGeometryAsync(host, md, settings);
                            }
                        }
                        await _docxExport.ExportAsync(md, outPath, settings, mermaidImgs, null, mermaidGeo, mermaidGen);
                        break;
                    case "pptx":
                        await _pptxExport.ExportAsync(md, outPath, settings);
                        break;
                    case "epub":
                        await _epubExport.ExportAsync(md, outPath, settings);
                        break;
                }
                vm.RecordExport(fmt.ToUpperInvariant(), outPath, md);
                processedFiles.Add(outPath);
                done++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Batch convert failed for {f}: {ex}");
                failed++;
            }
            finally
            {
                _convertLock.Release();
            }
        }

        if (onCompletedRefresh is not null)
        {
            await onCompletedRefresh();
        }

        return new { done, failed, outputFolder = outFolder, files = processedFiles };
    }
}
