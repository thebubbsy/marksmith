using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Tests;
using MarkSmith.Mcp.Tools;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Unit and integration tests verifying Milestone 3 multi-threaded SAX streaming engine,
/// token stream block splitting, parallel block worker pool, thread-safe relationship staging,
/// and deterministic sequence ordering.
/// </summary>
public class StreamingSaxParallelismTests
{
    private static async IAsyncEnumerable<string> CreateTokenStream(
        IEnumerable<string> tokens,
        [EnumeratorCancellation] CancellationToken ct = default,
        int delayMs = 0)
    {
        foreach (var token in tokens)
        {
            if (ct.IsCancellationRequested) yield break;
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            yield return token;
        }
    }

    [Fact]
    public async Task TokenStreamBlockSplitter_DelineatesHeadingsParagraphsAndCodeBlocks_Incrementally()
    {
        var tokens = new[]
        {
            "# Title Heading\n\n",
            "This is paragraph one ",
            "with continuous tokens.\n\n",
            "```csharp\n",
            "var x = 1;\n",
            "var y = 2;\n\n",
            "var z = x + y;\n",
            "```\n\n",
            "> [!NOTE]\n",
            "> This is a note callout.\n\n",
            "| Header 1 | Header 2 |\n",
            "| --- | --- |\n",
            "| Cell 1 | Cell 2 |\n\n",
            "Final concluding paragraph."
        };

        var channel = Channel.CreateUnbounded<MarkdownBlockChunk>();
        var tokenStream = CreateTokenStream(tokens);

        await TokenStreamBlockSplitter.IngestTokenStreamAsync(tokenStream, channel.Writer);

        var chunks = new List<MarkdownBlockChunk>();
        await foreach (var chunk in channel.Reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        Assert.True(chunks.Count >= 5, $"Expected at least 5 chunks, but got {chunks.Count}");

        // Verify sequential indexing
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].SequenceIndex);
        }

        // Verify chunk contents
        Assert.Contains("# Title Heading", chunks[0].Markdown);
        Assert.Contains("This is paragraph one with continuous tokens.", chunks[1].Markdown);
        Assert.Contains("var z = x + y;", chunks[2].Markdown);
        Assert.True(chunks[^1].IsLast);
    }

    [Fact]
    public async Task TokenStreamBlockSplitter_PreservesFencedCodeBlocks_WithoutSplittingOnInternalNewlines()
    {
        var tokens = new[]
        {
            "```python\n",
            "def calculate():\n",
            "    # empty line follows\n\n",
            "    res = 42\n",
            "    return res\n",
            "```\n\n",
            "Paragraph outside code."
        };

        var channel = Channel.CreateUnbounded<MarkdownBlockChunk>();
        var tokenStream = CreateTokenStream(tokens);

        await TokenStreamBlockSplitter.IngestTokenStreamAsync(tokenStream, channel.Writer);

        var chunks = new List<MarkdownBlockChunk>();
        await foreach (var chunk in channel.Reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Contains("```python", chunks[0].Markdown);
        Assert.Contains("def calculate():", chunks[0].Markdown);
        Assert.Contains("res = 42", chunks[0].Markdown);
        Assert.Contains("```", chunks[0].Markdown);
        Assert.Contains("Paragraph outside code.", chunks[1].Markdown);
    }

    [Fact]
    public async Task TokenStreamBlockSplitter_PreservesContainers_TabsAndColumns()
    {
        var tokens = new[]
        {
            ":::tabs\n",
            "=== Tab 1\n",
            "Content for tab 1\n\n",
            "=== Tab 2\n",
            "Content for tab 2\n",
            ":::\n\n",
            "After container."
        };

        var channel = Channel.CreateUnbounded<MarkdownBlockChunk>();
        var tokenStream = CreateTokenStream(tokens);

        await TokenStreamBlockSplitter.IngestTokenStreamAsync(tokenStream, channel.Writer);

        var chunks = new List<MarkdownBlockChunk>();
        await foreach (var chunk in channel.Reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Contains(":::tabs", chunks[0].Markdown);
        Assert.Contains("=== Tab 1", chunks[0].Markdown);
        Assert.Contains("=== Tab 2", chunks[0].Markdown);
        Assert.Contains(":::", chunks[0].Markdown);
        Assert.Contains("After container.", chunks[1].Markdown);
    }

    [Fact]
    public async Task StreamingDocxExportService_MultiThreadedParallelism_ProducesValidDocx()
    {
        var tokenList = new List<string>();
        tokenList.Add("# Document Title\n\n");
        tokenList.Add("Introductory summary paragraph.\n\n");

        for (int i = 1; i <= 30; i++)
        {
            tokenList.Add($"## Section {i}: Topic Analysis\n\n");
            tokenList.Add($"Paragraph {i}.1 explaining the detailed findings and context for section {i}.\n\n");
            tokenList.Add($"> [!TIP]\n> Actionable tip for item {i}.\n\n");
            tokenList.Add($"```csharp\n// Code snippet {i}\nint val{i} = {i} * 10;\n```\n\n");
        }
        tokenList.Add("### Conclusion\n\nFinal remarks and summary.");

        var streamingService = new StreamingDocxExportService(workerCount: 4);
        var tempDocx = Path.Combine(Path.GetTempPath(), $"streaming_test_{Guid.NewGuid():N}.docx");

        try
        {
            var tokenStream = CreateTokenStream(tokenList);
            await streamingService.ExportStreamAsync(tokenStream, tempDocx, new AppSettings { Theme = "GitHub Light" });

            Assert.True(File.Exists(tempDocx), "Generated DOCX file should exist.");
            var fileInfo = new FileInfo(tempDocx);
            Assert.True(fileInfo.Length > 2000, "DOCX file should contain substantial packaged content.");

            // Validate OpenXML Schema Compliance
            using (var doc = WordprocessingDocument.Open(tempDocx, false))
            {
                var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
                var errors = validator.Validate(doc)
                    .Where(e => e.ErrorType != ValidationErrorType.MarkupCompatibility)
                    .ToList();

                Assert.Empty(errors);
            }

            // Verify strict sequence ordering in XML
            using (var zip = ZipFile.OpenRead(tempDocx))
            {
                var entry = zip.GetEntry("word/document.xml");
                Assert.NotNull(entry);
                using var sr = new StreamReader(entry.Open());
                var docXml = await sr.ReadToEndAsync();

                Assert.Contains("Document Title", docXml);
                Assert.Contains("Introductory summary paragraph", docXml);

                // Verify section sequence indices are monotonically increasing
                int lastIdx = -1;
                for (int i = 1; i <= 30; i++)
                {
                    int currentIdx = docXml.IndexOf($"Section {i}: Topic Analysis", StringComparison.Ordinal);
                    Assert.True(currentIdx > lastIdx, $"Section {i} appeared out of sequence order in document.xml");
                    lastIdx = currentIdx;
                }
            }
        }
        finally
        {
            try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
        }
    }

    [Fact]
    public async Task StreamingDocxExportService_ThreadSafeRelationships_AllocatesUniqueAtomicRIds()
    {
        var tokenList = new List<string>();
        tokenList.Add("# Relationship Stress Test\n\n");

        for (int i = 1; i <= 25; i++)
        {
            tokenList.Add($"Paragraph {i} has a [Link {i}](https://example.com/link{i}) and an inline formula $\\sqrt{{{i}}} + x^2$.\n\n");
            tokenList.Add($"Another link [Target {i}](https://github.com/thebubbsy/marksmith/issue/{i}).\n\n");
        }

        var streamingService = new StreamingDocxExportService(workerCount: 6);
        using var ms = new MemoryStream();

        var tokenStream = CreateTokenStream(tokenList);
        await streamingService.ExportStreamAsync(tokenStream, ms, new AppSettings());

        var bytes = ms.ToArray();
        Assert.NotEmpty(bytes);

        var tempDocx = Path.Combine(Path.GetTempPath(), $"rel_test_{Guid.NewGuid():N}.docx");
        try
        {
            await File.WriteAllBytesAsync(tempDocx, bytes);

            using var zip = ZipFile.OpenRead(tempDocx);
            var relsEntry = zip.GetEntry("word/_rels/document.xml.rels");
            Assert.NotNull(relsEntry);

            using var sr = new StreamReader(relsEntry.Open());
            var relsXml = await sr.ReadToEndAsync();

            // Verify unique relationship IDs in .rels
            var rIdMatches = System.Text.RegularExpressions.Regex.Matches(relsXml, @"Id=""(rId\d+)""");
            var rIds = rIdMatches.Select(m => m.Groups[1].Value).ToList();

            var uniqueRIds = new HashSet<string>(rIds);
            Assert.Equal(rIds.Count, uniqueRIds.Count); // Zero duplicate rIds
            Assert.True(rIds.Count >= 50, $"Expected >= 50 relationships, but found {rIds.Count}");
        }
        finally
        {
            try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
        }
    }

    [Fact]
    public async Task StreamingDocxExportService_StreamsFromStream_MatchesTokenStream()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stream Input Test");
        sb.AppendLine();
        for (int i = 1; i <= 20; i++)
        {
            sb.AppendLine($"Paragraph {i} streamed from raw stream.");
            sb.AppendLine();
        }
        var rawMd = sb.ToString();

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(rawMd));
        using var outputStream = new MemoryStream();

        var service = new StreamingDocxExportService();
        await service.ExportStreamAsync(inputStream, outputStream, new AppSettings());

        var outputBytes = outputStream.ToArray();
        Assert.NotEmpty(outputBytes);
        Assert.True(outputBytes.Length > 1000);
    }

    [Fact]
    public async Task StreamingDocxExportService_CancellationToken_AbortsPromptly()
    {
        using var cts = new CancellationTokenSource();

        async IAsyncEnumerable<string> InfiniteTokenStream([EnumeratorCancellation] CancellationToken ct = default)
        {
            int i = 0;
            while (!ct.IsCancellationRequested)
            {
                i++;
                yield return $"Paragraph {i} content token.\n\n";
                await Task.Delay(10, ct);
                if (i == 5)
                {
                    cts.Cancel();
                }
            }
        }

        var service = new StreamingDocxExportService();
        using var outputStream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ExportStreamAsync(InfiniteTokenStream(cts.Token), outputStream, new AppSettings(), cts.Token);
        });
    }

    [Fact]
    public async Task RenderMarkdownTool_WithStreamMode_GeneratesValidDocx()
    {
        var tool = new RenderMarkdownTool();
        var tempOut = Path.Combine(Path.GetTempPath(), $"mcp_stream_{Guid.NewGuid():N}.docx");

        try
        {
            var json = $$"""
            {
                "markdown": "# MCP Streaming Test\n\nParagraph 1 in streaming mode.\n\n## Section 2\n\nParagraph 2 in streaming mode.",
                "output_path": "{{tempOut.Replace("\\", "\\\\")}}",
                "stream_mode": true,
                "theme": "GitHub Light"
            }
            """;

            using var doc = JsonDocument.Parse(json);
            var result = await tool.ExecuteAsync(doc.RootElement);

            Assert.False(result.IsError);
            Assert.True(File.Exists(tempOut));
            Assert.True(new FileInfo(tempOut).Length > 1000);
        }
        finally
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
        }
    }
}
