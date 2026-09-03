using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using Markdig.Syntax;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Services;

/// <summary>
/// Incremental markdown block chunk delineated from an incoming asynchronous token stream.
/// </summary>
public sealed class MarkdownBlockChunk
{
    public required int SequenceIndex { get; init; }
    public required string Markdown { get; init; }
    public bool IsLast { get; init; }
}

/// <summary>
/// Type of auxiliary OpenXML part or relationship staged during parallel block rendering.
/// </summary>
public enum StagedPartType
{
    Hyperlink,
    Image,
    Chart,
    Diagram,
    CustomXml,
    Comment
}

/// <summary>
/// Thread-isolated staged part or relationship descriptor awaiting single-threaded package commit.
/// </summary>
public sealed class StagedPart
{
    public required string RelId { get; init; }
    public required StagedPartType Type { get; init; }
    public string? UriOrPath { get; init; }
    public byte[]? Data { get; init; }
    public string? ContentType { get; init; }
    public object? ExtraData { get; init; }
}

/// <summary>
/// Thread-safe registry for allocating unique atomic relationship IDs (rId), numbering IDs,
/// bookmark IDs, drawing IDs, and staging auxiliary package parts without zip archive lock contention.
/// </summary>
public sealed class ThreadSafePartRegistry
{
    private int _nextRelId = 10;
    private int _nextNumId = 2;
    private int _nextBookmarkId = 1;
    private int _nextDrawingId = 1000;
    private int _nextCommentId = 1;

    private readonly ConcurrentBag<StagedPart> _stagedParts = new();

    public string ReserveRelationshipId()
    {
        var id = Interlocked.Increment(ref _nextRelId);
        return $"rId{id}";
    }

    public int NextNumId() => Interlocked.Increment(ref _nextNumId);
    public int NextBookmarkId() => Interlocked.Increment(ref _nextBookmarkId);
    public uint NextDrawingId() => (uint)Interlocked.Increment(ref _nextDrawingId);
    public int NextCommentId() => Interlocked.Increment(ref _nextCommentId);

    public void Stage(StagedPart part)
    {
        _stagedParts.Add(part);
    }

    public IReadOnlyCollection<StagedPart> GetAllStaged() => _stagedParts.ToArray();

    public void CommitAllStaged(MainDocumentPart main)
    {
        foreach (var part in _stagedParts)
        {
            switch (part.Type)
            {
                case StagedPartType.Hyperlink:
                    if (!string.IsNullOrWhiteSpace(part.UriOrPath))
                    {
                        try
                        {
                            main.AddHyperlinkRelationship(new Uri(part.UriOrPath, UriKind.RelativeOrAbsolute), true, part.RelId);
                        }
                        catch
                        {
                            // If URI is malformed or already registered, fallback gracefully
                        }
                    }
                    break;

                case StagedPartType.Image:
                    if (part.Data is { Length: > 0 })
                    {
                        var isSvg = part.ContentType == "image/svg+xml";
                        var isJpeg = part.ContentType?.Contains("jpeg", StringComparison.OrdinalIgnoreCase) == true
                                     || part.ContentType?.Contains("jpg", StringComparison.OrdinalIgnoreCase) == true;
                        try
                        {
                            if (isSvg)
                            {
                                var imgPart = main.AddNewPart<ImagePart>("image/svg+xml", part.RelId);
                                using var ms = new MemoryStream(part.Data);
                                imgPart.FeedData(ms);
                            }
                            else
                            {
                                var imgPart = main.AddImagePart(isJpeg ? ImagePartType.Jpeg : ImagePartType.Png, part.RelId);
                                using var ms = new MemoryStream(part.Data);
                                imgPart.FeedData(ms);
                            }
                        }
                        catch
                        {
                            // Avoid throwing if relationship already exists
                        }
                    }
                    break;

                case StagedPartType.CustomXml:
                    if (part.Data is { Length: > 0 })
                    {
                        try
                        {
                            var customPart = main.AddCustomXmlPart(CustomXmlPartType.CustomXml, part.RelId);
                            using var ms = new MemoryStream(part.Data);
                            customPart.FeedData(ms);
                        }
                        catch { }
                    }
                    break;
            }
        }
    }
}

/// <summary>
/// Rendered block fragment containing OOXML elements ready for SAX serialization.
/// </summary>
public sealed class RenderedBlockChunk
{
    public required int SequenceIndex { get; init; }
    public required List<OpenXmlElement> Elements { get; init; }
    public bool IsLast { get; init; }
}

/// <summary>
/// Token stream block delimiter parser. Incremental state machine that splits incoming token chunks
/// into standalone Markdown block boundaries without waiting for EOF.
/// </summary>
public static class TokenStreamBlockSplitter
{
    public static async Task IngestTokenStreamAsync(
        IAsyncEnumerable<string> tokenStream,
        ChannelWriter<MarkdownBlockChunk> writer,
        CancellationToken ct = default)
    {
        var buffer = new StringBuilder();
        int sequenceIndex = 0;

        await foreach (var token in tokenStream.WithCancellation(ct))
        {
            if (string.IsNullOrEmpty(token)) continue;

            buffer.Append(token);
            ProcessBuffer(buffer, ref sequenceIndex, writer, isEof: false);
        }

        ProcessBuffer(buffer, ref sequenceIndex, writer, isEof: true);
        writer.Complete();
    }

    public static async Task IngestStreamAsync(
        Stream stream,
        ChannelWriter<MarkdownBlockChunk> writer,
        CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var buffer = new StringBuilder();
        int sequenceIndex = 0;

        char[] rentPool = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int charsRead;
            while ((charsRead = await reader.ReadAsync(rentPool, 0, rentPool.Length)) > 0)
            {
                if (ct.IsCancellationRequested) break;
                buffer.Append(rentPool, 0, charsRead);
                ProcessBuffer(buffer, ref sequenceIndex, writer, isEof: false);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rentPool);
        }

        ProcessBuffer(buffer, ref sequenceIndex, writer, isEof: true);
        writer.Complete();
    }

    private static void ProcessBuffer(
        StringBuilder buffer,
        ref int sequenceIndex,
        ChannelWriter<MarkdownBlockChunk> writer,
        bool isEof)
    {
        while (buffer.Length > 0)
        {
            var text = buffer.ToString();
            var trimmedStart = text.TrimStart('\r', '\n');
            var leadingWs = text.Length - trimmedStart.Length;

            if (trimmedStart.Length == 0)
            {
                buffer.Clear();
                return;
            }

            // 1. YAML Frontmatter at start of buffer
            if (trimmedStart.StartsWith("---") || trimmedStart.StartsWith("+++"))
            {
                var marker = trimmedStart.Substring(0, 3);
                var secondMarkerIdx = text.IndexOf("\n" + marker, leadingWs + 3, StringComparison.Ordinal);
                if (secondMarkerIdx >= 0)
                {
                    var endOfMarkerLine = text.IndexOf('\n', secondMarkerIdx + 1);
                    if (endOfMarkerLine < 0) endOfMarkerLine = text.Length;
                    var chunkText = text.Substring(0, endOfMarkerLine).Trim();
                    EmitChunk(chunkText, ref sequenceIndex, writer, false);
                    buffer.Remove(0, endOfMarkerLine);
                    continue;
                }
                else if (!isEof)
                {
                    return;
                }
            }

            // 2. Fenced Code Block (``` or ~~~)
            if (trimmedStart.StartsWith("```") || trimmedStart.StartsWith("~~~"))
            {
                var fenceMarker = trimmedStart.Substring(0, 3);
                var firstLineEnd = text.IndexOf('\n', leadingWs);
                if (firstLineEnd >= 0)
                {
                    var closingIdx = text.IndexOf("\n" + fenceMarker, firstLineEnd, StringComparison.Ordinal);
                    if (closingIdx >= 0)
                    {
                        var endOfFenceLine = text.IndexOf('\n', closingIdx + 1);
                        if (endOfFenceLine < 0) endOfFenceLine = text.Length;
                        var chunkText = text.Substring(0, endOfFenceLine).Trim();
                        EmitChunk(chunkText, ref sequenceIndex, writer, false);
                        buffer.Remove(0, endOfFenceLine);
                        continue;
                    }
                    else if (!isEof)
                    {
                        return;
                    }
                }
                else if (!isEof)
                {
                    return;
                }
            }

            // 3. Container Block (:::)
            if (trimmedStart.StartsWith(":::"))
            {
                var firstLineEnd = text.IndexOf('\n', leadingWs);
                if (firstLineEnd >= 0)
                {
                    var closingIdx = text.IndexOf("\n:::", firstLineEnd, StringComparison.Ordinal);
                    if (closingIdx >= 0)
                    {
                        var endOfFenceLine = text.IndexOf('\n', closingIdx + 1);
                        if (endOfFenceLine < 0) endOfFenceLine = text.Length;
                        var chunkText = text.Substring(0, endOfFenceLine).Trim();
                        EmitChunk(chunkText, ref sequenceIndex, writer, false);
                        buffer.Remove(0, endOfFenceLine);
                        continue;
                    }
                    else if (!isEof)
                    {
                        return;
                    }
                }
                else if (!isEof)
                {
                    return;
                }
            }

            // 4. Normal paragraph / block delimiter: blank line (\r?\n\s*\r?\n)
            var blankLineMatch = System.Text.RegularExpressions.Regex.Match(text, @"\r?\n\s*\r?\n");
            var headingMatch = System.Text.RegularExpressions.Regex.Match(text, @"(?:\r?\n)(?=#{1,6}\s+)");

            if (headingMatch.Success && (!blankLineMatch.Success || headingMatch.Index < blankLineMatch.Index))
            {
                if (headingMatch.Index > leadingWs)
                {
                    var chunkText = text.Substring(0, headingMatch.Index).Trim();
                    if (!string.IsNullOrWhiteSpace(chunkText))
                    {
                        EmitChunk(chunkText, ref sequenceIndex, writer, false);
                    }
                    buffer.Remove(0, headingMatch.Index);
                    continue;
                }
            }

            if (blankLineMatch.Success)
            {
                var chunkText = text.Substring(0, blankLineMatch.Index).Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    EmitChunk(chunkText, ref sequenceIndex, writer, false);
                }
                buffer.Remove(0, blankLineMatch.Index + blankLineMatch.Length);
                continue;
            }

            if (isEof)
            {
                var chunkText = text.Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    EmitChunk(chunkText, ref sequenceIndex, writer, true);
                }
                buffer.Clear();
                return;
            }

            return;
        }
    }

    private static void EmitChunk(string content, ref int sequenceIndex, ChannelWriter<MarkdownBlockChunk> writer, bool isLast)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        var chunk = new MarkdownBlockChunk
        {
            SequenceIndex = sequenceIndex++,
            Markdown = content,
            IsLast = isLast
        };
        writer.TryWrite(chunk);
    }
}

/// <summary>
/// High-throughput multi-threaded SAX streaming engine for OpenXML (.docx) generation.
/// Implements a 4-Stage Producer-Consumer pipeline:
/// Stage 1: Async Token Ingestion splitting stream into block boundaries incrementally.
/// Stage 2: Parallel Block Renderer Worker Pool translating Markdig AST blocks into thread-isolated OOXML fragments.
/// Stage 3: Thread-Safe Relationship &amp; Part Staging Registry with atomic rId allocation.
/// Stage 4: Deterministic SAX Sequencer Consumer writing block fragments in sequence index order directly into OpenXmlWriter.
/// </summary>
public sealed class StreamingDocxExportService
{
    private readonly int _workerCount;

    public StreamingDocxExportService(int? workerCount = null)
    {
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
    }

    /// <summary>
    /// Streamingly export an asynchronous token stream (e.g. from Gemini 3.8) to an output stream in DOCX format.
    /// </summary>
    public async Task ExportStreamAsync(
        IAsyncEnumerable<string> tokenStream,
        Stream outputStream,
        AppSettings settings,
        CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mk-streaming-export-{Guid.NewGuid():N}.docx");
        try
        {
            await ExportStreamAsync(tokenStream, tempPath, settings, ct);
            using var fs = File.OpenRead(tempPath);
            await fs.CopyToAsync(outputStream, ct);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Streamingly export an input stream of markdown to an output stream in DOCX format.
    /// </summary>
    public async Task ExportStreamAsync(
        Stream inputStream,
        Stream outputStream,
        AppSettings settings,
        CancellationToken ct = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mk-streaming-export-{Guid.NewGuid():N}.docx");
        try
        {
            await ExportStreamAsync(inputStream, tempPath, settings, ct);
            using var fs = File.OpenRead(tempPath);
            await fs.CopyToAsync(outputStream, ct);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Streamingly export an asynchronous token stream to a target DOCX file path.
    /// </summary>
    public async Task ExportStreamAsync(
        IAsyncEnumerable<string> tokenStream,
        string docxPath,
        AppSettings settings,
        CancellationToken ct = default)
    {
        var blockChannel = Channel.CreateBounded<MarkdownBlockChunk>(new BoundedChannelOptions(128)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var ingestionTask = Task.Run(() => TokenStreamBlockSplitter.IngestTokenStreamAsync(tokenStream, blockChannel.Writer, ct), ct);

        await RunPipelineAsync(blockChannel.Reader, docxPath, settings, ingestionTask, ct);
    }

    /// <summary>
    /// Streamingly export an input stream of markdown to a target DOCX file path.
    /// </summary>
    public async Task ExportStreamAsync(
        Stream inputStream,
        string docxPath,
        AppSettings settings,
        CancellationToken ct = default)
    {
        var blockChannel = Channel.CreateBounded<MarkdownBlockChunk>(new BoundedChannelOptions(128)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var ingestionTask = Task.Run(() => TokenStreamBlockSplitter.IngestStreamAsync(inputStream, blockChannel.Writer, ct), ct);

        await RunPipelineAsync(blockChannel.Reader, docxPath, settings, ingestionTask, ct);
    }

    private async Task RunPipelineAsync(
        ChannelReader<MarkdownBlockChunk> blockReader,
        string docxPath,
        AppSettings settings,
        Task ingestionTask,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(docxPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var docType = docxPath.EndsWith(".dotx", StringComparison.OrdinalIgnoreCase)
            ? WordprocessingDocumentType.Template
            : WordprocessingDocumentType.Document;

        using var package = WordprocessingDocument.Create(docxPath, docType);
        package.PackageProperties.Title = "Markdown Export";
        package.PackageProperties.Creator = settings.AuthorName;
        package.PackageProperties.Subject = ExportBranding.CreatedIn;
        ExportBranding.SetCompany(package);
        package.PackageProperties.Created = DateTime.UtcNow;
        package.PackageProperties.Modified = DateTime.UtcNow;

        var main = package.AddMainDocumentPart();

        var theme = AppServices.Themes.GetOrDefault(settings.Theme);
        var isDark = theme.Name.Contains("Dark") || theme.Name is "Dracula" or "Cyberpunk" or "Obsidian" or "Monokai Pro";

        ReferenceDocumentMerger.MergedReferenceResult? refMerge = null;
        if (ReferenceDocumentMerger.IsValidReferenceDocument(settings.BrandTemplatePath))
        {
            refMerge = ReferenceDocumentMerger.MergeReference(main, settings.BrandTemplatePath, theme, settings);
        }

        var partRegistry = new ThreadSafePartRegistry();

        var ctx = new DocxExportService.Ctx
        {
            Settings = settings,
            MainPart = main,
            Numbering = main.NumberingDefinitionsPart?.Numbering ?? DocxExportService.AddNumbering(main),
            Theme = theme,
            Alerts = isDark ? DocxExportService.AlertStylesDark : DocxExportService.AlertStyles,
            LinkColor = isDark ? "6CB6FF" : "0563C1",
            NoEmoji = settings.NoEmoji,
            MermaidMode = settings.MermaidDocxMode,
            BrandFont = string.IsNullOrWhiteSpace(settings.BrandFontFamily)
                ? (refMerge?.ExtractedBrandFont ?? null)
                : settings.BrandFontFamily.Trim(),
            OversizedDiagramMode = 4,
            SmartConnectors = settings.SmartConnectors,
            AdvancedFeatures = new Dictionary<string, FeatureNode>(),
            PartRegistry = partRegistry,
            NextNumId = refMerge?.NextNumId ?? 2,
            BulletNumId = refMerge?.BulletNumId ?? 1,
            BulletAbstractNumId = refMerge?.BulletAbstractNumId ?? 0,
            OrderedAbstractNumId = refMerge?.OrderedAbstractNumId ?? 1,
        };

        if (refMerge is null || !refMerge.Applied || main.StyleDefinitionsPart is null)
        {
            DocxExportService.AddStyles(main, ctx);
        }

        var renderedChannel = Channel.CreateBounded<RenderedBlockChunk>(new BoundedChannelOptions(128)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Stage 2: Parallel Block Renderer Worker Pool
        var workerTasks = Enumerable.Range(0, _workerCount)
            .Select(_ => Task.Run(() => RenderWorkerLoopAsync(blockReader, renderedChannel.Writer, ctx, ct), ct))
            .ToArray();

        var workersCompletionTask = Task.WhenAll(workerTasks).ContinueWith(_ =>
        {
            renderedChannel.Writer.Complete();
        }, TaskScheduler.Default);

        // Stage 4: Deterministic SAX Sequencer Consumer
        var sequencerTask = Task.Run(() => RunSaxSequencerAsync(renderedChannel.Reader, main, ctx, settings, refMerge, ct), ct);

        await Task.WhenAll(ingestionTask, workersCompletionTask, sequencerTask);

        // Stage 3 Commit: Flush all staged parts & relationships into main package safely on single thread
        partRegistry.CommitAllStaged(main);

        // Finalize Settings & Auxiliary Parts
        var wantWeb = ctx.ForceWebLayout || (settings.TargetFormat != "docx"
                      && (!settings.PageBorder && settings.UnlimitedHeight
                          || !ThemeDefinition.IsLight(ctx.Theme.Background)));

        DocxExportService.AddSettings(main, ctx,
            updateFieldsOnOpen: settings.IncludeToc || ctx.HasIndex || ctx.HasFormulas,
            trackChanges: settings.TrackChanges || ctx.HasRevisions,
            webLayout: wantWeb);

        if (AppServices.License.State.Edition == Models.Edition.Trial)
            AppServices.License.ConsumeDocxExport();
    }

    private static async Task RenderWorkerLoopAsync(
        ChannelReader<MarkdownBlockChunk> reader,
        ChannelWriter<RenderedBlockChunk> writer,
        DocxExportService.Ctx ctx,
        CancellationToken ct)
    {
        var pipeline = ctx.NoEmoji ? DocxExportService.PipelineNoEmoji : DocxExportService.Pipeline;

        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var chunk))
            {
                var md = TextNormalizer.Newlines(chunk.Markdown);
                md = AdmonitionNormalizer.Apply(md);
                md = DialectNormalizer.Apply(md, ctx.Settings.DashMode);
                md = DiagramFenceSniffer.Apply(md);

                var docId = AdvancedFeaturePipeline.ContentBasedDocumentId(md);
                var featureNodes = AdvancedFeaturePipeline.Shared.Process(md, docId);

                if (featureNodes.Count > 0)
                {
                    var marked = new StringBuilder(md.Length + featureNodes.Count * 48);
                    int cursor = 0;
                    foreach (var node in featureNodes.OrderBy(n => n.Block.Start))
                    {
                        marked.Append(md, cursor, node.Block.Start - cursor);
                        marked.Append("\n\n<!-- MARKSMITH_FEATURE:").Append(node.StableId).Append(" -->\n\n");
                        cursor = node.Block.End;
                    }
                    marked.Append(md, cursor, md.Length - cursor);
                    md = marked.ToString();

                    foreach (var node in featureNodes)
                    {
                        ctx.AdvancedFeatures[node.StableId] = node;
                    }
                }

                if (ctx.Settings.NoEmoji) md = EmojiStripper.Strip(md);
                md = DashReplacer.Apply(md, ctx.Settings.DashMode, ctx.Settings.DashCustom);
                md = FormattingService.Apply(md, ctx.Settings);

                var doc = Markdown.Parse(md, pipeline);

                var blockContainer = new W.Body();
                foreach (var block in doc)
                {
                    DocxExportService.RenderBlock(block, blockContainer, ctx, listLevel: -1);
                }

                var elements = blockContainer.ChildElements.Select(e => (OpenXmlElement)e.CloneNode(true)).ToList();

                var renderedChunk = new RenderedBlockChunk
                {
                    SequenceIndex = chunk.SequenceIndex,
                    Elements = elements,
                    IsLast = chunk.IsLast
                };

                await writer.WriteAsync(renderedChunk, ct);
            }
        }
    }

    private static async Task RunSaxSequencerAsync(
        ChannelReader<RenderedBlockChunk> reader,
        MainDocumentPart main,
        DocxExportService.Ctx ctx,
        AppSettings settings,
        ReferenceDocumentMerger.MergedReferenceResult? refMerge,
        CancellationToken ct)
    {
        using var writer = OpenXmlWriter.Create(main);

        var rootAttributes = new List<OpenXmlAttribute>
        {
            new("mc", "Ignorable", "http://schemas.openxmlformats.org/markup-compatibility/2006", "w14 w15")
        };

        var rootNamespaces = new List<KeyValuePair<string, string>>
        {
            new("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main"),
            new("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"),
            new("m", "http://schemas.openxmlformats.org/officeDocument/2006/math"),
            new("w14", "http://schemas.microsoft.com/office/word/2010/wordml"),
            new("w15", "http://schemas.microsoft.com/office/word/2012/wordml"),
            new("a", "http://schemas.openxmlformats.org/drawingml/2006/main"),
            new("wp", "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"),
            new("pic", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
            new("c", "http://schemas.openxmlformats.org/drawingml/2006/chart"),
            new("dgm", "http://schemas.openxmlformats.org/drawingml/2006/diagram"),
            new("wpg", "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"),
            new("wps", "http://schemas.microsoft.com/office/word/2010/wordprocessingShape"),
            new("mc", "http://schemas.openxmlformats.org/markup-compatibility/2006")
        };

        writer.WriteStartElement(new W.Document(), rootAttributes, rootNamespaces);
        writer.WriteElement(new W.DocumentBackground { Color = DocxExportService.Hex(ctx.Theme.Background) });
        writer.WriteStartElement(new W.Body());

        string title = "Markdown Export";

        if (settings.BrandCoverPage)
        {
            var coverContainer = new W.Body();
            DocxExportService.AppendCoverPage(coverContainer, ctx, settings, title);
            foreach (var el in coverContainer.ChildElements)
                writer.WriteElement(el);
        }

        if (settings.IncludeToc)
        {
            var tocContainer = new W.Body();
            DocxExportService.AppendTocField(tocContainer, ctx);
            foreach (var el in tocContainer.ChildElements)
                writer.WriteElement(el);
        }

        int expectedSeq = 0;
        var pendingChunks = new ConcurrentDictionary<int, RenderedBlockChunk>();

        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var chunk))
            {
                pendingChunks[chunk.SequenceIndex] = chunk;

                while (pendingChunks.TryRemove(expectedSeq, out var ready))
                {
                    foreach (var el in ready.Elements)
                    {
                        writer.WriteElement(el);
                    }
                    expectedSeq++;
                }
            }
        }

        // Write any trailing chunks
        while (pendingChunks.TryRemove(expectedSeq, out var ready))
        {
            foreach (var el in ready.Elements)
            {
                writer.WriteElement(el);
            }
            expectedSeq++;
        }

        var sectPr = DocxExportService.BuildSectionProperties(main, ctx, settings, title, refMerge);
        writer.WriteElement(sectPr);

        writer.WriteEndElement(); // </w:body>
        writer.WriteEndElement(); // </w:document>
    }
}
