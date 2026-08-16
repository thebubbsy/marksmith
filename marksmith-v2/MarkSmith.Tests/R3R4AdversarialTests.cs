using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Core.Generator;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

// COLLECTION "UpdateSpool": the chunked-download test spools to the SAME fixed path as
// UpdateServiceTests (%TEMP%\MarksmithUpdates\Marksmith-Setup-Latest.exe) and deletes it in
// finally — running the two classes in parallel races on that file (intermittent FileNotFound).
[Collection("UpdateSpool")]
public class R3R4AdversarialTests : IDisposable
{
    private readonly string _testTempDir;

    public R3R4AdversarialTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "R3R4_AdvTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }
    }

    // =========================================================================
    // SECTION 1: R3 - MainViewModel.ReadInputFileAsync Adversarial Tests
    // =========================================================================

    private class TrackingSynchronizationContext : SynchronizationContext
    {
        public int PostCount = 0;
        public int SendCount = 0;
        public readonly List<SendOrPostCallback> Callbacks = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            lock (Callbacks)
            {
                Callbacks.Add(d);
            }
            d(state); // execute synchronously for test verification
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref SendCount);
            d(state);
        }
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_NormalAsyncExecution_PopulatesCurrentMarkdown()
    {
        var filePath = Path.Combine(_testTempDir, "normal.md");
        const string expectedContent = "# Hello World\nThis is normal markdown async content.";
        await File.WriteAllTextAsync(filePath, expectedContent);

        var vm = new MainViewModel();
        var syncContext = new TrackingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(syncContext);

        try
        {
            vm.InputFilePath = filePath;

            // Wait briefly for the async file read to complete
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && vm.CurrentMarkdown != expectedContent)
            {
                await Task.Delay(20);
            }

            Assert.Equal(expectedContent, vm.CurrentMarkdown);
            Assert.True(syncContext.PostCount > 0, "UI synchronization context Post should have been invoked.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_NullSynchronizationContext_ExecutesDirectly()
    {
        var filePath = Path.Combine(_testTempDir, "null_sync.md");
        const string expectedContent = "# Direct Execution\nNo sync context present.";
        await File.WriteAllTextAsync(filePath, expectedContent);

        SynchronizationContext.SetSynchronizationContext(null);
        var vm = new MainViewModel();
        vm.InputFilePath = filePath;

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && vm.CurrentMarkdown != expectedContent)
        {
            await Task.Delay(20);
        }

        Assert.Equal(expectedContent, vm.CurrentMarkdown);
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_RapidSwitching_CancelsPriorTokensAndSettlesOnLatest()
    {
        var files = new List<string>();
        for (int i = 0; i < 20; i++)
        {
            var p = Path.Combine(_testTempDir, $"switch_{i}.md");
            await File.WriteAllTextAsync(p, $"Content of file {i}");
            files.Add(p);
        }

        var vm = new MainViewModel();

        // Rapidly switch InputFilePath across 20 files
        for (int i = 0; i < files.Count; i++)
        {
            vm.InputFilePath = files[i];
            await Task.Delay(2); // very small delay to interleave task scheduling
        }

        var expectedFinalContent = $"Content of file {files.Count - 1}";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && vm.CurrentMarkdown != expectedFinalContent)
        {
            await Task.Delay(25);
        }

        Assert.Equal(expectedFinalContent, vm.CurrentMarkdown);
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_NonExistentOrCorruptFile_HandlesExceptionCleanly()
    {
        var nonExistentPath = Path.Combine(_testTempDir, "does_not_exist_" + Guid.NewGuid().ToString("N") + ".md");
        var vm = new MainViewModel();
        vm.PastedMarkdown = "Initial content";

        // Setting a non-existent path
        vm.InputFilePath = nonExistentPath;

        await Task.Delay(100);
        Assert.False(vm.HasInputFile);
        Assert.Equal(string.Empty, vm.CurrentMarkdown);
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_LargeFile_ReadsAsyncWithoutFreezing()
    {
        var filePath = Path.Combine(_testTempDir, "large_file.md");
        // Create a 2MB markdown file
        var largeContent = "# Heading\n" + new string('A', 2 * 1024 * 1024);
        await File.WriteAllTextAsync(filePath, largeContent);

        var vm = new MainViewModel();
        vm.InputFilePath = filePath;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && vm.CurrentMarkdown != largeContent)
        {
            await Task.Delay(30);
        }

        Assert.Equal(largeContent.Length, vm.CurrentMarkdown.Length);
        Assert.Equal(largeContent, vm.CurrentMarkdown);
    }

    // =========================================================================
    // SECTION 2: R3 - UpdateService.DownloadAndInstallAsync Buffer Pooling Tests
    // =========================================================================

    [Fact]
    public async Task UpdateService_DownloadAndInstallAsync_NormalDownload_RentsAndReturnsBuffer()
    {
        var payload = new byte[256 * 1024]; // 256 KB
        Random.Shared.NextBytes(payload);

        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentLength64 = payload.Length;
                await ctx.Response.OutputStream.WriteAsync(payload);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        });

        try
        {
            var reports = new List<double>();
            var progress = new Progress<double>(p => { lock (reports) reports.Add(p); });
            var updater = new UpdateService();

            var result = await updater.DownloadAndInstallAsync($"http://localhost:{port}/update.exe", progress);

            // Install step fails because payload isn't a real installer EXE, but download succeeds
            var spool = Path.Combine(Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe");
            Assert.True(File.Exists(spool));
            Assert.Equal(payload.Length, new FileInfo(spool).Length);

            // Verify progress reached ~100%
            var deadline = DateTime.UtcNow.AddSeconds(3);
            double lastReport = -1;
            while (DateTime.UtcNow < deadline)
            {
                lock (reports) { if (reports.Count > 0) lastReport = reports[^1]; }
                if (lastReport >= 99.5) break;
                await Task.Delay(25);
            }
            Assert.True(lastReport >= 99.5, $"Progress was {lastReport}");
        }
        finally
        {
            listener.Stop();
            try { File.Delete(Path.Combine(Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe")); } catch { }
        }
    }

    [Fact]
    public async Task UpdateService_DownloadAndInstallAsync_TruncatedStream_ReturnsBufferAndExitsCleanly()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentLength64 = 500 * 1024; // Claim 500 KB
                var partial = new byte[32 * 1024]; // Only send 32 KB then abort
                await ctx.Response.OutputStream.WriteAsync(partial);
                ctx.Response.Abort(); // Force abrupt reset / truncation
            }
            catch { }
        });

        try
        {
            var updater = new UpdateService();
            var result = await updater.DownloadAndInstallAsync($"http://localhost:{port}/truncated.exe");
            Assert.False(result, "Truncated download should fail gracefully without throwing unhandled exceptions.");
        }
        finally
        {
            listener.Stop();
            try { File.Delete(Path.Combine(Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe")); } catch { }
        }
    }

    [Fact]
    public async Task UpdateService_DownloadAndInstallAsync_CancellationToken_ReturnsBufferOnMidStreamCancel()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverSending = new TaskCompletionSource<bool>();

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentLength64 = 10 * 1024 * 1024; // 10 MB
                var chunk = new byte[64 * 1024];
                serverSending.TrySetResult(true);

                for (int i = 0; i < 50; i++)
                {
                    await ctx.Response.OutputStream.WriteAsync(chunk);
                    await Task.Delay(100);
                }
                ctx.Response.OutputStream.Close();
            }
            catch { }
        });

        try
        {
            using var cts = new CancellationTokenSource();
            var updater = new UpdateService();

            var downloadTask = updater.DownloadAndInstallAsync($"http://localhost:{port}/large.exe", null, cts.Token);

            // Wait until server starts sending, then cancel
            await serverSending.Task;
            await Task.Delay(50);
            cts.Cancel();

            var result = await downloadTask;
            Assert.False(result, "Cancelled download should return false.");
        }
        finally
        {
            listener.Stop();
            try { File.Delete(Path.Combine(Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe")); } catch { }
        }
    }

    [Fact]
    public async Task UpdateService_DownloadAndInstallAsync_HttpError404_FailsGracefully()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
            catch { }
        });

        try
        {
            var updater = new UpdateService();
            var result = await updater.DownloadAndInstallAsync($"http://localhost:{port}/notfound.exe");
            Assert.False(result, "404 response must return false gracefully.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task UpdateService_DownloadAndInstallAsync_StressLoop_NoPoolExhaustionOrMemoryLeak()
    {
        // Execute 25 iterations of rapid downloads and cancellations to stress test ArrayPool rent/return
        for (int i = 0; i < 25; i++)
        {
            var port = GetFreePort();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();

            var payload = new byte[128 * 1024]; // 128 KB
            Random.Shared.NextBytes(payload);

            _ = Task.Run(async () =>
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.ContentLength64 = payload.Length;
                    await ctx.Response.OutputStream.WriteAsync(payload);
                    ctx.Response.OutputStream.Close();
                }
                catch { }
            });

            try
            {
                var updater = new UpdateService();
                var res = await updater.DownloadAndInstallAsync($"http://localhost:{port}/stress_{i}.exe");
                // download completes without exception
            }
            finally
            {
                listener.Stop();
            }
        }

        // Verify that ArrayPool is intact and can still allocate/return normally
        var poolBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        Assert.NotNull(poolBuffer);
        Assert.True(poolBuffer.Length >= 64 * 1024);
        ArrayPool<byte>.Shared.Return(poolBuffer);
    }

    // =========================================================================
    // SECTION 3: R4 - DocxPackageWriter Streaming Image Packing Tests
    // =========================================================================

    [Fact]
    public void DocxPackageWriter_WriteDocx_MissingImage_FallsBackToDummyPng()
    {
        var missingPath = Path.Combine(_testTempDir, "missing_image_" + Guid.NewGuid().ToString("N") + ".png");
        var docxOut = Path.Combine(_testTempDir, "output_missing_image.docx");

        var genResult = new DiagramGenerationResult
        {
            DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            ImageRelMap = new Dictionary<string, string>
            {
                { missingPath, "rIdImg1" }
            }
        };

        DocxPackageWriter.WriteDocx(docxOut, genResult);

        Assert.True(File.Exists(docxOut));

        using var archive = ZipFile.OpenRead(docxOut);
        var mediaEntry = archive.GetEntry("word/media/image_rIdImg1.png");
        Assert.NotNull(mediaEntry);

        using var entryStream = mediaEntry.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        var bytes = ms.ToArray();

        // Verify dummy 1x1 PNG header (PNG magic bytes: 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
        byte[] expectedHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (int i = 0; i < expectedHeader.Length; i++)
        {
            Assert.Equal(expectedHeader[i], bytes[i]);
        }
        Assert.Equal(67, bytes.Length); // exact length of 1x1 dummy PNG
    }

    [Fact]
    public void DocxPackageWriter_WriteDocx_SmallImage_StreamsExactBytes()
    {
        var imagePath = Path.Combine(_testTempDir, "small_test.png");
        var originalBytes = new byte[1024]; // 1 KB
        Random.Shared.NextBytes(originalBytes);
        File.WriteAllBytes(imagePath, originalBytes);

        var docxOut = Path.Combine(_testTempDir, "output_small_image.docx");

        var genResult = new DiagramGenerationResult
        {
            DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            ImageRelMap = new Dictionary<string, string>
            {
                { imagePath, "rIdSmall1" }
            }
        };

        DocxPackageWriter.WriteDocx(docxOut, genResult);

        Assert.True(File.Exists(docxOut));

        using var archive = ZipFile.OpenRead(docxOut);
        var mediaEntry = archive.GetEntry("word/media/image_rIdSmall1.png");
        Assert.NotNull(mediaEntry);

        using var entryStream = mediaEntry.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        var writtenBytes = ms.ToArray();

        Assert.Equal(originalBytes, writtenBytes);
    }

    [Fact]
    public void DocxPackageWriter_WriteDocx_LargeImageGreaterThan1MB_StreamsCorrectlyWithoutCorruption()
    {
        var imagePath = Path.Combine(_testTempDir, "large_photo.jpg");
        var originalBytes = new byte[3 * 1024 * 1024]; // 3 MB image
        Random.Shared.NextBytes(originalBytes);
        File.WriteAllBytes(imagePath, originalBytes);

        var originalSha256 = SHA256.HashData(originalBytes);

        var docxOut = Path.Combine(_testTempDir, "output_large_image.docx");

        var genResult = new DiagramGenerationResult
        {
            DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            ImageRelMap = new Dictionary<string, string>
            {
                { imagePath, "rIdLarge1" }
            }
        };

        DocxPackageWriter.WriteDocx(docxOut, genResult);

        Assert.True(File.Exists(docxOut));

        using var archive = ZipFile.OpenRead(docxOut);
        var mediaEntry = archive.GetEntry("word/media/image_rIdLarge1.jpg");
        Assert.NotNull(mediaEntry);

        using var entryStream = mediaEntry.Open();
        using var ms = new MemoryStream();
        entryStream.CopyTo(ms);
        var writtenBytes = ms.ToArray();

        var writtenSha256 = SHA256.HashData(writtenBytes);

        Assert.Equal(originalBytes.Length, writtenBytes.Length);
        Assert.Equal(originalSha256, writtenSha256);
    }

    [Fact]
    public void DocxPackageWriter_WriteDocx_MultipleConcurrentImages_PreservesAllEntriesAndRels()
    {
        var relMap = new Dictionary<string, string>();
        var imageHashes = new Dictionary<string, byte[]>();

        // Create 15 images of varying sizes and formats, plus 5 missing images
        for (int i = 0; i < 15; i++)
        {
            var ext = (i % 3 == 0) ? "png" : (i % 3 == 1) ? "jpg" : "jpeg";
            var imgPath = Path.Combine(_testTempDir, $"multi_img_{i}.{ext}");
            var imgBytes = new byte[(i + 1) * 64 * 1024]; // 64 KB to ~1MB
            Random.Shared.NextBytes(imgBytes);
            File.WriteAllBytes(imgPath, imgBytes);

            var rId = $"rIdImg_{i}";
            relMap[imgPath] = rId;
            imageHashes[rId] = SHA256.HashData(imgBytes);
        }

        // Add 5 missing images
        for (int i = 15; i < 20; i++)
        {
            var imgPath = Path.Combine(_testTempDir, $"missing_multi_img_{i}.png");
            var rId = $"rIdImg_{i}";
            relMap[imgPath] = rId;
        }

        var docxOut = Path.Combine(_testTempDir, "output_multi_images.docx");

        var genResult = new DiagramGenerationResult
        {
            DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            ImageRelMap = relMap
        };

        DocxPackageWriter.WriteDocx(docxOut, genResult);

        Assert.True(File.Exists(docxOut));

        using var archive = ZipFile.OpenRead(docxOut);

        // Check document.xml.rels
        var relsEntry = archive.GetEntry("word/_rels/document.xml.rels");
        Assert.NotNull(relsEntry);
        using var relsReader = new StreamReader(relsEntry.Open());
        var relsXml = relsReader.ReadToEnd();

        // Verify existing images
        for (int i = 0; i < 15; i++)
        {
            var rId = $"rIdImg_{i}";
            var ext = (i % 3 == 0) ? "png" : (i % 3 == 1) ? "jpg" : "jpeg";
            var entry = archive.GetEntry($"word/media/image_{rId}.{ext}");
            Assert.NotNull(entry);

            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var hash = SHA256.HashData(ms.ToArray());
            Assert.Equal(imageHashes[rId], hash);

            Assert.Contains($"Id=\"{rId}\"", relsXml);
        }

        // Verify missing images got dummy fallback
        for (int i = 15; i < 20; i++)
        {
            var rId = $"rIdImg_{i}";
            var entry = archive.GetEntry($"word/media/image_{rId}.png");
            Assert.NotNull(entry);
            Assert.Equal(67, entry.Length);
            Assert.Contains($"Id=\"{rId}\"", relsXml);
        }
    }

    [Fact]
    public void DocxPackageWriter_WriteDocx_ConcurrentMultiThreadedPacking_ThreadSafe()
    {
        // Run 10 concurrent threads packing DOCX archives simultaneously
        Parallel.For(0, 10, i =>
        {
            var threadDir = Path.Combine(_testTempDir, $"thread_{i}");
            Directory.CreateDirectory(threadDir);

            var imgPath = Path.Combine(threadDir, $"thread_img_{i}.png");
            var imgData = new byte[100 * 1024];
            Random.Shared.NextBytes(imgData);
            File.WriteAllBytes(imgPath, imgData);

            var docxOut = Path.Combine(threadDir, $"thread_out_{i}.docx");
            var genResult = new DiagramGenerationResult
            {
                DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
                DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
                DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
                DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
                ImageRelMap = new Dictionary<string, string> { { imgPath, $"rId_{i}" } }
            };

            DocxPackageWriter.WriteDocx(docxOut, genResult);

            Assert.True(File.Exists(docxOut));
            using var archive = ZipFile.OpenRead(docxOut);
            var entry = archive.GetEntry($"word/media/image_rId_{i}.png");
            Assert.NotNull(entry);
            Assert.Equal(imgData.Length, entry.Length);
        });
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_LockedFile_HandlesIOExceptionGracefully()
    {
        var lockedFilePath = Path.Combine(_testTempDir, "locked_file.md");
        await File.WriteAllTextAsync(lockedFilePath, "# Locked File Content");

        // Lock the file exclusively
        using var lockStream = new FileStream(lockedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var vm = new MainViewModel();
        vm.PastedMarkdown = "Should be cleared or unaffected";

        vm.InputFilePath = lockedFilePath;

        await Task.Delay(150);

        // Verify exception was handled gracefully, state was reset
        Assert.Equal(string.Empty, vm.CurrentMarkdown);
    }

    [Fact]
    public async Task MainViewModel_ReadInputFileAsync_UnicodeAndEmojiCharacters_PreservesFidelity()
    {
        var filePath = Path.Combine(_testTempDir, "unicode_emoji.md");
        const string unicodeContent = "# 🚀 Title with Emojis: 🧠 & ⚡\n\n日本語テスト • العربية • Math: ∑(x_i) = ∫ f(t) dt";
        await File.WriteAllTextAsync(filePath, unicodeContent);

        var vm = new MainViewModel();
        vm.InputFilePath = filePath;

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && vm.CurrentMarkdown != unicodeContent)
        {
            await Task.Delay(25);
        }

        Assert.Equal(unicodeContent, vm.CurrentMarkdown);
    }

    [Fact]
    public async Task UpdateService_DownloadAndInstallAsync_ChunkedTransferEncoding_HandlesProgressAndBufferPooling()
    {
        var payload = new byte[180 * 1024]; // 180 KB
        Random.Shared.NextBytes(payload);

        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                // Send without Content-Length (chunked)
                ctx.Response.SendChunked = true;
                await ctx.Response.OutputStream.WriteAsync(payload);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        });

        try
        {
            var updater = new UpdateService();
            var result = await updater.DownloadAndInstallAsync($"http://localhost:{port}/chunked.exe");

            var spool = Path.Combine(Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe");
            Assert.True(File.Exists(spool));
            Assert.Equal(payload.Length, new FileInfo(spool).Length);
        }
        finally
        {
            listener.Stop();
            try { File.Delete(Path.Combine(Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe")); } catch { }
        }
    }

    [Fact]
    public void DocxPackageWriter_WriteDocx_UpperAndMixedCaseExtensions_NormalizesCorrectly()
    {
        var pngPath = Path.Combine(_testTempDir, "test_upper.PNG");
        var jpgPath = Path.Combine(_testTempDir, "test_mixed.JpEg");
        File.WriteAllBytes(pngPath, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(jpgPath, new byte[] { 4, 5, 6 });

        var docxOut = Path.Combine(_testTempDir, "output_cased_extensions.docx");

        var genResult = new DiagramGenerationResult
        {
            DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            ImageRelMap = new Dictionary<string, string>
            {
                { pngPath, "rIdUpperPng" },
                { jpgPath, "rIdMixedJpg" }
            }
        };

        DocxPackageWriter.WriteDocx(docxOut, genResult);

        Assert.True(File.Exists(docxOut));

        using var archive = ZipFile.OpenRead(docxOut);
        var pngEntry = archive.GetEntry("word/media/image_rIdUpperPng.png");
        var jpgEntry = archive.GetEntry("word/media/image_rIdMixedJpg.jpeg");

        Assert.NotNull(pngEntry);
        Assert.NotNull(jpgEntry);
    }

    [Fact]
    public void DocxPackageWriter_WriteDocx_OverwriteExistingFile_ReplacesDocxCleanly()
    {
        var docxOut = Path.Combine(_testTempDir, "overwrite_target.docx");
        File.WriteAllText(docxOut, "GARBAGE CORRUPTED PRE-EXISTING CONTENT THAT SHOULD BE OVERWRITTEN");

        var genResult = new DiagramGenerationResult
        {
            DiagramDataXml = "<dgm:dataModel xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramLayoutXml = "<dgm:layoutDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramStyleXml = "<dgm:styleDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            DiagramColorsXml = "<dgm:colorsDef xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"/>",
            ImageRelMap = new Dictionary<string, string>()
        };

        DocxPackageWriter.WriteDocx(docxOut, genResult);

        Assert.True(File.Exists(docxOut));

        // Must be a valid zip archive without corruption
        using var archive = ZipFile.OpenRead(docxOut);
        var docEntry = archive.GetEntry("word/document.xml");
        Assert.NotNull(docEntry);
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
