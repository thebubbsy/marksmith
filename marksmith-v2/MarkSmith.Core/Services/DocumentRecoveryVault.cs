using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarkSmith.Core.Services
{
    public class RecoverySnapshot
    {
        public string DocId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string FilePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Implements periodic auto-save snapshot vault to local storage
    /// to prevent data loss during unexpected app crashes or OS restarts.
    /// </summary>
    public class DocumentRecoveryVault
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
        private readonly string _vaultDirectory;

        public DocumentRecoveryVault(string? vaultDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(vaultDirectory))
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _vaultDirectory = Path.Combine(appData, "Marksmith", "recovery_vault");
            }
            else
            {
                _vaultDirectory = vaultDirectory;
            }

            EnsureDirectoryExists();
        }

        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_vaultDirectory))
            {
                Directory.CreateDirectory(_vaultDirectory);
            }
        }

        public string SaveSnapshot(string docId, string content, string title = "Untitled")
        {
            if (string.IsNullOrWhiteSpace(docId)) docId = Guid.NewGuid().ToString("N");
            if (content == null) content = string.Empty;

            EnsureDirectoryExists();

            var snapshot = new RecoverySnapshot
            {
                DocId = docId,
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                Content = content,
                Timestamp = DateTime.UtcNow
            };

            string safeId = SanitizeFileName(docId);
            string snapshotPath = Path.Combine(_vaultDirectory, $"{safeId}.json");
            snapshot.FilePath = snapshotPath;

            string json = JsonSerializer.Serialize(snapshot, JsonOpts);
            File.WriteAllText(snapshotPath, json);

            return snapshotPath;
        }

        public RecoverySnapshot? GetLatestSnapshot(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId)) return null;

            string safeId = SanitizeFileName(docId);
            string snapshotPath = Path.Combine(_vaultDirectory, $"{safeId}.json");

            if (!File.Exists(snapshotPath)) return null;

            try
            {
                string json = File.ReadAllText(snapshotPath);
                return JsonSerializer.Deserialize<RecoverySnapshot>(json, JsonOpts);
            }
            catch
            {
                return null;
            }
        }

        public List<RecoverySnapshot> GetAllSnapshots()
        {
            EnsureDirectoryExists();
            var snapshots = new List<RecoverySnapshot>();

            foreach (var filePath in Directory.GetFiles(_vaultDirectory, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(json, JsonOpts);
                    if (snapshot != null)
                    {
                        snapshots.Add(snapshot);
                    }
                }
                catch
                {
                    // Ignore corrupted snapshot files
                }
            }

            return snapshots.OrderByDescending(s => s.Timestamp).ToList();
        }

        public bool DeleteSnapshot(string docId)
        {
            if (string.IsNullOrWhiteSpace(docId)) return false;

            string safeId = SanitizeFileName(docId);
            string snapshotPath = Path.Combine(_vaultDirectory, $"{safeId}.json");

            if (File.Exists(snapshotPath))
            {
                try
                {
                    File.Delete(snapshotPath);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public int PurgeOldSnapshots(TimeSpan maxAge)
        {
            EnsureDirectoryExists();
            int purgedCount = 0;
            DateTime cutoff = DateTime.UtcNow.Subtract(maxAge);

            foreach (var filePath in Directory.GetFiles(_vaultDirectory, "*.json"))
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(filePath);
                        purgedCount++;
                    }
                }
                catch
                {
                    // Ignore individual deletion errors
                }
            }

            return purgedCount;
        }

        private string SanitizeFileName(string input)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return string.Concat(input.Select(c => invalidChars.Contains(c) ? '_' : c));
        }
    }
}
