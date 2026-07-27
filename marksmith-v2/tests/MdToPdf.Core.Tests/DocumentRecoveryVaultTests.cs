using System;
using System.IO;
using MdToPdf.Core.Services;
using Xunit;

namespace MdToPdf.Core.Tests
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
        public void SaveSnapshot_CreatesSnapshotFile()
        {
            string docId = "doc_101";
            string content = "# Auto-saved content\nThis is a test snapshot.";
            string title = "Project Plan";

            string path = _vault.SaveSnapshot(docId, content, title);

            Assert.True(File.Exists(path));
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
