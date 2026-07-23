using System;
using System.IO;
using System.Threading.Tasks;
using MdToPdf.Models;

namespace MdToPdf.Services;

public sealed class BatchConvertService
{
    private readonly PdfExportService _pdfExport = new();
    private readonly DocxExportService _docxExport = new();
    private readonly MermaidHarvestService _mermaidHarvest = new();

    public async Task ConvertDirectoryAsync(
        IWebRenderHost? host,
        string sourceDir,
        string outputDir,
        string targetFormat,
        AppSettings settings,
        Action<string>? progressCallback = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        Directory.CreateDirectory(outputDir);

        var mdFiles = Directory.GetFiles(sourceDir, "*.md", SearchOption.AllDirectories);
        
        var format = targetFormat.ToLowerInvariant();
        bool isPdf = format == "pdf";
        bool isDocx = format == "docx";

        if (!isPdf && !isDocx)
            throw new ArgumentException("Target format must be 'pdf' or 'docx'");

        foreach (var file in mdFiles)
        {
            try
            {
                var relPath = Path.GetRelativePath(sourceDir, file);
                var outFileDir = Path.Combine(outputDir, Path.GetDirectoryName(relPath) ?? "");
                Directory.CreateDirectory(outFileDir);

                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                var outFilePath = Path.Combine(outFileDir, $"{fileNameWithoutExt}.{format}");

                progressCallback?.Invoke($"Converting {file} to {format}...");

                var mdContent = await File.ReadAllTextAsync(file);

                // Run basic repairs like MainViewModel does
                var classification = AppServices.LlmSource.Classify(mdContent);
                (mdContent, _) = AppServices.LlmSource.RepairArtifacts(mdContent, classification);
                if (settings.NormalizeLlm)
                {
                    (mdContent, _) = AppServices.LlmSource.NormalizeStyle(mdContent, classification);
                }

                if (isPdf)
                {
                    if (host == null)
                    {
                        progressCallback?.Invoke($"Skipped {file}: PDF export requires a web render host.");
                        continue;
                    }
                    var html = AppServices.ViewModel.BuildPreviewHtml(mdContent);
                    await _pdfExport.ExportAsync(host, html, outFilePath, settings);
                }
                else if (isDocx)
                {
                    var hasMermaid = mdContent.Contains("```mermaid", StringComparison.Ordinal);
                    var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                    
                    System.Collections.Generic.List<Mermaid.HarvestedDiagram?>? geometry = null;
                    System.Collections.Generic.List<Mermaid.GenericDiagram?>? genericGeom = null;
                    System.Collections.Generic.List<byte[]?>? mermaidImgs = null;

                    if (hasMermaid && host != null)
                    {
                        if (settings.MermaidDocxMode == 1)
                        {
                            var mode = settings.OversizedDiagramMode;
                            if (mode == 1 || (mode >= 3 && mode <= 8))
                            {
                                geometry = await _mermaidHarvest.HarvestMermaidGeometryAsync(host, mdContent, settings, theme);
                                var usable = geometry?.Any(g => g is { IsEmpty: false }) == true;
                                if (!usable) geometry = null;
                            }
                            genericGeom = await _mermaidHarvest.HarvestGenericGeometryAsync(host, mdContent, settings);
                        }
                        mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(host, mdContent, settings, theme);
                    }

                    await _docxExport.ExportAsync(mdContent, outFilePath, settings, mermaidImgs, null, geometry, genericGeom, null);
                }

                progressCallback?.Invoke($"Successfully converted: {outFilePath}");
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke($"Failed to convert {file}: {ex.Message}");
            }
        }
    }
}
