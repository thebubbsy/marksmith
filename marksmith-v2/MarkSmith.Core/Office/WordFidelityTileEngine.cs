using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.Core.Office
{
    /// <summary>
    /// Incremental Word-exact rendering: the document is rasterized ONCE as a stack of page-band
    /// PNGs (tiles) through the persistent marksmith-office host. On later edits we diff the
    /// markdown, map the changed lines to the page bands they land on, and re-render ONLY those
    /// bands — Word stays open across renders, so a single band refresh is cheap and the rest of
    /// the page grid stays stable (no full-page flicker, no Word cold start per keystroke).
    /// </summary>
    public sealed class WordFidelityTileEngine : IDisposable
    {
        // 2x letter-width (~150 DPI) — vector-crisp text and real page furniture (headers,
        // footers, borders) via Word's own PDF export + the OS PDF rasterizer.
        private const double TileScale = 2.0;

        // When provided, the app builds the docx through its REAL export pipeline (mermaid
        // harvest, smartart geometry, AI-cleanup fixes) so the preview matches the shipped file.
        private readonly Func<string, string, Task<bool>>? _docxBuilder;

        private readonly string _tempDir;
        private string _tempDocxPath;
        private string _tempPdfPath;
        private WordTileServer? _server;
        private string? _lastMarkdown;
        private byte[][]? _tiles;
        private bool _disposed;

        public WordFidelityTileEngine(Func<string, string, Task<bool>>? docxBuilder = null)
        {
            _docxBuilder = docxBuilder;
            _tempDir = Path.Combine(Path.GetTempPath(), "MarkSmithFidelity");
            Directory.CreateDirectory(_tempDir);
            _tempDocxPath = Path.Combine(_tempDir, $"fidelity_{Guid.NewGuid():N}.docx");
            _tempPdfPath = Path.Combine(_tempDir, $"fidelity_{Guid.NewGuid():N}.pdf");
        }

        private async Task<bool> BuildDocxAsync(string markdown, AppSettings settings, string targetPath)
        {
            if (_docxBuilder != null) return await _docxBuilder(markdown, targetPath).ConfigureAwait(false);
            await new DocxExportService().ExportAsync(markdown, targetPath, settings).ConfigureAwait(false);
            return true;
        }

        public int PageCount => _server?.PageCount ?? 0;

        public IReadOnlyList<byte[]>? Tiles => _tiles;

        public bool HasServer => _server != null;

        /// <summary>First render: build the real docx, open Word, rasterize every page via Word's PDF.</summary>
        public async Task<bool> RenderAllAsync(string markdown, AppSettings settings, IProgress<int>? progress = null)
        {
            _lastMarkdown = markdown;
            _tempDocxPath = NewTempDocxPath();
            _tempPdfPath = NewTempPdfPath();
            if (!await BuildDocxAsync(markdown, settings, _tempDocxPath)) { _tiles = null; return false; }
            _server?.Dispose();
            _server = WordTileServer.Start(_tempDocxPath);
            if (_server == null) { _tiles = null; return false; }

            if (!_server.ExportPdf(_tempPdfPath)) { _tiles = null; return false; }
            int pages = _server.PageCount;
            var tiles = new byte[pages][];
            for (int p = 1; p <= pages; p++)
            {
                tiles[p - 1] = _server.RenderPdfPage(_tempPdfPath, p, TileScale);
                progress?.Report(p);
            }
            _tiles = tiles;
            return tiles.All(t => t != null);
        }

        /// <summary>
        /// Incremental update: re-export, reopen in the SAME Word instance, re-render only the
        /// page bands the edit touched. Returns the dirty page set (1-based). Null if unavailable.
        /// </summary>
        public async Task<IReadOnlySet<int>?> UpdateAsync(string markdown, AppSettings settings)
        {
            if (_server == null) return null;
            if (_tiles == null || _tiles.Length == 0) return null;

            var (first, last) = ComputeDirtySpan(_lastMarkdown ?? "", markdown);
            int oldPages = _tiles.Length;

            // Word keeps the previously exported docx open; write the new version to a fresh
            // path, reopen (which closes the old one in Word), then drop the old file.
            string oldPath = _tempDocxPath;
            string newPath = NewTempDocxPath();
            string newPdfPath = NewTempPdfPath();
            if (!await BuildDocxAsync(markdown, settings, newPath))
            {
                try { File.Delete(newPath); } catch { }
                return null;
            }
            if (!_server.Reopen(newPath))
            {
                try { File.Delete(newPath); } catch { }
                return null;
            }
            _tempDocxPath = newPath;
            try { File.Delete(oldPath); } catch { }

            int newPages = _server.PageCount;
            var dirty = new HashSet<int>();
            if (newPages != oldPages)
            {
                // Pagination shifted — the whole grid is suspect; rebuild it.
                _tiles = null;
                await RenderAllAsync(markdown, settings).ConfigureAwait(false);
                return _tiles == null ? null : new HashSet<int>(Enumerable.Range(1, _tiles.Length));
            }

            int totalLines = Math.Max(1, markdown.Split('\n').Length);
            var (firstPage, lastPage) = DirtyPages(newPages, totalLines, first, last);

            // Word exports ONLY the dirty page range (From..To) — the layout cost scales with the
            // edit, not the document. Rasterize those pages from the fresh PDF at 2x (crisp).
            if (!_server.ExportPdf(newPdfPath, firstPage, lastPage))
            {
                try { File.Delete(newPdfPath); } catch { }
                return null;
            }
            _tempPdfPath = newPdfPath;
            for (int p = firstPage; p <= lastPage; p++)
            {
                if (_server.RenderPdfPage(_tempPdfPath, p - firstPage + 1, TileScale) is { } bytes)
                {
                    _tiles[p - 1] = bytes;
                    dirty.Add(p);
                }
            }
            _lastMarkdown = markdown;
            return dirty;
        }

        /// <summary>1-based line span (inclusive) that changed between two markdown sources.</summary>
        public static (int First, int Last) ComputeDirtySpan(string oldMd, string newMd)
        {
            var a = oldMd.Split('\n');
            var b = newMd.Split('\n');
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix &&
                   a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
            int first = prefix + 1;                       // 1-based first changed line
            int last = Math.Max(first, b.Length - suffix); // 1-based last changed line
            return (first, last);
        }

        /// <summary>Maps a changed line span to the page bands it can affect (1-based, padded).</summary>
        public static (int FirstPage, int LastPage) DirtyPages(int totalPages, int totalLines, int firstChangedLine, int lastChangedLine)
        {
            if (totalPages <= 0) return (0, 0);
            if (totalPages == 1) return (1, 1);
            double linesPerPage = (double)totalLines / totalPages;
            int first = Math.Max(1, (int)Math.Floor((firstChangedLine - 1) / linesPerPage) + 1);
            int last = Math.Min(totalPages, (int)Math.Ceiling(lastChangedLine / linesPerPage));
            // A line sits near a page boundary — include the neighbours so partial spill re-renders.
            first = Math.Max(1, first - 1);
            last = Math.Min(totalPages, last + 1);
            return (first, last);
        }

        /// <summary>Grid preview page: every band as a tile; dirty bands show a refreshing overlay.</summary>
        public string BuildPageHtml(bool lookingGlassMode, bool stale, IReadOnlySet<int>? refreshing = null)
        {
            return WordFidelityPage.Build(_tiles, lookingGlassMode, stale, refreshing);
        }

        /// <summary>
        /// Writes the tile PNGs + the grid page into the engine's temp folder and returns the
        /// .html path. The host navigates to this FILE (relative img refs) — NavigateToString
        /// caps around 2MB of content and 2x page tiles blow past it.
        /// </summary>
        public string? BuildPageFile(bool lookingGlassMode, bool stale, IReadOnlySet<int>? refreshing = null)
        {
            if (_tiles == null || _tiles.Length == 0) return null;
            try
            {
                for (int i = 0; i < _tiles.Length; i++)
                {
                    File.WriteAllBytes(Path.Combine(_tempDir, $"page_{i + 1}.png"), _tiles[i]);
                }
                string pagePath = Path.Combine(_tempDir, "fidelity.html");
                File.WriteAllText(pagePath, BuildPageHtml(lookingGlassMode, stale, refreshing));
                return pagePath;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server?.Dispose();
            try { File.Delete(_tempDocxPath); } catch { }
            try { File.Delete(_tempPdfPath); } catch { }
            try { if (Directory.Exists(_tempDir) && !Directory.EnumerateFileSystemEntries(_tempDir).Any()) Directory.Delete(_tempDir); } catch { }
        }

        private string NewTempDocxPath() => Path.Combine(_tempDir, $"fidelity_{Guid.NewGuid():N}.docx");
        private string NewTempPdfPath() => Path.Combine(_tempDir, $"fidelity_{Guid.NewGuid():N}.pdf");
    }
}
