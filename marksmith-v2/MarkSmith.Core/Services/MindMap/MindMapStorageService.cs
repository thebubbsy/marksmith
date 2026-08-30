using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MarkSmith.Models.MindMap;
using MarkSmith.Services;

namespace MarkSmith.Services.MindMap
{
    public sealed class MindMapStorageService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string GetDefaultLibraryStoragePath()
        {
            // Flow through AppPaths.ConfigDir so MARKSMITH_CONFIG_DIR can redirect the whole
            // config surface — hardcoding %LOCALAPPDATA% here breaks test isolation (#27 convention).
            string dir = Path.Combine(AppPaths.ConfigDir, "MindMaps");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "library.msmap");
        }

        /// <summary>True when the user has a galaxy of their own on disk. The studio only falls
        /// back to the guided tour when this is false — the tour is a first-run experience, not
        /// the thing you see every time you open the window.</summary>
        public static bool HasSavedLibrary(string? filePath = null)
        {
            try
            {
                return File.Exists(filePath ?? GetDefaultLibraryStoragePath());
            }
            catch
            {
                return false;
            }
        }

        public async Task SaveAsync(MindMapDocument doc, string filePath)
        {
            doc.LastSaved = DateTime.Now.ToString("o");
            string json = JsonSerializer.Serialize(doc, JsonOpts);
            AtomicFile.WriteAllText(filePath, json);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Loads a map, repairing anything structurally broken before it reaches the canvas. A
        /// .msmap that fails to parse is preserved as a .corrupt sibling rather than silently
        /// replaced, because it is the user's memory of their whole library.
        /// </summary>
        public async Task<MindMapLoadResult> LoadWithReportAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return new MindMapLoadResult(CreateTutorialGalaxy(), new MindMapRepairReport(), IsFirstRun: true);
            }

            MindMapDocument? doc = null;
            try
            {
                string json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                doc = JsonSerializer.Deserialize<MindMapDocument>(json, JsonOpts);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                TryQuarantine(filePath);
                return new MindMapLoadResult(CreateTutorialGalaxy(), new MindMapRepairReport(), IsFirstRun: true,
                    LoadError: $"'{Path.GetFileName(filePath)}' could not be read and was set aside as .corrupt — starting from the guided tour.");
            }

            if (doc == null || doc.Nodes.Count == 0)
            {
                return new MindMapLoadResult(CreateTutorialGalaxy(), new MindMapRepairReport(), IsFirstRun: true);
            }

            var report = MindMapGraph.Normalize(doc);
            return new MindMapLoadResult(doc, report, IsFirstRun: false);
        }

        public async Task<MindMapDocument> LoadAsync(string filePath) => (await LoadWithReportAsync(filePath)).Document;

        private static void TryQuarantine(string filePath)
        {
            try
            {
                string target = filePath + ".corrupt";
                if (File.Exists(target)) File.Delete(target);
                File.Move(filePath, target);
            }
            catch { /* quarantine is best effort; never block the window from opening */ }
        }

        /// <summary>Kept for callers that only want the sample content.</summary>
        public static MindMapDocument CreateDefaultGalaxy() => CreateTutorialGalaxy();

        /// <summary>
        /// The first-run guided tour. It is a working map — every node is draggable, linkable and
        /// deletable — that also happens to explain what the map is for: relationships between
        /// documents instead of a folder tree. Nodes are flagged IsTutorial so "Clear the tour"
        /// can remove exactly these and leave anything the user added.
        /// </summary>
        public static MindMapDocument CreateTutorialGalaxy()
        {
            var doc = new MindMapDocument
            {
                Title = "Document Galaxy Vault",
                LastSaved = DateTime.Now.ToString("o"),
                IsTutorial = true
            };

            MindMapNode Add(MindMapNode node, MindMapNode? parent = null)
            {
                node.IsTutorial = true;
                node.Tags = MindMapGraph.NormalizeTags(node.Tags);
                if (parent != null)
                {
                    node.ParentId = parent.Id;
                    parent.ChildIds.Add(node.Id);
                }
                doc.Nodes.Add(node);
                return node;
            }

            var root = Add(new MindMapNode
            {
                Title = "MarkSmith Document Galaxy",
                NodeType = MindMapNodeType.Project,
                X = -620,
                Y = -32,
                Width = 250,
                Height = 64,
                ColorHex = "#FF7C4D",
                Icon = "🌌",
                Progress = 100,
                Tags = new() { "vault", "start-here" },
                MarkdownContent =
                    "# Your documents, as a constellation\n\n" +
                    "This is a **memory map** of your writing. Every Markdown, Word, PDF and PowerPoint " +
                    "file you care about becomes a star, and the lines between them are the reasons they " +
                    "belong together — *not* the folder they happen to sit in.\n\n" +
                    "Nothing here is precious. Drag it, rename it, delete it, or clear the whole tour from " +
                    "the toolbar and start with your own vault."
            });
            doc.RootNodeId = root.Id;

            Add(new MindMapNode
            {
                Title = "① Why this beats folders",
                NodeType = MindMapNodeType.Note,
                X = -240,
                Y = -290,
                Width = 250,
                Height = 58,
                ColorHex = "#22D3EE",
                Icon = "🧭",
                Progress = 100,
                Tags = new() { "start-here", "concept" },
                MarkdownContent =
                    "## A file lives in one folder. It belongs to many stories.\n\n" +
                    "A folder tree forces one answer to \"where does this go?\". A research PDF that fed a " +
                    "proposal, got quoted in a deck and started an argument in your notes has four right " +
                    "answers — so it ends up filed under the wrong one and you never find it again.\n\n" +
                    "Here a document sits in **one** place and carries as many named relationships as it " +
                    "earned. You navigate by \"what came out of this\", not by remembering a path."
            }, root);

            Add(new MindMapNode
            {
                Title = "② Every file becomes a star",
                NodeType = MindMapNodeType.Concept,
                X = -240,
                Y = -200,
                Width = 250,
                Height = 58,
                ColorHex = "#34D399",
                Icon = "✨",
                Progress = 100,
                Tags = new() { "start-here", "concept" },
                MarkdownContent =
                    "## Nodes are real files on disk\n\n" +
                    "A node's badge shows its format — `MD`, `DOCX`, `PDF`, `PPTX`, `EPUB`. " +
                    "**Double-click any node** to open the file in the MarkSmith editor; right-click it for " +
                    "its version history.\n\n" +
                    "Nodes without a file are still useful: use them as ideas, milestones and headings that " +
                    "hold a cluster together."
            }, root);

            var lesson3 = Add(new MindMapNode
            {
                Title = "③ Links carry the meaning",
                NodeType = MindMapNodeType.Concept,
                X = -240,
                Y = -110,
                Width = 250,
                Height = 58,
                ColorHex = "#A855F7",
                Icon = "🔗",
                Progress = 100,
                Tags = new() { "start-here", "concept" },
                MarkdownContent =
                    "## Name the relationship, not the folder\n\n" +
                    "Select a node, click **🔗 Link**, pick a target and *say why*: `grew out of`, " +
                    "`evidence for`, `supersedes`, `argues against`.\n\n" +
                    "Solid lines are the hierarchy. Dashed lines are cross-links that cut across it — the " +
                    "connections a folder tree simply cannot express."
            }, root);

            var cluster = Add(new MindMapNode
            {
                Title = "🚀 Worked example: Q3 Launch",
                NodeType = MindMapNodeType.Project,
                X = -240,
                Y = -10,
                Width = 250,
                Height = 62,
                ColorHex = "#FBBF24",
                Icon = "🚀",
                Progress = 70,
                Tags = new() { "example", "launch" },
                MarkdownContent =
                    "## Four formats, one story\n\n" +
                    "These four documents would live in four different folders under any normal filing " +
                    "scheme — research, decks, drafts, deliverables. Follow the dashed lines instead and " +
                    "the actual history of the work reads left to right."
            }, root);

            Add(new MindMapNode
            {
                Title = "④ Import your own vault",
                NodeType = MindMapNodeType.Milestone,
                X = -240,
                Y = 90,
                Width = 250,
                Height = 58,
                ColorHex = "#EC4899",
                Icon = "📂",
                Progress = 0,
                Tags = new() { "start-here", "next-step" },
                MarkdownContent =
                    "## Point it at a real folder\n\n" +
                    "**📂 Import Vault** scans a directory and builds the map for you. It reads " +
                    "`[[wikilinks]]`, relative Markdown links and `#tags` out of your files and draws the " +
                    "connections it finds — folders become clusters, not cages.\n\n" +
                    "Then hit **💾 Save**: the map is yours from that point on and this tour never comes back."
            }, root);

            var brief = Add(new MindMapNode
            {
                Title = "Q3 Launch Brief",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".docx",
                X = 160,
                Y = -60,
                Width = 230,
                Height = 56,
                ColorHex = "#3B82F6",
                Icon = "📄",
                Progress = 90,
                WordCount = 2400,
                Tags = new() { "example", "launch", "deliverable" },
                MarkdownContent =
                    "## Q3 Launch Brief *(Word)*\n\n" +
                    "The deliverable everything else fed into. Notice it is the busiest node in this " +
                    "cluster — that is what a hub looks like on the map."
            }, cluster);

            var research = Add(new MindMapNode
            {
                Title = "Market Research",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".pdf",
                X = 160,
                Y = 20,
                Width = 230,
                Height = 56,
                ColorHex = "#22D3EE",
                Icon = "📑",
                Progress = 100,
                WordCount = 8800,
                Tags = new() { "example", "research" },
                MarkdownContent =
                    "## Market Research *(PDF)*\n\n" +
                    "A vendor report nobody wrote and nobody would think to file under \"launch\". The link " +
                    "labelled *evidence for* is the only thing that makes it findable a year from now."
            }, cluster);

            var notes = Add(new MindMapNode
            {
                Title = "Launch Notes",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".md",
                X = 160,
                Y = 100,
                Width = 230,
                Height = 56,
                ColorHex = "#34D399",
                Icon = "📝",
                Progress = 60,
                WordCount = 1300,
                Tags = new() { "example", "launch", "notes" },
                MarkdownContent =
                    "## Launch Notes *(Markdown)*\n\n" +
                    "Where the thinking happened. Scrappy, dated, full of `[[wikilinks]]` — exactly the " +
                    "material the vault importer turns into edges automatically."
            }, cluster);

            var deck = Add(new MindMapNode
            {
                Title = "Kickoff Deck",
                NodeType = MindMapNodeType.Document,
                FileExtension = ".pptx",
                X = 160,
                Y = 180,
                Width = 230,
                Height = 56,
                ColorHex = "#EC4899",
                Icon = "📊",
                Progress = 100,
                WordCount = 600,
                Tags = new() { "example", "launch" },
                MarkdownContent =
                    "## Kickoff Deck *(PowerPoint)*\n\n" +
                    "The meeting that started it. Slides are documents too — they get a node like " +
                    "everything else."
            }, cluster);

            void Link(MindMapNode from, MindMapNode to, string label, string color, MindMapLinkStyle style, MindMapLinkDirection dir = MindMapLinkDirection.SourceToTarget)
            {
                doc.Links.Add(new MindMapLink
                {
                    SourceNodeId = from.Id,
                    TargetNodeId = to.Id,
                    Label = label,
                    ColorHex = color,
                    Style = style,
                    Direction = dir,
                    Kind = MindMapLinkKind.Manual
                });
            }

            Link(deck, notes, "kicked off", "#EC4899", MindMapLinkStyle.CurvedBezier);
            Link(notes, brief, "grew into", "#34D399", MindMapLinkStyle.CurvedBezier);
            Link(research, brief, "evidence for", "#22D3EE", MindMapLinkStyle.Dashed);
            Link(research, notes, "quoted in", "#22D3EE", MindMapLinkStyle.Dashed);
            Link(lesson3, cluster, "looks like this →", "#A855F7", MindMapLinkStyle.SynapseGlow, MindMapLinkDirection.SourceToTarget);

            MindMapGraph.Normalize(doc);
            return doc;
        }

        // ---- Mermaid export ----

        /// <summary>
        /// Renders the hierarchy as a Mermaid `mindmap`. Cross-links cannot be expressed in that
        /// syntax — use <see cref="ExportToMermaidFlowchart"/> when the point is the network.
        /// </summary>
        public static string ExportToMermaid(MindMapDocument doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("mindmap");
            if (doc == null || doc.Nodes.Count == 0) return sb.ToString();

            var byId = BuildNodeIndex(doc);
            var root = doc.Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId) ?? doc.Nodes[0];

            var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
            sb.AppendLine($"  root(({MermaidLabel(root)}))");
            AppendMermaidChildren(sb, doc, byId, root, 2, visited);

            // Anything the hierarchy never reached still belongs in the export, otherwise a map
            // with several clusters silently loses all but one of them.
            var stranded = doc.Nodes.Where(n => !visited.Contains(n.Id) && (n.ParentId == null || !visited.Contains(n.ParentId))).ToList();
            if (stranded.Count > 0)
            {
                sb.AppendLine("    Unlinked");
                foreach (var node in stranded)
                {
                    if (!visited.Add(node.Id)) continue;
                    sb.AppendLine($"      {MermaidLabel(node)}");
                    AppendMermaidChildren(sb, doc, byId, node, 4, visited);
                }
            }

            return sb.ToString();
        }

        private static void AppendMermaidChildren(StringBuilder sb, MindMapDocument doc, Dictionary<string, MindMapNode> byId, MindMapNode node, int depth, HashSet<string> visited)
        {
            // `visited` is what stops a hand-edited cycle (A is B's child, B is A's child) from
            // recursing until the stack dies.
            foreach (string childId in node.ChildIds)
            {
                if (!byId.TryGetValue(childId, out var child)) continue;
                if (!visited.Add(child.Id)) continue;
                sb.Append(new string(' ', depth * 2)).AppendLine(MermaidLabel(child));
                AppendMermaidChildren(sb, doc, byId, child, depth + 1, visited);
            }
        }

        /// <summary>
        /// Renders the whole graph — hierarchy *and* cross-links, with their labels — as a Mermaid
        /// flowchart, which is the shape this feature is actually about.
        /// </summary>
        public static string ExportToMermaidFlowchart(MindMapDocument doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("flowchart LR");
            if (doc == null || doc.Nodes.Count == 0) return sb.ToString();

            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            foreach (var node in doc.Nodes)
            {
                string alias = "n" + i++;
                ids[node.Id] = alias;
                string icon = MermaidQuoted(node.Icon);
                if (icon.Length > 0) icon += " ";
                sb.AppendLine($"    {alias}[\"{icon}{MermaidQuoted(node.Title)}\"]");
            }

            foreach (var node in doc.Nodes)
            {
                if (node.ParentId == null || !ids.TryGetValue(node.ParentId, out var parentAlias)) continue;
                sb.AppendLine($"    {parentAlias} --> {ids[node.Id]}");
            }

            foreach (var link in doc.Links)
            {
                if (!ids.TryGetValue(link.SourceNodeId, out var s) || !ids.TryGetValue(link.TargetNodeId, out var t)) continue;
                string arrow = link.Direction == MindMapLinkDirection.Bidirectional ? "<-.->" : "-.->";
                string label = string.IsNullOrWhiteSpace(link.Label) ? "" : $"|\"{MermaidQuoted(link.Label)}\"|";
                sb.AppendLine($"    {s} {arrow}{label} {t}");
            }

            return sb.ToString();
        }

        private static Dictionary<string, MindMapNode> BuildNodeIndex(MindMapDocument doc)
        {
            var byId = new Dictionary<string, MindMapNode>(doc.Nodes.Count, StringComparer.Ordinal);
            foreach (var n in doc.Nodes) byId[n.Id] = n;
            return byId;
        }

        /// <summary>Mermaid's mindmap grammar has no escape mechanism, so bracket characters in a
        /// title have to go rather than produce a diagram that will not parse.</summary>
        private static string MermaidLabel(MindMapNode node)
        {
            string icon = string.IsNullOrWhiteSpace(node.Icon) ? "" : node.Icon.Trim() + " ";
            string title = node.Title ?? "Node";
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
            {
                sb.Append(c switch
                {
                    '(' or ')' or '[' or ']' or '{' or '}' or '"' or '\r' or '\n' or '|' => ' ',
                    _ => c
                });
            }
            string cleaned = sb.ToString().Trim();
            if (cleaned.Length == 0) cleaned = "Node";
            if (cleaned.Length > 60) cleaned = cleaned[..57].TrimEnd() + "…";
            return icon + cleaned;
        }

        private static string MermaidQuoted(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = text.Replace("\"", "#quot;").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > 60) s = s[..57].TrimEnd() + "…";
            return s;
        }
    }

    public sealed record MindMapLoadResult(
        MindMapDocument Document,
        MindMapRepairReport Repairs,
        bool IsFirstRun,
        string? LoadError = null);
}
