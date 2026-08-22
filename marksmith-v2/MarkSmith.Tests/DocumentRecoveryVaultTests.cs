using System;
using System.IO;
using MarkSmith.Core.Services;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests
{
    public class DocumentRecoveryVaultTests : IDisposable
    {
        private readonly string _tempVaultDir;
        private readonly DocumentRecoveryVault _vault;

        public DocumentRecoveryVaultTests()
        {
            _tempVaultDir = Path.Combine(Path.GetTempPath(), "marksmith_test_vault_" + Guid.NewGuid().ToString("N"));
            _vault = new DocumentRecoveryVault(_tempVaultDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempVaultDir))
            {
                try { Directory.Delete(_tempVaultDir, true); } catch { }
            }
        }

        [Fact]
        public void DefaultConstructor_ResolvesUnderAppPathsConfigDir_AndSavesAtomicallyWithoutTmpFiles()
        {
            var defaultVault = new DocumentRecoveryVault();
            string docId = "default_vault_test_" + Guid.NewGuid().ToString("N");
            string content = "# Content for default vault test";
            string title = "Default Vault Test";

            string path = defaultVault.SaveSnapshot(docId, content, title);

            try
            {
                Assert.StartsWith(AppPaths.ConfigDir, path, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(path));
                Assert.False(File.Exists(path + ".tmp"));

                var snapshot = defaultVault.GetLatestSnapshot(docId);
                Assert.NotNull(snapshot);
                Assert.Equal(docId, snapshot.DocId);
                Assert.Equal(title, snapshot.Title);
                Assert.Equal(content, snapshot.Content);
            }
            finally
            {
                defaultVault.DeleteSnapshot(docId);
                Assert.False(File.Exists(path));
            }
        }

        [Fact]
        public void SaveSnapshot_CreatesSnapshotFile()
        {
            string docId = "doc_101";
            string content = "# Auto-saved content\nThis is a test snapshot.";
            string title = "Project Plan";

            string path = _vault.SaveSnapshot(docId, content, title);

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            var snapshot = _vault.GetLatestSnapshot(docId);
            Assert.NotNull(snapshot);
            Assert.Equal(docId, snapshot.DocId);
            Assert.Equal(title, snapshot.Title);
            Assert.Equal(content, snapshot.Content);
        }

        [Fact]
        public void GetLatestSnapshot_NonExistentDoc_ReturnsNull()
        {
            Assert.Null(_vault.GetLatestSnapshot("non_existent_doc_id"));
        }

        [Fact]
        public void GetAllSnapshots_ReturnsAllSavedSnapshots()
        {
            _vault.SaveSnapshot("doc1", "Content 1", "Title 1");
            _vault.SaveSnapshot("doc2", "Content 2", "Title 2");

            var snapshots = _vault.GetAllSnapshots();

            Assert.Equal(2, snapshots.Count);
        }

        [Fact]
        public void DeleteSnapshot_RemovesSnapshotFile()
        {
            string docId = "doc_to_delete";
            _vault.SaveSnapshot(docId, "Temporary draft", "Draft");

            Assert.NotNull(_vault.GetLatestSnapshot(docId));

            bool deleted = _vault.DeleteSnapshot(docId);

            Assert.True(deleted);
            Assert.Null(_vault.GetLatestSnapshot(docId));
        }

        [Fact]
        public void SaveSnapshot_SuccessiveUpdates_AtomicallyOverwrites_WithoutLeavingTmpFiles()
        {
            string docId = "successive_doc";
            string initialContent = "Initial content v1";
            string updatedContent = "Updated content v2 with expanded text";

            string path1 = _vault.SaveSnapshot(docId, initialContent, "Draft v1");
            Assert.True(File.Exists(path1));
            Assert.False(File.Exists(path1 + ".tmp"));
            Assert.Equal(initialContent, _vault.GetLatestSnapshot(docId)?.Content);

            string path2 = _vault.SaveSnapshot(docId, updatedContent, "Draft v2");
            Assert.Equal(path1, path2);
            Assert.True(File.Exists(path2));
            Assert.False(File.Exists(path2 + ".tmp"));
            Assert.Equal(updatedContent, _vault.GetLatestSnapshot(docId)?.Content);
        }

        [Fact]
        public void SaveSnapshot_ConcurrentSaves_DoNotCorruptOrLeaveTmpFiles()
        {
            int iterations = 20;
            System.Threading.Tasks.Parallel.For(0, iterations, i =>
            {
                string docId = $"concurrent_doc_{i}";
                string path = _vault.SaveSnapshot(docId, $"Content {i}", $"Title {i}");
                Assert.True(File.Exists(path));
                Assert.False(File.Exists(path + ".tmp"));
            });

            var all = _vault.GetAllSnapshots();
            Assert.True(all.Count >= iterations);
        }

        [Fact]
        public void PurgeOldSnapshots_DeletesExpiredSnapshots()
        {
            string docId = "old_doc";
            string path = _vault.SaveSnapshot(docId, "Old draft", "Old");

            // Artificially set file last write time to 10 days ago
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));

            int purged = _vault.PurgeOldSnapshots(TimeSpan.FromDays(7));

            Assert.Equal(1, purged);
            Assert.Null(_vault.GetLatestSnapshot(docId));
        }
    }
}
