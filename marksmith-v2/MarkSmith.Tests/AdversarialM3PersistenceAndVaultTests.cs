using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Core.Services;
using MarkSmith.Models.MindMap;
using MarkSmith.Services;
using MarkSmith.Services.MindMap;
using Xunit;

namespace MarkSmith.Core.Tests;

public class AdversarialM3PersistenceAndVaultTests : IDisposable
{
    private readonly string _testScratchDir;

    public AdversarialM3PersistenceAndVaultTests()
    {
        _testScratchDir = Path.Combine(Path.GetTempPath(), "m3_adversarial_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testScratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testScratchDir))
        {
            try { Directory.Delete(_testScratchDir, true); } catch { }
        }
    }

    [Fact]
    public void DocumentRecoveryVault_DefaultConstructor_IsStrictlyIsolatedUnderAppPathsConfigDir()
    {
        var vault = new DocumentRecoveryVault();
        string docId = "isolation_test_" + Guid.NewGuid().ToString("N");
        string content = "# Isolated Markdown Snapshot";
        string title = "Isolated Test";

        string snapshotPath = vault.SaveSnapshot(docId, content, title);

        try
        {
            // Must reside strictly within AppPaths.ConfigDir
            Assert.StartsWith(AppPaths.ConfigDir, snapshotPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(snapshotPath));
            Assert.False(File.Exists(snapshotPath + ".tmp"));

            // Must NOT exist in %APPDATA%\Marksmith\recovery_vault
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string leakedPath = Path.Combine(roamingAppData, "Marksmith", "recovery_vault", $"{docId}.json");
            Assert.False(File.Exists(leakedPath), $"Vault leaked snapshot into roaming APPDATA: {leakedPath}");

            // Verify retrieval
            var retrieved = vault.GetLatestSnapshot(docId);
            Assert.NotNull(retrieved);
            Assert.Equal(docId, retrieved.DocId);
            Assert.Equal(content, retrieved.Content);
            Assert.Equal(title, retrieved.Title);
        }
        finally
        {
            vault.DeleteSnapshot(docId);
            Assert.False(File.Exists(snapshotPath));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DocumentRecoveryVault_NullOrWhitespacePath_FallsBackToAppPathsConfigDir(string? emptyOrWhitespace)
    {
        var vault = new DocumentRecoveryVault(emptyOrWhitespace);
        string docId = "fallback_test_" + Guid.NewGuid().ToString("N");
        string path = vault.SaveSnapshot(docId, "Fallback content", "Fallback");

        try
        {
            Assert.StartsWith(AppPaths.ConfigDir, path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(path));
        }
        finally
        {
            vault.DeleteSnapshot(docId);
        }
    }

    [Fact]
    public void DocumentRecoveryVault_ExplicitDirectory_HonorsCustomTarget()
    {
        string customDir = Path.Combine(_testScratchDir, "custom_vault_location");
        var vault = new DocumentRecoveryVault(customDir);

        string docId = "custom_dir_test_" + Guid.NewGuid().ToString("N");
        string path = vault.SaveSnapshot(docId, "Custom content", "Custom");

        Assert.StartsWith(customDir, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.NotNull(vault.GetLatestSnapshot(docId));
    }

    [Fact]
    public void DocumentRecoveryVault_SanitizesIllegalCharactersInDocId()
    {
        string customDir = Path.Combine(_testScratchDir, "sanitize_test");
        var vault = new DocumentRecoveryVault(customDir);

        // docId contains illegal path characters: \ / : * ? " < > |
        string hostileDocId = @"doc:illegal*char?name<test>|path/with\slashes";
        string content = "Adversarial content for hostile docId";
        string title = "Hostile Title";

        string path = vault.SaveSnapshot(hostileDocId, content, title);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        // Verify retrieval using the exact original unsanitized docId
        var snapshot = vault.GetLatestSnapshot(hostileDocId);
        Assert.NotNull(snapshot);
        Assert.Equal(hostileDocId, snapshot.DocId);
        Assert.Equal(content, snapshot.Content);

        // Verify deletion using the original unsanitized docId
        bool deleted = vault.DeleteSnapshot(hostileDocId);
        Assert.True(deleted);
        Assert.False(File.Exists(path));
        Assert.Null(vault.GetLatestSnapshot(hostileDocId));
    }

    [Fact]
    public void DocumentRecoveryVault_GetAllSnapshots_IgnoresCorruptedAndZeroByteFiles()
    {
        string customDir = Path.Combine(_testScratchDir, "corrupted_vault_test");
        var vault = new DocumentRecoveryVault(customDir);

        // 1. Save a valid snapshot
        vault.SaveSnapshot("valid_doc_1", "Valid content 1", "Valid 1");
        vault.SaveSnapshot("valid_doc_2", "Valid content 2", "Valid 2");

        // 2. Inject corrupted files directly into vault directory
        string zeroByteFile = Path.Combine(customDir, "corrupt_zero_byte.json");
        File.WriteAllText(zeroByteFile, "");

        string garbageTextFile = Path.Combine(customDir, "corrupt_garbage.json");
        File.WriteAllText(garbageTextFile, "NOT_JSON_<<<>>>!@@##$$%%^^&&*(");

        string partialJsonFile = Path.Combine(customDir, "corrupt_partial.json");
        File.WriteAllText(partialJsonFile, "{\"DocId\": \"bad\", \"Content\": ");

        // 3. GetAllSnapshots should gracefully parse valid snapshots and skip corrupted ones
        var all = vault.GetAllSnapshots();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.DocId == "valid_doc_1");
        Assert.Contains(all, s => s.DocId == "valid_doc_2");
    }

    [Fact]
    public void DocumentRecoveryVault_PurgeOldSnapshots_RemovesOnlyExpiredFiles()
    {
        string customDir = Path.Combine(_testScratchDir, "purge_vault_test");
        var vault = new DocumentRecoveryVault(customDir);

        string oldDoc = "old_doc_" + Guid.NewGuid().ToString("N");
        string newDoc = "new_doc_" + Guid.NewGuid().ToString("N");

        string oldPath = vault.SaveSnapshot(oldDoc, "Old content", "Old");
        string newPath = vault.SaveSnapshot(newDoc, "New content", "New");

        // Artificially backdate old snapshot to 30 days ago
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-30));
        // Ensure new snapshot is fresh
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);

        int purged = vault.PurgeOldSnapshots(TimeSpan.FromDays(7));

        Assert.Equal(1, purged);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        Assert.Null(vault.GetLatestSnapshot(oldDoc));
        Assert.NotNull(vault.GetLatestSnapshot(newDoc));
    }

    [Fact]
    public void DocumentRecoveryVault_ConcurrentSnapshotSaves_MaintainsDataIntegrity()
    {
        string customDir = Path.Combine(_testScratchDir, "concurrent_vault_test");
        var vault = new DocumentRecoveryVault(customDir);

        int count = 40;
        Parallel.For(0, count, i =>
        {
            string id = $"thread_doc_{i:D3}";
            string body = $"Markdown content from worker thread {i} with payload: {new string('A', i * 50)}";
            string p = vault.SaveSnapshot(id, body, $"Thread Title {i}");

            Assert.True(File.Exists(p));
            Assert.False(File.Exists(p + ".tmp"));
        });

        var snapshots = vault.GetAllSnapshots();
        Assert.Equal(count, snapshots.Count);

        for (int i = 0; i < count; i++)
        {
            string id = $"thread_doc_{i:D3}";
            var s = vault.GetLatestSnapshot(id);
            Assert.NotNull(s);
            Assert.Equal(id, s.DocId);
            Assert.StartsWith($"Markdown content from worker thread {i}", s.Content);
        }
    }

    [Fact]
    public void DocumentRecoveryVault_RapidSuccessiveOverwrites_MaintainsConsistencyWithoutTmpLeaks()
    {
        string customDir = Path.Combine(_testScratchDir, "rapid_overwrites_test");
        var vault = new DocumentRecoveryVault(customDir);

        string docId = "rapid_target_doc";
        for (int step = 0; step < 30; step++)
        {
            string content = $"Revision {step} at {DateTime.UtcNow.Ticks}";
            string p = vault.SaveSnapshot(docId, content, $"Revision {step}");

            Assert.True(File.Exists(p));
            Assert.False(File.Exists(p + ".tmp"));

            var snap = vault.GetLatestSnapshot(docId);
            Assert.NotNull(snap);
            Assert.Equal(content, snap.Content);
        }

        // Verify only 1 file exists in the directory
        var jsonFiles = Directory.GetFiles(customDir, "*.json");
        Assert.Single(jsonFiles);
        var tmpFiles = Directory.GetFiles(customDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task MindMapStorageService_SaveAsync_SavesAtomicallyAndRedirectionWorks()
    {
        string defaultPath = MindMapStorageService.GetDefaultLibraryStoragePath();
        Assert.StartsWith(AppPaths.ConfigDir, defaultPath, StringComparison.OrdinalIgnoreCase);

        var service = new MindMapStorageService();
        var doc = MindMapStorageService.CreateDefaultGalaxy();
        doc.Title = "Adversarial Galaxy";

        string targetFile = Path.Combine(_testScratchDir, "galaxy.msmap");
        await service.SaveAsync(doc, targetFile);

        Assert.True(File.Exists(targetFile));
        Assert.False(File.Exists(targetFile + ".tmp"));

        var loaded = await service.LoadAsync(targetFile);
        Assert.NotNull(loaded);
        Assert.Equal("Adversarial Galaxy", loaded.Title);
        Assert.Equal(doc.Nodes.Count, loaded.Nodes.Count);
    }

    [Fact]
    public void AtomicFile_WriteAllText_CreatesParentDirectoryAutomatically()
    {
        string deeplyNested = Path.Combine(_testScratchDir, "sub1", "sub2", "sub3", "atomic_test.txt");
        Assert.False(Directory.Exists(Path.GetDirectoryName(deeplyNested)));

        AtomicFile.WriteAllText(deeplyNested, "deep content");

        Assert.True(File.Exists(deeplyNested));
        Assert.False(File.Exists(deeplyNested + ".tmp"));
        Assert.Equal("deep content", File.ReadAllText(deeplyNested));
    }
}
