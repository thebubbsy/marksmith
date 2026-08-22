using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Core.Services;
using MarkSmith.Models;
using MarkSmith.Models.MindMap;
using MarkSmith.Services;
using MarkSmith.Services.MindMap;
using Xunit;

namespace MarkSmith.Tests;

public class M3PersistenceEmpiricalChallengerTests : IDisposable
{
    private readonly string _testRoot;

    public M3PersistenceEmpiricalChallengerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MarkSmith_M3_Challenger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public void DocumentRecoveryVault_MassiveConcurrentWrites_SameDocId_LeavesNoTmpAndValidJson()
    {
        string vaultDir = Path.Combine(_testRoot, "vault_same_doc");
        var vault = new DocumentRecoveryVault(vaultDir);
        string docId = "shared_doc_target";
        int concurrency = 50;

        Parallel.For(0, concurrency, i =>
        {
            vault.SaveSnapshot(docId, $"# Document Content from thread {i}\nBody payload iteration {i}", $"Title {i}");
        });

        string snapshotPath = Path.Combine(vaultDir, $"{docId}.json");
        Assert.True(File.Exists(snapshotPath), "Snapshot target file must exist.");
        Assert.False(File.Exists(snapshotPath + ".tmp"), "No dangling .tmp file should exist.");

        var tmpFiles = Directory.GetFiles(vaultDir, "*.tmp");
        Assert.Empty(tmpFiles);

        var latest = vault.GetLatestSnapshot(docId);
        Assert.NotNull(latest);
        Assert.Equal(docId, latest.DocId);
        Assert.StartsWith("# Document Content from thread", latest.Content);
    }

    [Fact]
    public void DocumentRecoveryVault_MassiveConcurrentWrites_DifferentDocIds_AllPersistedAndNoTmpFiles()
    {
        string vaultDir = Path.Combine(_testRoot, "vault_diff_docs");
        var vault = new DocumentRecoveryVault(vaultDir);
        int concurrency = 80;

        Parallel.For(0, concurrency, i =>
        {
            string docId = $"doc_item_{i:D4}";
            vault.SaveSnapshot(docId, $"Content for document {i}", $"Title {i}");
        });

        var tmpFiles = Directory.GetFiles(vaultDir, "*.tmp");
        Assert.Empty(tmpFiles);

        var allSnapshots = vault.GetAllSnapshots();
        Assert.Equal(concurrency, allSnapshots.Count);

        for (int i = 0; i < concurrency; i++)
        {
            string docId = $"doc_item_{i:D4}";
            var snapshot = vault.GetLatestSnapshot(docId);
            Assert.NotNull(snapshot);
            Assert.Equal($"Content for document {i}", snapshot.Content);
        }
    }

    [Fact]
    public void DocumentRecoveryVault_AdversarialDocIds_WithInvalidChars_SanitizesAndSavesAtomically()
    {
        string vaultDir = Path.Combine(_testRoot, "vault_adversarial_ids");
        var vault = new DocumentRecoveryVault(vaultDir);

        var adversarialIds = new[]
        {
            "doc/with/slashes",
            @"doc\with\backslashes",
            "doc:with:colons",
            "doc*with*asterisks",
            "doc?with?questions",
            "doc\"with\"quotes",
            "doc<with>brackets",
            "doc|with|pipes",
            "doc with spaces & unicode 🚀 漢字",
            "",
            "   "
        };

        foreach (var id in adversarialIds)
        {
            string path = vault.SaveSnapshot(id, $"Content for {id}", $"Title for {id}");
            Assert.True(File.Exists(path), $"Snapshot file should exist for ID: '{id}'");
            Assert.False(File.Exists(path + ".tmp"), $"Tmp file must not exist for ID: '{id}'");
        }

        var tmpFiles = Directory.GetFiles(vaultDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void DocumentRecoveryVault_SequentialOverwrites_100Times_MaintainsIntegrityAndZeroTmp()
    {
        string vaultDir = Path.Combine(_testRoot, "vault_sequential");
        var vault = new DocumentRecoveryVault(vaultDir);
        string docId = "sequential_overwrite_doc";

        for (int i = 1; i <= 100; i++)
        {
            string path = vault.SaveSnapshot(docId, $"Version {i} of text", $"Version {i}");
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        var tmpFiles = Directory.GetFiles(vaultDir, "*.tmp");
        Assert.Empty(tmpFiles);

        var latest = vault.GetLatestSnapshot(docId);
        Assert.NotNull(latest);
        Assert.Equal("Version 100 of text", latest.Content);
        Assert.Equal("Version 100", latest.Title);
    }

    [Fact]
    public void DocumentRecoveryVault_DefaultConstructor_ResolvesUnderAppPathsConfigDir()
    {
        var vault = new DocumentRecoveryVault();
        string docId = "default_ctor_test_" + Guid.NewGuid().ToString("N");
        string path = vault.SaveSnapshot(docId, "Sample Content", "Sample Title");

        try
        {
            Assert.StartsWith(AppPaths.ConfigDir, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("Sample Content", vault.GetLatestSnapshot(docId)?.Content);
        }
        finally
        {
            vault.DeleteSnapshot(docId);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void CustomThemeStore_RapidConcurrentMutations_DoesNotCorruptStoreOrLeaveTmp()
    {
        int concurrency = 30;
        var themeNames = new List<string>();

        for (int i = 0; i < concurrency; i++)
        {
            themeNames.Add($"Challenger Theme {i} " + Guid.NewGuid().ToString("N"));
        }

        try
        {
            Parallel.For(0, concurrency, i =>
            {
                var theme = new ThemeDefinition(
                    themeNames[i],
                    $"#FF{i:X2}00", "#000000", "#112233", "#F8F8F8", "#E0E0E0", "#0071C1", "#F0F0F0", "#CCCCCC");
                CustomThemeStore.AddOrUpdate(theme);
            });

            string storePath = Path.Combine(AppPaths.ConfigDir, "custom-themes.json");
            Assert.True(File.Exists(storePath));
            Assert.False(File.Exists(storePath + ".tmp"));

            var allThemes = CustomThemeStore.All;
            Assert.NotNull(allThemes);

            foreach (var name in themeNames)
            {
                Assert.Contains(allThemes, t => t.Name == name);
            }
        }
        finally
        {
            foreach (var name in themeNames)
            {
                CustomThemeStore.Remove(name);
            }
        }
    }

    [Fact]
    public async Task MindMapStorageService_MassiveConcurrentSaves_SameFile_LoadsValidDocument()
    {
        var service = new MindMapStorageService();
        string mapFile = Path.Combine(_testRoot, "concurrent_shared.msmap");
        int concurrency = 30;

        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(async () =>
        {
            var doc = MindMapStorageService.CreateDefaultGalaxy();
            doc.Title = $"Galaxy Revision {i}";
            doc.Nodes.Add(new MindMapNode
            {
                Title = $"Dynamic Node {i}",
                NodeType = MindMapNodeType.Document,
                X = i * 10,
                Y = i * 10
            });
            await service.SaveAsync(doc, mapFile);
        }));

        await Task.WhenAll(tasks);

        Assert.True(File.Exists(mapFile));
        Assert.False(File.Exists(mapFile + ".tmp"));

        var loaded = await service.LoadAsync(mapFile);
        Assert.NotNull(loaded);
        Assert.StartsWith("Galaxy Revision", loaded.Title);
        Assert.NotEmpty(loaded.Nodes);
        Assert.NotEmpty(loaded.Links);
    }

    [Fact]
    public async Task MindMapStorageService_MassiveConcurrentSaves_DistinctFiles_AllRoundtripCleanly()
    {
        var service = new MindMapStorageService();
        string mapsDir = Path.Combine(_testRoot, "distinct_maps");
        Directory.CreateDirectory(mapsDir);
        int count = 40;

        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            string filePath = Path.Combine(mapsDir, $"galaxy_{i:D3}.msmap");
            var doc = MindMapStorageService.CreateDefaultGalaxy();
            doc.Title = $"Galaxy {i}";
            await service.SaveAsync(doc, filePath);
        }));

        await Task.WhenAll(tasks);

        var tmpFiles = Directory.GetFiles(mapsDir, "*.tmp");
        Assert.Empty(tmpFiles);

        for (int i = 0; i < count; i++)
        {
            string filePath = Path.Combine(mapsDir, $"galaxy_{i:D3}.msmap");
            Assert.True(File.Exists(filePath));
            var loaded = await service.LoadAsync(filePath);
            Assert.NotNull(loaded);
            Assert.Equal($"Galaxy {i}", loaded.Title);
        }
    }

    [Fact]
    public void MindMapStorageService_GetDefaultLibraryStoragePath_ResolvesUnderAppPathsConfigDir()
    {
        string defaultPath = MindMapStorageService.GetDefaultLibraryStoragePath();
        Assert.StartsWith(AppPaths.ConfigDir, defaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("library.msmap", defaultPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtomicFile_DirectStressTest_VariousPayloads_LeavesNoTmpFiles()
    {
        string atomicDir = Path.Combine(_testRoot, "atomic_direct");
        Directory.CreateDirectory(atomicDir);

        // Empty string
        string emptyPath = Path.Combine(atomicDir, "empty.txt");
        AtomicFile.WriteAllText(emptyPath, string.Empty);
        Assert.True(File.Exists(emptyPath));
        Assert.False(File.Exists(emptyPath + ".tmp"));
        Assert.Equal(string.Empty, File.ReadAllText(emptyPath));

        // Unicode and emoji payload
        string unicodePath = Path.Combine(atomicDir, "unicode.txt");
        string unicodeContent = "🚀 Testing UTF-8: 漢字, русский, العربية, \uD83D\uDE00, \n\r\t \u200B special formatting";
        AtomicFile.WriteAllText(unicodePath, unicodeContent);
        Assert.True(File.Exists(unicodePath));
        Assert.False(File.Exists(unicodePath + ".tmp"));
        Assert.Equal(unicodeContent, File.ReadAllText(unicodePath));

        // Large payload (2MB)
        string largePath = Path.Combine(atomicDir, "large.txt");
        string largeContent = new string('A', 2 * 1024 * 1024);
        AtomicFile.WriteAllText(largePath, largeContent);
        Assert.True(File.Exists(largePath));
        Assert.False(File.Exists(largePath + ".tmp"));
        Assert.Equal(largeContent.Length, new FileInfo(largePath).Length);
    }
}
