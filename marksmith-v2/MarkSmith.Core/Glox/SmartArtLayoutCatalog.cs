using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Core.AST;
using MarkSmith.Core.Resolver;

namespace MarkSmith.Core.Glox
{
    /// <summary>
    /// Central, amendable registry of the real embedded Office SmartArt layouts.
    ///
    /// Each entry is a genuine <c>.glox</c> package (layout + quick style + colors) whose
    /// <c>layout1.xml</c> declares the algorithm Word uses to compute geometry (cycle,
    /// hierChild, linear, grid, pyramid, venn, composite, ...). Because Word renders
    /// geometry *from the layout's algorithm*, embedding the real package is what makes each
    /// type render distinctly instead of collapsing to basic blocks.
    ///
    /// This is the extension seam for the product promise: drop a new <c>.glox</c> into
    /// <c>Resources/EmbeddedGlox/</c> and register one alias, and both the DOCX export path
    /// and the designer pick it up automatically.
    /// </summary>
    public sealed class SmartArtLayoutCatalog : IGloxLayoutSource
    {
        private readonly Dictionary<string, GloxPackage> _byAlias = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GloxPackage> _byUrn = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<GloxPackage> _all = new();

        private static SmartArtLayoutCatalog? _shared;

        /// <summary>Process-wide shared catalog (lazy, single-load).</summary>
        public static SmartArtLayoutCatalog Shared =>
            _shared ??= new SmartArtLayoutCatalog();

        /// <summary>
        /// The Office default document theme (accent1..accent6 etc.). SmartArt colors resolve
        /// against it, so exports carry it so diagrams render colored in Word.
        /// </summary>
        public string ThemeXml { get; private set; } = string.Empty;

        /// <summary>Shared native quickStyle + colors (all Office gallery styles share these two).</summary>
        public string SharedQuickStyleXml { get; private set; } = string.Empty;
        public string SharedColorsXml { get; private set; } = string.Empty;

        public IReadOnlyList<GloxPackage> All => _all;
        internal Dictionary<string, string> AliasToUrn { get; } = new(StringComparer.OrdinalIgnoreCase);

        private SmartArtLayoutCatalog()
        {
            LoadSharedParts();
            LoadEmbeddedPackages();
            RegisterAliases();
        }

        private static string? ReadResource(string nameSuffix)
        {
            var asm = typeof(GloxExtractor).Assembly;
            string? fullName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(nameSuffix, StringComparison.OrdinalIgnoreCase));
            if (fullName == null) return null;
            using var stream = asm.GetManifestResourceStream(fullName);
            if (stream == null) return null;
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }

        private void LoadSharedParts()
        {
            SharedQuickStyleXml = ReadResource("EmbeddedGlox.shared.quickstyle.xml") ?? string.Empty;
            SharedColorsXml = ReadResource("EmbeddedGlox.shared.colors.xml") ?? string.Empty;
            ThemeXml = ReadResource("EmbeddedGlox.theme.xml") ?? string.Empty;
        }

        private void LoadEmbeddedPackages()
        {
            var asm = typeof(GloxExtractor).Assembly;
            var resourceNames = asm.GetManifestResourceNames()
                .Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                            && n.Contains("EmbeddedGlox.", StringComparison.OrdinalIgnoreCase)
                            // Sidecars are not layouts: shared style/colors/theme + per-layout pairs.
                            && !n.EndsWith(".quickstyle.xml", StringComparison.OrdinalIgnoreCase)
                            && !n.EndsWith(".colors.xml", StringComparison.OrdinalIgnoreCase)
                            && !n.EndsWith("theme.xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (var name in resourceNames)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;

                try
                {
                    using var reader = new System.IO.StreamReader(stream);
                    string layoutXml = reader.ReadToEnd();
                    // The .xml files are the readable, schema-valid layout definitions
                    // (the single source of truth). Parse them directly.
                    var pkg = GloxExtractor.ExtractFromXmlString(layoutXml);
                    pkg.LayoutXml = layoutXml;
                    if (string.IsNullOrWhiteSpace(pkg.UniqueId)) continue;
                    if (!string.IsNullOrWhiteSpace(pkg.Title)) _byUrn[pkg.Title] = pkg;
                    _byUrn[pkg.UniqueId] = pkg;

                    // Attach the shared native quickStyle + colors so Word paints real
                    // colors instead of rendering grayscale (empty style parts).
                    if (string.IsNullOrWhiteSpace(pkg.StyleXml))
                    {
                        pkg.StyleXml = SharedQuickStyleXml;
                        pkg.StyleUniqueId = ExtractUniqueId(SharedQuickStyleXml);
                    }
                    if (string.IsNullOrWhiteSpace(pkg.ColorXml))
                    {
                        pkg.ColorXml = SharedColorsXml;
                        pkg.ColorUniqueId = ExtractUniqueId(SharedColorsXml);
                    }

                    _all.Add(pkg);
                }
                catch (Exception ex)
                {
                    // Never let one malformed package break the whole catalog.
                    System.Diagnostics.Debug.WriteLine(
                        $"SmartArtLayoutCatalog: failed to load '{name}': {ex.Message}");
                }
            }
        }

        private static string ExtractUniqueId(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
            try
            {
                return System.Xml.Linq.XDocument.Parse(xml).Root?.Attribute("uniqueId")?.Value ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RegisterAliases()
        {
            // Explicit alias map so a friendly name (and the designer's palette/ledger
            // aliases) always resolves to a real package. Alias -> authoritative Microsoft
            // Office URN tail (the 176 native layouts from the corpus carry these), e.g.
            // "hierarchy" -> urn:microsoft.com/office/officeart/2005/8/layout/orgChart1.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hierarchy"] = "orgChart1",
                ["orgchart"]  = "orgChart1",
                ["org"]       = "orgChart1",
                ["tree"]      = "orgChart1",
                ["hier"]      = "orgChart1",

                ["process"]      = "process1",
                ["step_process"] = "process1",
                ["workflow"]     = "process1",
                ["target"]       = "process1",

                ["cycle"]       = "cycle1",
                ["multi_cycle"] = "cycle1",

                ["matrix"]      = "matrix1",
                ["grid_matrix"] = "matrix1",
                ["grid"]        = "matrix1",

                ["pyramid"]  = "pyramid1",
                ["venn"]     = "venn1",

                ["picturelist"] = "pList1",
                ["picture"]     = "pList1",
                ["mosaic"]      = "pList1",

                ["relationship"] = "CircleRelationship",
                ["composite"]    = "CircleRelationship",

                ["list"]    = "default",
                ["default"] = "default",
                ["basic"]   = "default",
            };

            foreach (var kvp in map)
            {
                var pkg = FindByUrnSuffix(kvp.Value);
                if (pkg != null) _byAlias[kvp.Key] = pkg;
            }

            foreach (var kvp in map)
            {
                string? urn = FindByUrnSuffix(kvp.Value)?.UniqueId;
                if (urn != null) AliasToUrn[kvp.Key] = urn;
            }
        }

        private GloxPackage? FindByUrnSuffix(string suffix)
        {
            return _all.FirstOrDefault(p =>
                p.UniqueId.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase) ||
                p.UniqueId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves an alias, title, or full URN to a real embedded layout package.
        /// Returns null (never throws) so callers can fall back gracefully.
        /// </summary>
        public GloxPackage? TryResolve(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string normalized = input.Trim();

            if (_byAlias.TryGetValue(normalized, out var byAlias)) return byAlias;
            if (_byUrn.TryGetValue(normalized, out var byUrn)) return byUrn;

            // Tail match against real URNs.
            var tail = _all.FirstOrDefault(p =>
                p.UniqueId.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
                p.UniqueId.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
            if (tail != null) return tail;

            return null;
        }

        /// <summary>Resolves with a hard guarantee, throwing if unknown (mirrors UrnResolver).</summary>
        public GloxPackage Resolve(string input)
        {
            return TryResolve(input)
                ?? throw new UrnResolutionException(
                    $"Failed to resolve SmartArt layout for '{input}'. Zero-fallback guarantee strictly enforced.");
        }

        /// <summary>
        /// Registers a package imported at runtime (e.g. user-imported .glox) so the
        /// designer gallery, DOCX export, and resolver pick it up immediately.
        /// </summary>
        public void RegisterPackage(GloxPackage pkg, string? alias = null)
        {
            if (pkg == null || string.IsNullOrWhiteSpace(pkg.UniqueId)) return;

            if (!_all.Contains(pkg)) _all.Add(pkg);
            _byUrn[pkg.UniqueId] = pkg;
            if (!string.IsNullOrWhiteSpace(pkg.Title)) _byUrn[pkg.Title] = pkg;
            if (!string.IsNullOrWhiteSpace(alias)) _byAlias[alias] = pkg;
        }
    }

    /// <summary>Seam so consumers can depend on "any layout source" without binding to the static catalog.</summary>
    public interface IGloxLayoutSource
    {
        IReadOnlyList<GloxPackage> All { get; }
        GloxPackage? TryResolve(string? input);
    }

    /// <summary>
    /// Intelligent content-shape classifier: analyzes document structure, hierarchy depth,
    /// semantic keywords, and sequential patterns to recommend the optimal SmartArt FAMILY:
    /// "list", "hierarchy", "process", "cycle", "matrix", "pyramid", "venn", "relationship", "picturelist".
    /// Returns null when the content has no list structure at all.
    /// </summary>
    public static class SmartArtLayoutSuggester
    {
        public static string? Suggest(CanonicalAst ast)
        {
            if (ast == null || ast.Root == null) return null;

            var items = CollectListItems(ast.Root);
            if (items.Count == 0) return null;

            // 1. Picture list: items with embedded images or image paths.
            if (items.Any(n => n.NodeType == AstNodeType.Image || !string.IsNullOrEmpty(n.ImagePath)))
                return "picturelist";

            var rootText = (ast.Root.Text ?? "").ToLowerInvariant();

            // ONE pass over the items computes every marker count/flag the family checks below
            // need — the per-family predicates used to re-scan the whole list with their own
            // regexes each (up to seven scans per preview render), and the Venn check joined
            // every item's text into one big string just to run three Contains on it.
            int swotCount = 0, tierCount = 0, cycleCount = 0, seqMarked = 0, topLevelCount = 0;
            bool anyTopLevelSwot = false, hasDesirable = false, hasFeasible = false, hasViable = false;
            foreach (var n in items)
            {
                if (n.Depth == 1) topLevelCount++;
                if (SwotMarker.IsMatch(n.Text)) { swotCount++; if (n.Depth == 1) anyTopLevelSwot = true; }
                if (TierMarker.IsMatch(n.Text)) tierCount++;
                if (CycleMarker.IsMatch(n.Text)) cycleCount++;
                if (SequentialMarker.IsMatch(n.Text) || TimelineYearMarker.IsMatch(n.Text) || n.Text.Contains("->") || n.Text.Contains("=>")) seqMarked++;
                if (!hasDesirable && n.Text.Contains("desirable", StringComparison.OrdinalIgnoreCase)) hasDesirable = true;
                if (!hasFeasible && n.Text.Contains("feasible", StringComparison.OrdinalIgnoreCase)) hasFeasible = true;
                if (!hasViable && n.Text.Contains("viable", StringComparison.OrdinalIgnoreCase)) hasViable = true;
            }

            // 2. Matrix / SWOT / 2x2 Grid detection.
            if (rootText.Contains("matrix") || rootText.Contains("swot") || rootText.Contains("quadrant") || rootText.Contains("2x2")
                || swotCount >= 2
                || (topLevelCount == 4 && (swotCount >= 1 || anyTopLevelSwot)))
                return "matrix";

            // 3. Pyramid / Layered / Tiered hierarchy detection.
            if (rootText.Contains("pyramid") || rootText.Contains("maslow") || rootText.Contains("hierarchy of needs") || rootText.Contains("tiered") || rootText.Contains("layers")
                || (tierCount >= 2 && tierCount >= items.Count / 2.0))
                return "pyramid";

            // 4. Cycle / Continuous loop / Feedback loop detection.
            bool looksLikeCycle = rootText.Contains("cycle") || rootText.Contains("loop") || rootText.Contains("circular") || rootText.Contains("lifecycle") || rootText.Contains("feedback") || rootText.Contains("pdca")
                || (cycleCount >= 2 && items.Count >= 3 && items.Count <= 8);
            if (!looksLikeCycle && items.Count >= 3)
            {
                // Last item referencing repeating or looping back.
                var lastText = items[items.Count - 1].Text.ToLowerInvariant();
                if (lastText.Contains("repeat") || lastText.Contains("iterate") || lastText.Contains("loop") || lastText.Contains("restart") || lastText.Contains("continuous"))
                    looksLikeCycle = true;
            }
            if (looksLikeCycle)
                return "cycle";

            // 5. Venn / Overlapping / Relationship detection.
            if (rootText.Contains("venn") || rootText.Contains("intersection") || rootText.Contains("overlap")
                || (hasDesirable && hasFeasible && hasViable))
                return "venn";

            if (rootText.Contains("relationship") || rootText.Contains("interconnect") || rootText.Contains("ecosystem") || rootText.Contains("hub and spoke") || rootText.Contains("radial"))
                return "relationship";

            // 6. Process / Workflow / Timeline / Sequential steps.
            if (rootText.Contains("process") || rootText.Contains("workflow") || rootText.Contains("timeline") || rootText.Contains("roadmap") || rootText.Contains("pipeline") || rootText.Contains("funnel")
                || (seqMarked >= 2 && seqMarked >= items.Count / 2.0))
                return "process";

            // 7. Hierarchy / Org Chart: structured nested trees (depth >= 2 or parent-child branches).
            if (items.Any(n => n.Children.Count > 0) || items.Max(n => n.Depth) >= 2)
                return "hierarchy";

            // 8. Plain flat bullet list (skills, features, checklist) -> flat snake layout.
            return "list";
        }

        private static List<AstNode> CollectListItems(AstNode root)
        {
            var result = new List<AstNode>();
            var queue = new Queue<AstNode>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (node != root && node.Depth >= 1) result.Add(node);
                foreach (var child in node.Children) queue.Enqueue(child);
            }
            return result;
        }

        private static readonly Regex SequentialMarker = new(
            @"^\s*(\d{1,2}[.)]|Step\b|Phase\b|Stage\b|Part\b|First\b|Next\b|Then\b|After\b|Finally\b|Last\b|Sprint\s*\d+|Q[1-4]\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TimelineYearMarker = new(
            @"\b(19\d{2}|20\d{2})\b|^\s*(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TierMarker = new(
            @"^\s*(Tier\s*\d+|Level\s*\d+|Layer\s*\d+|Top\b|Middle\b|Bottom\b|Apex\b|Foundation\b|Strategic\b|Tactical\b|Operational\b|Vision\b|Strategy\b|Execution\b|Self-Actualization|Esteem|Belonging|Safety|Physiological)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SwotMarker = new(
            @"\b(Strengths?|Weakness(es)?|Opportunit(y|ies)|Threats?|Quadrants?(\s*[1-4])?|Q[1-4]\b|Urgent|Important)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CycleMarker = new(
            @"\b(Plan\b|Do\b|Check\b|Act\b|Repeats?|Iterat(e|ion|ive)|Feedbacks?|Loops?|Continuous(ly)?|Recurring|Lifecycles?|Rotat(e|ion)|Cycles?|Retrospectives?|Reflect(ion)?|↺)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

