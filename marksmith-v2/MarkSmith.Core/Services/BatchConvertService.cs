using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models;

namespace MarkSmith.Services;

/// <summary>
/// Batch convert: a folder or a list of files into one format. The conversion itself is
/// <see cref="AutomationExportService.ConvertFilesAsync"/>; this adds the Word-licence check up
/// front and the source clean-up every batch gets (repairs, AI-quirk normalisation).
/// </summary>
public sealed class BatchConvertService
{
    /// <summary>Converts every document in <paramref name="sourceDir"/> and its subfolders,
    /// recreating the folder structure under <paramref name="outputDir"/>.</summary>
    public async Task<BatchConvertResult> ConvertDirectoryAsync(
        IWebRenderHost? host,
        string sourceDir,
        string outputDir,
        string targetFormat,
        AppSettings settings,
        Action<string>? progressCallback = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        var files = AutomationExportService.FindBatchSources(sourceDir, recursive: true, outputDir);
        return await ConvertFilesAsync(host, files, sourceDir, outputDir, targetFormat, settings, progressCallback, null, ct);
    }

    /// <summary>Converts the given files. <paramref name="baseFolder"/> null writes them all
    /// straight into <paramref name="outputDir"/> (dropped files from different folders).</summary>
    public async Task<BatchConvertResult> ConvertFilesAsync(
        IWebRenderHost? host,
        IReadOnlyList<string> files,
        string? baseFolder,
        string outputDir,
        string targetFormat,
        AppSettings settings,
        Action<string>? progressCallback = null,
        Action<string, string, string>? recorded = null,
        CancellationToken ct = default)
    {
        var format = OutputFormats.Normalize(targetFormat)
            ?? throw new ArgumentException($"MarkSmith can't batch-convert to \"{targetFormat}\". Pick PDF, Word, PowerPoint, EPUB or an email draft.", nameof(targetFormat));

        if (format == OutputFormats.Docx && !AppServices.License.CanExportDocx)
            throw new InvalidOperationException(ProGate.ApiLine(FeatureId.DocxExport, AppServices.License.State));

        Directory.CreateDirectory(outputDir);
        if (files.Count == 0) return new BatchConvertResult { Format = format, OutputFolder = outputDir };

        var result = await AppServices.AutomationExport.ConvertFilesAsync(
            files, baseFolder, outputDir, format, settings, host,
            prepare: md => Prepare(md, settings),
            progress: (n, total, name) => progressCallback?.Invoke($"Converting {n} of {total}: {name}…"),
            recorded: (written, md, source) =>
            {
                progressCallback?.Invoke($"Successfully converted: {written}");
                recorded?.Invoke(written, md, source);
            },
            ct: ct);
        foreach (var failure in result.Failures) progressCallback?.Invoke($"Failed to convert {failure}");
        return result;
    }

    // The same clean-up an opened file gets: correctness repairs always, style only when on.
    private static string Prepare(string md, AppSettings settings)
    {
        var classification = AppServices.LlmSource.Classify(md);
        (md, _) = AppServices.LlmSource.RepairArtifacts(md, classification);
        if (settings.NormalizeLlm)
            (md, _) = AppServices.LlmSource.NormalizeStyle(md, classification, settings.CustomNormalizationRules);
        return md;
    }
}
