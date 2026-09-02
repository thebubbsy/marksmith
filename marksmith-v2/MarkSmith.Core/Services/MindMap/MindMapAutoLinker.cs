using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MarkSmith.Models.MindMap;

namespace MarkSmith.Services.MindMap
{
    public sealed class MindMapAutoLinkerOptions
    {
        /// <summary>Upper bound on files pulled into one map. Past a few hundred nodes the canvas
        /// stops being readable long before it stops being fast.</summary>
        public int MaxFiles { get; set; } = 400;

        /// <summary>Mirror the directory tree as folder nodes. Without this every file becomes a
        /// direct child of the root and a 200-file vault lays out as one unreadable column.</summary>
        public bool MirrorFolders { get; set; } = true;

        /// <summary>How many tags two documents must share before the linker infers a connection.
        /// Two is the floor that keeps a common tag like #notes from linking everything to
        /// everything.</summary>
        public int SharedTagThreshold { get; set; } = 2;

        /// <summary>Cap on inferred shared-tag edges. A tag applied to 40 documents would otherwise
        /// produce 780 edges on its own and bury every real link under a hairball.</summary>
        public int MaxSharedTagLinks { get; set; } = 120;

        /// <summary>Characters of file content kept on each node for preview and search.</summary>
        public int PreviewCharacterLimit { get; set; } = 20000;

        public static readonly string[] DefaultExtensions =
        {
            ".md", ".markdown", ".mdx", ".txt", ".docx", ".pdf", ".pptx", ".epub", ".rtf", ".html", ".htm"
        };

        /// <summary>Directories never worth mapping — build output and tool caches would otherwise
        /// swamp the map with thousands of vendored files.</summary>
        public static readonly string[] IgnoredDirectories =
        {
            ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".vscode", ".idea",
            "__pycache__", ".venv", "venv", "dist", "build", "target", ".next", ".cache", ".trash"
        };
    }

    public sealed class MindMapAutoLinker
    {
        private static readonly Regex WikiLinkRegex = new(@"\[\[([^\]\|#]+)(?:[#\|][^\]]*)?\]\]", RegexOptions.Compiled);

        // Skips absolute URLs, mailto:, and pure in-page anchors; the leading (?<!\!) keeps image
        // embeds out so they can be classified separately as Embed links.
        private static readonly Regex MdLinkRegex = new(
            @"(?<!\!)\[([^\]]*)\]\(\s*(?!https?://|mailto:|#)([^)\s]+)",
            RegexOptions.Compiled);

        private static readonly Regex EmbedRegex = new(
            @"!\[([^\]]*)\]\(\s*(?!https?://|mailto:|#)([^)\s]+)",
            RegexOptions.Compiled);

        // A tag is a # at a word boundary followed by a letter — that excludes "#1", "#404" issue
        // refs and "#FF7C4D" colour literals, all of which used to become tags and then link
        // unrelated documents together through a shared "tag".
        private static readonly Regex TagRegex = new(@"(?<![\w&/])#([A-Za-z][\w\-/]{1,47})\b", RegexOptions.Compiled);

        private static readonly Regex HeadingRegex = new(@"^#\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex FencedCodeRegex = new(@"^[ \t]*(```|~~~).*?^[ \t]*\1[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex InlineCodeRegex = new(@"`[^`\r\n]*`", RegexOptions.Compiled);
        private static readonly Regex FrontMatterRegex = new(@"\A---\r?\n(.*?)\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);

        public async Task<MindMapDocument> BuildGalaxyFromDirectoryAsync(string directoryPath, string? vaultName = null)
            => await BuildGalaxyFromDirectoryAsync(directoryPath, new MindMapAutoLinkerOptions(), vaultName, CancellationToken.None);

        public async Task<MindMapDocument> BuildGalaxyFromDirectoryAsync(
            string directoryPath,
            MindMapAutoLinkerOptions options,
            string? vaultName = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new MindMapAutoLinkerOptions();

            var doc = new MindMapDocument
            {
                Title = vaultName ?? SafeDirectoryName(directoryPath),
                LastSaved = DateTime.Now.ToString("o"),
                SourceDirectory = directoryPath
            };

            var root = new MindMapNode
            {
                Title = doc.Title,
                NodeType = MindMapNodeType.Project,
                X = 0,
                Y = 0,
                Width = 240,
                Height = 62,
                ColorHex = "#FF7C4D",
                Icon = "\uEC07",
                Progress = 100,
                Tags = new() { "#vault", "#root" }
            };
            doc.RootNodeId = root.Id;
            doc.Nodes.Add(root);

            if (!Directory.Exists(directoryPath)) return doc;

            var files = EnumerateCandidateFiles(directoryPath, options).Take(options.MaxFiles).ToList();

            string[] palette = doc.Theme.BranchColors.ToArray();
            int colorIdx = 0;

            // Folder nodes are created lazily, so a directory containing nothing mappable never
            // appears as an empty branch.
            var folderNodes = new Dictionary<string, MindMapNode>(StringComparer.OrdinalIgnoreCase)
            {
                [NormalizeDirectory(directoryPath)] = root
            };

            var fileNodes = new Dictionary<string, MindMapNode>(StringComparer.OrdinalIgnoreCase);
            var nameIndex = new NameIndex();

            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string ext = Path.GetExtension(file).ToLowerInvariant();
                string baseName = Path.GetFileNameWithoutExtension(file);
                var scan = await ScanFileAsync(file, ext, options);

                var node = new MindMapNode
                {
                    Title = scan.Title ?? baseName,
                    FilePath = file,
                    FileExtension = ext,
                    NodeType = MindMapNodeType.Document,
                    Width = 190,
                    Height = 56,
                    ColorHex = palette[colorIdx++ % palette.Length],
                    Icon = IconFor(ext),
                    Tags = MindMapGraph.NormalizeTags(scan.Tags),
                    MarkdownContent = scan.Preview,
                    WordCount = scan.WordCount,
                    CreatedDate = SafeStamp(() => File.GetCreationTime(file)),
                    ModifiedDate = SafeStamp(() => File.GetLastWriteTime(file))
                };

                var parent = options.MirrorFolders
                    ? EnsureFolderNode(doc, folderNodes, directoryPath, Path.GetDirectoryName(file), root, palette)
                    : root;

                node.ParentId = parent.Id;
                parent.ChildIds.Add(node.Id);
                doc.Nodes.Add(node);

                fileNodes[file] = node;
                nameIndex.Add(baseName, node);
                nameIndex.Add(Path.GetFileName(file), node);
                if (scan.Title != null) nameIndex.Add(scan.Title, node);
                nameIndex.Add(RelativePath(directoryPath, file), node);
            }

            // Linking runs in strict precedence order over the WHOLE vault, strongest reason first:
            // an explicit [[wikilink]] must never lose its slot to an inferred shared-tag edge just
            // because the tagged file happened to be scanned first. Each pass only fills pairs that
            // no stronger pass already claimed.
            var claimed = new Dictionary<(string, string), MindMapLink>();

            AddEmbedLinks(doc, fileNodes, nameIndex, claimed, directoryPath);
            AddWikiLinks(doc, fileNodes, nameIndex, claimed, directoryPath);
            AddCrossReferenceLinks(doc, fileNodes, nameIndex, claimed, directoryPath);
            AddSharedTagLinks(doc, fileNodes, claimed, options);

            MindMapGraph.Normalize(doc);
            return doc;
        }

        // ---- File discovery ----

        private static IEnumerable<string> EnumerateCandidateFiles(string rootDirectory, MindMapAutoLinkerOptions options)
        {
            var extensions = new HashSet<string>(MindMapAutoLinkerOptions.DefaultExtensions, StringComparer.OrdinalIgnoreCase);
            var ignored = new HashSet<string>(MindMapAutoLinkerOptions.IgnoredDirectories, StringComparer.OrdinalIgnoreCase);

            // Manual walk rather than SearchOption.AllDirectories: the recursive enumerator gives up
            // on the first unreadable directory, so one permission-denied folder used to abort the
            // scan of an entire vault.
            var queue = new Queue<string>();
            queue.Enqueue(rootDirectory);

            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    if (!extensions.Contains(Path.GetExtension(f))) continue;
                    if (Path.GetFileName(f).StartsWith(".", StringComparison.Ordinal)) continue;
                    yield return f;
                }

                string[] subdirs;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
                foreach (string sub in subdirs)
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || ignored.Contains(name)) continue;
                    queue.Enqueue(sub);
                }
            }
        }

        private static MindMapNode EnsureFolderNode(
            MindMapDocument doc,
            Dictionary<string, MindMapNode> folderNodes,
            string vaultRoot,
            string? directory,
            MindMapNode root,
            string[] palette)
        {
            if (string.IsNullOrEmpty(directory)) return root;

            string key = NormalizeDirectory(directory);
            if (folderNodes.TryGetValue(key, out var existing)) return existing;

            string rootKey = NormalizeDirectory(vaultRoot);
            if (!key.StartsWith(rootKey, StringComparison.OrdinalIgnoreCase)) return root;

            var parent = EnsureFolderNode(doc, folderNodes, vaultRoot, Path.GetDirectoryName(directory), root, palette);

            var node = new MindMapNode
            {
                Title = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                NodeType = MindMapNodeType.Folder,
                FilePath = directory,
                Width = 200,
                Height = 54,
                ColorHex = palette[(key.GetHashCode() & 0x7FFFFFFF) % palette.Length],
                Icon = "\uE8B7",
                ParentId = parent.Id,
                Tags = new List<string> { "#folder" }
            };
            parent.ChildIds.Add(node.Id);
            doc.Nodes.Add(node);
            folderNodes[key] = node;
            return node;
        }

        // ---- Linking passes ----

        private void AddEmbedLinks(MindMapDocument doc, Dictionary<string, MindMapNode> fileNodes, NameIndex names,
            Dictionary<(string, string), MindMapLink> claimed, string vaultRoot)
        {
            foreach (var (path, source) in fileNodes)
            {
                if (source.MarkdownContent is not { Length: > 0 } content) continue;
                foreach (Match m in EmbedRegex.Matches(content))
                {
                    var target = names.Resolve(m.Groups[2].Value, path, vaultRoot);
                    TryClaim(doc, claimed, source, target, MindMapLinkKind.Embed, "embeds",
                        source.ColorHex, MindMapLinkStyle.Straight);
                }
            }
        }

        private void AddWikiLinks(MindMapDocument doc, Dictionary<string, MindMapNode> fileNodes, NameIndex names,
            Dictionary<(string, string), MindMapLink> claimed, string vaultRoot)
        {
            foreach (var (path, source) in fileNodes)
            {
                if (source.MarkdownContent is not { Length: > 0 } content) continue;
                foreach (Match m in WikiLinkRegex.Matches(content))
                {
                    var target = names.Resolve(m.Groups[1].Value, path, vaultRoot);
                    TryClaim(doc, claimed, source, target, MindMapLinkKind.WikiLink, "wikilink",
                        source.ColorHex, MindMapLinkStyle.CurvedBezier);
                }
            }
        }

        private void AddCrossReferenceLinks(MindMapDocument doc, Dictionary<string, MindMapNode> fileNodes, NameIndex names,
            Dictionary<(string, string), MindMapLink> claimed, string vaultRoot)
        {
            foreach (var (path, source) in fileNodes)
            {
                if (source.MarkdownContent is not { Length: > 0 } content) continue;
                foreach (Match m in MdLinkRegex.Matches(content))
                {
                    var target = names.Resolve(m.Groups[2].Value, path, vaultRoot);
                    TryClaim(doc, claimed, source, target, MindMapLinkKind.CrossReference, "cross-reference",
                        source.ColorHex, MindMapLinkStyle.Dashed);
                }
            }
        }

        private void AddSharedTagLinks(MindMapDocument doc, Dictionary<string, MindMapNode> fileNodes,
            Dictionary<(string, string), MindMapLink> claimed, MindMapAutoLinkerOptions options)
        {
            if (options.SharedTagThreshold <= 0) return;

            var tagged = fileNodes.Values.Where(n => n.Tags.Count >= options.SharedTagThreshold).ToList();

            // Score every candidate pair first, then keep only the best ones. Emitting them as they
            // are found lets whichever pairs happen to come first in directory order win, which is
            // arbitrary; strongest-overlap-first is at least meaningful.
            var candidates = new List<(MindMapNode A, MindMapNode B, List<string> Shared)>();
            for (int i = 0; i < tagged.Count; i++)
            {
                for (int j = i + 1; j < tagged.Count; j++)
                {
                    var shared = tagged[i].Tags
                        .Intersect(tagged[j].Tags, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (shared.Count >= options.SharedTagThreshold)
                    {
                        candidates.Add((tagged[i], tagged[j], shared));
                    }
                }
            }

            foreach (var (a, b, shared) in candidates
                         .OrderByDescending(c => c.Shared.Count)
                         .ThenBy(c => c.A.Title, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.B.Title, StringComparer.OrdinalIgnoreCase)
                         .Take(options.MaxSharedTagLinks))
            {
                TryClaim(doc, claimed, a, b, MindMapLinkKind.SharedTag,
                    $"shared {string.Join(", ", shared.Take(3))}", "#7C4DFF",
                    MindMapLinkStyle.SynapseGlow, MindMapLinkDirection.Bidirectional, shared.Count);
            }
        }

        /// <summary>
        /// Records one edge unless a stronger reason already claimed that pair. When the new reason
        /// outranks the old one the existing link is upgraded in place rather than duplicated, so
        /// "these two share tags" is quietly replaced by "this one links to that one".
        /// </summary>
        private static void TryClaim(
            MindMapDocument doc,
            Dictionary<(string, string), MindMapLink> claimed,
            MindMapNode source,
            MindMapNode? target,
            MindMapLinkKind kind,
            string label,
            string colorHex,
            MindMapLinkStyle style,
            MindMapLinkDirection direction = MindMapLinkDirection.SourceToTarget,
            double weight = 1.0)
        {
            if (target == null || target.Id == source.Id) return;

            var key = MindMapGraph.PairKey(source.Id, target.Id);
            if (claimed.TryGetValue(key, out var existing))
            {
                if (MindMapLinkKindRank.Of(kind) <= MindMapLinkKindRank.Of(existing.Kind)) return;

                existing.SourceNodeId = source.Id;
                existing.TargetNodeId = target.Id;
                existing.Kind = kind;
                existing.Label = label;
                existing.ColorHex = colorHex;
                existing.Style = style;
                existing.Direction = direction;
                existing.Weight = weight;
                return;
            }

            var link = new MindMapLink
            {
                SourceNodeId = source.Id,
                TargetNodeId = target.Id,
                Label = label,
                ColorHex = colorHex,
                Style = style,
                Direction = direction,
                Kind = kind,
                Weight = weight
            };
            claimed[key] = link;
            doc.Links.Add(link);
        }

        // ---- File scanning ----

        private sealed record FileScan(string? Title, List<string> Tags, string? Preview, int WordCount);

        private static async Task<FileScan> ScanFileAsync(string file, string ext, MindMapAutoLinkerOptions options)
        {
            bool isText = ext is ".md" or ".markdown" or ".mdx" or ".txt";
            if (!isText) return new FileScan(null, new List<string>(), null, 0);

            const long maxScanBytes = 8L * 1024 * 1024;
            string content;
            try
            {
                // A vault can contain a multi-hundred-megabyte log or dump with a .txt extension.
                // It still deserves a node; it does not deserve to be read into memory whole.
                if (new FileInfo(file).Length > maxScanBytes)
                {
                    return new FileScan(null, new List<string>(), null, 0);
                }
                content = await File.ReadAllTextAsync(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new FileScan(null, new List<string>(), null, 0);
            }

            var tags = new List<string>();
            string? title = null;

            var fm = FrontMatterRegex.Match(content);
            string body = content;
            if (fm.Success)
            {
                ParseFrontMatter(fm.Groups[1].Value, ref title, tags);
                body = content[fm.Length..];
            }

            if (title == null)
            {
                var h = HeadingRegex.Match(body);
                if (h.Success && !string.IsNullOrWhiteSpace(h.Groups[1].Value))
                {
                    title = h.Groups[1].Value.Trim().TrimEnd('#').Trim();
                }
            }

            // Tags are read from prose only. Scanning code blocks turned CSS colours, shell
            // comments and C# preprocessor directives into tags, and those false tags then linked
            // unrelated documents to each other.
            string prose = InlineCodeRegex.Replace(FencedCodeRegex.Replace(body, " "), " ");
            foreach (Match tm in TagRegex.Matches(prose))
            {
                tags.Add(tm.Groups[1].Value.Trim());
            }

            int wordCount = CountWords(prose);
            string preview = content.Length > options.PreviewCharacterLimit
                ? content[..options.PreviewCharacterLimit] + "\n\n…(truncated)"
                : content;

            return new FileScan(string.IsNullOrWhiteSpace(title) ? null : title, tags, preview, wordCount);
        }

        private static void ParseFrontMatter(string yaml, ref string? title, List<string> tags)
        {
            // Deliberately not a YAML parser — just the two keys that carry map-relevant meaning,
            // in both the inline `[a, b]` and the `- a` block forms.
            bool inTagBlock = false;
            foreach (string rawLine in yaml.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                string trimmed = line.Trim();

                if (inTagBlock)
                {
                    if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        tags.Add(trimmed[2..].Trim().Trim('"', '\''));
                        continue;
                    }
                    inTagBlock = false;
                }

                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;

                string key = trimmed[..colon].Trim().ToLowerInvariant();
                string value = trimmed[(colon + 1)..].Trim();

                if (key == "title" && value.Length > 0)
                {
                    title = value.Trim('"', '\'');
                }
                else if (key is "tags" or "keywords")
                {
                    if (value.Length == 0)
                    {
                        inTagBlock = true;
                    }
                    else
                    {
                        foreach (string t in value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            tags.Add(t.Trim().Trim('"', '\''));
                        }
                    }
                }
            }
        }

        private static int CountWords(string text)
        {
            int count = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { inWord = false; }
                else if (!inWord) { inWord = true; count++; }
            }
            return count;
        }

        // ---- Helpers ----

        /// <summary>
        /// Resolves a link target the way a person reading it would: as a path relative to the
        /// linking file, as a vault-relative path, or by bare name.
        /// </summary>
        private sealed class NameIndex
        {
            private readonly Dictionary<string, MindMapNode> _byName = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _ambiguous = new(StringComparer.OrdinalIgnoreCase);

            public void Add(string? name, MindMapNode node)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                string key = Canonical(name);
                if (key.Length == 0) return;

                if (_byName.TryGetValue(key, out var existing))
                {
                    // Two different files answering to the same name is not a link — guessing one
                    // would silently connect the wrong documents.
                    if (!ReferenceEquals(existing, node)) _ambiguous.Add(key);
                    return;
                }
                _byName[key] = node;
            }

            public MindMapNode? Resolve(string? rawTarget, string sourceFilePath, string vaultRoot)
            {
                if (string.IsNullOrWhiteSpace(rawTarget)) return null;

                string target = Uri.UnescapeDataString(rawTarget.Trim().Trim('<', '>', '"', '\''));
                int hash = target.IndexOf('#');
                if (hash >= 0) target = target[..hash];
                if (target.Length == 0) return null;

                // 1. Resolved against the linking file's own directory — the meaning a Markdown
                //    reader gives a relative link.
                string? sourceDir = Path.GetDirectoryName(sourceFilePath);
                if (sourceDir != null)
                {
                    try
                    {
                        string full = Path.GetFullPath(Path.Combine(sourceDir, target));
                        if (TryGet(RelativePath(vaultRoot, full), out var byRelative)) return byRelative;
                        if (TryGet(Path.GetFileName(full), out var byFileName)) return byFileName;
                        if (TryGet(Path.GetFileNameWithoutExtension(full), out var byStem)) return byStem;
                    }
                    catch (ArgumentException) { /* not a usable path; fall through to name matching */ }
                }

                // 2. Bare name, the [[wikilink]] convention.
                if (TryGet(target, out var direct)) return direct;
                if (TryGet(Path.GetFileNameWithoutExtension(target), out var stem)) return stem;
                return null;
            }

            private bool TryGet(string? name, out MindMapNode? node)
            {
                node = null;
                if (string.IsNullOrWhiteSpace(name)) return false;
                string key = Canonical(name);
                if (_ambiguous.Contains(key)) return false;
                return _byName.TryGetValue(key, out node);
            }

            private static string Canonical(string name) =>
                name.Trim().Replace('\\', '/').TrimStart('.', '/').Trim();
        }

        private static string RelativePath(string root, string fullPath)
        {
            try
            {
                return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            }
            catch (ArgumentException)
            {
                return fullPath;
            }
        }

        private static string NormalizeDirectory(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        private static string SafeDirectoryName(string directoryPath)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath ?? ""));
            return string.IsNullOrWhiteSpace(name) ? "Document Galaxy Vault" : name;
        }

        private static string? SafeStamp(Func<DateTime> read)
        {
            try
            {
                return read().ToString("yyyy-MM-dd");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string IconFor(string ext) => ext switch
        {
            ".md" or ".markdown" or ".mdx" => "\uE82D",
            ".docx" or ".rtf" => "\uE8A5",
            ".pdf" => "\uEA90",
            ".pptx" => "\uE9D2",
            ".epub" => "\uE82D",
            ".html" or ".htm" => "\uE774",
            ".txt" => "\uE8A5",
            _ => "\uE8A5"
        };
    }
}
