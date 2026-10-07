using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EverythingHttpPlugin
{
    public sealed class EverythingIpcClient
    {
        private readonly PluginConfig _config;

        private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tiff", ".tif", ".heic", ".avif"
        };
        private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".m4v", ".ts", ".mts", ".3gp"
        };
        private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".opus"
        };
        private static readonly HashSet<string> DocExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".csv", ".tsv"
        };
        private static readonly HashSet<string> CodeExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".js", ".jsx", ".ts", ".tsx", ".html", ".htm", ".css", ".json", ".xml", ".cs", ".cpp", ".c", ".h", ".py", ".rs", ".go", ".ps1", ".cmd", ".bat", ".sh", ".sql", ".yaml", ".yml", ".md"
        };
        private static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso"
        };

        public EverythingIpcClient(PluginConfig config)
        {
            _config = config;
        }

        public async Task<SearchResponse> SearchAsync(
            string query,
            int offset = 0,
            int count = 50,
            string sort = "name",
            bool ascending = true,
            bool matchCase = false,
            bool matchWholeWord = false,
            bool matchPath = false,
            bool regex = false,
            string category = "all",
            string folder = "")
        {
            var sw = Stopwatch.StartNew();

            var esPath = FindEsExecutable();
            if (!string.IsNullOrEmpty(esPath) && File.Exists(esPath))
            {
                try
                {
                    var response = await QueryViaEverythingIpcAsync(esPath, query, offset, count, sort, ascending, matchCase, matchWholeWord, matchPath, regex, category, folder);
                    if (response != null)
                    {
                        sw.Stop();
                        response.TimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
                        return response;
                    }
                }
                catch
                {
                    // Fall through to fallback
                }
            }

            // Fallback: Local directory search fallback if Everything IPC is unavailable
            var fallback = QueryLocalFallback(query, offset, count, folder);
            sw.Stop();
            fallback.TimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            return fallback;
        }

        private async Task<SearchResponse?> QueryViaEverythingIpcAsync(
            string esPath,
            string query,
            int offset,
            int count,
            string sort,
            bool ascending,
            bool matchCase,
            bool matchWholeWord,
            bool matchPath,
            bool regex,
            string category,
            string folder)
        {
            var instance = string.IsNullOrEmpty(_config.EverythingInstance) ? "1.5a" : _config.EverythingInstance;

            // 1. Prepare search terms and flags
            var searchTerms = BuildSearchTerms(query, category, folder);

            // 2. Query total count and results in parallel
            var countTask = QueryTotalCountAsync(esPath, instance, searchTerms, matchCase, matchWholeWord, matchPath, regex);
            var resultsTask = QueryResultsAsync(esPath, instance, searchTerms, offset, count, sort, ascending, matchCase, matchWholeWord, matchPath, regex);

            await Task.WhenAll(countTask, resultsTask);

            long totalCount = await countTask;
            var items = await resultsTask;

            if (items == null)
            {
                return null;
            }

            if (totalCount < items.Count)
            {
                totalCount = items.Count;
            }

            return new SearchResponse
            {
                Query = query ?? "",
                Offset = offset,
                Count = items.Count,
                TotalResults = totalCount,
                Results = items
            };
        }

        private async Task<long> QueryTotalCountAsync(
            string esPath,
            string instance,
            List<string> terms,
            bool matchCase,
            bool matchWholeWord,
            bool matchPath,
            bool regex)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = esPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                psi.ArgumentList.Add("-instance");
                psi.ArgumentList.Add(instance);
                psi.ArgumentList.Add("-ipc3");
                psi.ArgumentList.Add("-get-result-count");

                if (matchCase) psi.ArgumentList.Add("-case");
                if (matchWholeWord) psi.ArgumentList.Add("-whole-word");
                if (matchPath) psi.ArgumentList.Add("-path");
                if (regex) psi.ArgumentList.Add("-regex");

                foreach (var term in terms)
                {
                    psi.ArgumentList.Add(term);
                }

                using var proc = Process.Start(psi);
                if (proc == null) return 0;

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                var trimmed = stdout.Trim();
                if (long.TryParse(trimmed, out var count))
                {
                    return count;
                }
            }
            catch { }

            return 0;
        }

        private async Task<List<SearchResultItem>?> QueryResultsAsync(
            string esPath,
            string instance,
            List<string> terms,
            int offset,
            int count,
            string sort,
            bool ascending,
            bool matchCase,
            bool matchWholeWord,
            bool matchPath,
            bool regex)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = esPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                psi.ArgumentList.Add("-instance");
                psi.ArgumentList.Add(instance);
                psi.ArgumentList.Add("-ipc3");
                psi.ArgumentList.Add("-json");
                psi.ArgumentList.Add("-name");
                psi.ArgumentList.Add("-path-column");
                psi.ArgumentList.Add("-size");
                psi.ArgumentList.Add("-dm");
                psi.ArgumentList.Add("-attributes");
                psi.ArgumentList.Add("-viewport-offset");
                psi.ArgumentList.Add(offset.ToString());
                psi.ArgumentList.Add("-viewport-count");
                psi.ArgumentList.Add(count.ToString());

                if (matchCase) psi.ArgumentList.Add("-case");
                if (matchWholeWord) psi.ArgumentList.Add("-whole-word");
                if (matchPath) psi.ArgumentList.Add("-path");
                if (regex) psi.ArgumentList.Add("-regex");

                var sortName = sort.ToLowerInvariant() switch
                {
                    "name" => "name",
                    "path" => "path",
                    "size" => "size",
                    "date" or "date_modified" or "datemodified" => "date-modified",
                    "extension" or "ext" => "extension",
                    _ => "name"
                };
                psi.ArgumentList.Add("-sort");
                psi.ArgumentList.Add($"{sortName}-{(ascending ? "ascending" : "descending")}");

                foreach (var term in terms)
                {
                    psi.ArgumentList.Add(term);
                }

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(stdout))
                {
                    return new List<SearchResultItem>();
                }

                var items = new List<SearchResultItem>();
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var name = el.TryGetProperty("name", out var pName) ? pName.GetString() ?? "" : "";
                        var path = el.TryGetProperty("path", out var pPath) ? pPath.GetString() ?? "" : "";

                        long size = 0;
                        if (el.TryGetProperty("size", out var pSize))
                        {
                            if (pSize.ValueKind == JsonValueKind.Number) size = pSize.GetInt64();
                            else if (pSize.ValueKind == JsonValueKind.String && long.TryParse(pSize.GetString(), out var sVal)) size = sVal;
                        }

                        long dateMod = 0;
                        if (el.TryGetProperty("date_modified", out var pDate))
                        {
                            if (pDate.ValueKind == JsonValueKind.Number) dateMod = pDate.GetInt64();
                            else if (pDate.ValueKind == JsonValueKind.String && long.TryParse(pDate.GetString(), out var dVal)) dateMod = dVal;
                        }

                        long attr = 0;
                        if (el.TryGetProperty("attributes", out var pAttr))
                        {
                            if (pAttr.ValueKind == JsonValueKind.Number) attr = pAttr.GetInt64();
                            else if (pAttr.ValueKind == JsonValueKind.String && long.TryParse(pAttr.GetString(), out var aVal)) attr = aVal;
                        }

                        bool isFolder = (attr & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY
                        var fullPath = CombinePath(path, name);

                        items.Add(CreateItem(name, path, fullPath, size, dateMod, isFolder));
                    }
                }

                return items;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> BuildSearchTerms(string query, string category, string folder)
        {
            var terms = new List<string>();

            // Category filter
            if (!string.IsNullOrEmpty(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                switch (category.ToLowerInvariant())
                {
                    case "audio":
                        terms.Add("audio:");
                        break;
                    case "video":
                        terms.Add("video:");
                        break;
                    case "image" or "pic" or "picture":
                        terms.Add("pic:");
                        break;
                    case "doc" or "document":
                        terms.Add("doc:");
                        break;
                    case "code":
                        terms.Add("ext:js;jsx;ts;tsx;html;htm;css;json;xml;cs;cpp;c;h;py;rs;go;ps1;cmd;bat;sh;sql;yaml;yml;md");
                        break;
                    case "zip" or "archive":
                        terms.Add("zip:");
                        break;
                    case "folder":
                        terms.Add("folder:");
                        break;
                }
            }

            // Folder navigation filter
            if (!string.IsNullOrEmpty(folder))
            {
                var cleanFolder = folder.TrimEnd('\\', '/');
                terms.Add($"parent:\"{cleanFolder}\"");
            }

            // Tokenize search query respecting double quotes
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matches = Regex.Matches(query, @"[\""].+?[\""]|[^ ]+");
                foreach (Match m in matches)
                {
                    var val = m.Value.Trim();
                    if (val.StartsWith('"') && val.EndsWith('"') && val.Length > 2)
                    {
                        val = val[1..^1];
                    }
                    if (!string.IsNullOrEmpty(val))
                    {
                        terms.Add(val);
                    }
                }
            }

            return terms;
        }

        private SearchResponse QueryLocalFallback(string query, int offset, int count, string folder)
        {
            var targetDir = !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var items = new List<SearchResultItem>();
            try
            {
                var dirInfo = new DirectoryInfo(targetDir);
                var entries = dirInfo.EnumerateFileSystemInfos();

                foreach (var entry in entries)
                {
                    if (!string.IsNullOrEmpty(query) && !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isDir = entry is DirectoryInfo;
                    long size = entry is FileInfo fi ? fi.Length : 0;
                    long dateMod = entry.LastWriteTimeUtc.ToFileTimeUtc();

                    items.Add(CreateItem(entry.Name, entry.FullName, entry.FullName, size, dateMod, isDir));
                }
            }
            catch { }

            var total = items.Count;
            var paged = items;
            if (offset > 0 || count < items.Count)
            {
                int take = Math.Min(count, Math.Max(0, items.Count - offset));
                paged = items.GetRange(Math.Min(offset, items.Count), take);
            }

            return new SearchResponse
            {
                Query = query ?? "",
                Offset = offset,
                Count = paged.Count,
                TotalResults = total,
                Results = paged
            };
        }

        public static string? FindEsExecutable()
        {
            // 1. WinGet Links
            var userPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\es.exe");
            if (File.Exists(userPath)) return userPath;

            // 2. Program Files Everything 1.5a
            var progFiles = @"C:\Program Files\Everything 1.5a\es.exe";
            if (File.Exists(progFiles)) return progFiles;

            // 3. Program Files Everything
            var progFilesOld = @"C:\Program Files\Everything\es.exe";
            if (File.Exists(progFilesOld)) return progFilesOld;

            // 4. PATH search
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (var p in pathVar.Split(';'))
                {
                    var trimmed = p.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                    {
                        var candidate = Path.Combine(trimmed, "es.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }

            return null;
        }

        private static string CombinePath(string path, string name)
        {
            if (string.IsNullOrEmpty(path)) return name;
            if (string.IsNullOrEmpty(name)) return path;
            return path.TrimEnd('\\', '/') + "\\" + name.TrimStart('\\', '/');
        }

        public static string Categorize(string ext, bool isFolder)
        {
            if (isFolder) return "folder";
            if (ImageExts.Contains(ext)) return "image";
            if (VideoExts.Contains(ext)) return "video";
            if (AudioExts.Contains(ext)) return "audio";
            if (DocExts.Contains(ext)) return "doc";
            if (CodeExts.Contains(ext)) return "code";
            if (ArchiveExts.Contains(ext)) return "zip";
            return "other";
        }

        private static SearchResultItem CreateItem(string name, string path, string fullPath, long size, long dateMod, bool isFolder)
        {
            var ext = isFolder ? "" : Path.GetExtension(name);
            var cat = Categorize(ext, isFolder);
            bool isGdrive = fullPath.StartsWith("G:\\", StringComparison.OrdinalIgnoreCase);

            return new SearchResultItem
            {
                Name = name,
                Path = path,
                FullPath = fullPath,
                Size = size,
                DateModified = dateMod,
                IsFolder = isFolder,
                Extension = ext,
                Category = cat,
                StreamUrl = isFolder ? "" : $"/api/stream?path={Uri.EscapeDataString(fullPath)}",
                DownloadUrl = isFolder ? "" : $"/api/download?path={Uri.EscapeDataString(fullPath)}",
                ThumbnailUrl = isFolder ? "" : $"/api/thumbnail?path={Uri.EscapeDataString(fullPath)}&size=360",
                SizeFormatted = isFolder ? "-" : FormatBytes(size),
                DateModifiedRelative = FormatDateModified(dateMod),
                CanPreviewImage = cat == "image",
                CanPreviewVideo = cat == "video",
                CanPreviewAudio = cat == "audio",
                CanPreviewText = cat == "code" || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase),
                CanPreviewPdf = ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase),
                IsGoogleDrive = isGdrive
            };
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] suf = { "B", "KB", "MB", "GB", "TB" };
            int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
            if (place >= suf.Length) place = suf.Length - 1;
            double num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return $"{num} {suf[place]}";
        }

        private static string FormatDateModified(long dateMod)
        {
            if (dateMod <= 0) return "-";
            try
            {
                var dt = DateTime.FromFileTimeUtc(dateMod);
                var diff = DateTime.UtcNow - dt;
                if (diff.TotalMinutes < 1) return "Just now";
                if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}m ago";
                if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
                if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}d ago";
                return dt.ToString("yyyy-MM-dd");
            }
            catch
            {
                return "-";
            }
        }
    }
}
