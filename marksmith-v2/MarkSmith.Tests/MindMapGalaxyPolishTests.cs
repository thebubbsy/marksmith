using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models.MindMap;
using MarkSmith.Services.MindMap;
using MarkSmith.ViewModels.MindMap;
using Xunit;

namespace MarkSmith.Tests
{
    /// <summary>
    /// Regression cover for the Document Galaxy polish pass: link precedence, graph repair,
    /// persistence, layout geometry and the studio's editing model.
    /// </summary>
    public class MindMapGalaxyPolishTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"marksmith_galaxy_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---- Auto-linker: link precedence ----

        [Fact]
        public async Task ExplicitWikilinkIsNotDisplacedByAnInferredSharedTagLink()
        {
            // The failure this locks down: the linker emitted edges file-by-file and refused to add
            // a second edge between an already-connected pair. Whichever file the OS happened to
            // enumerate first therefore won, and a shared-tag guess between two documents could
            // permanently suppress the [[wikilink]] one of them actually wrote.
            string dir = NewTempDir();
            try
            {
                // "Beta" sorts before "Alpha", so the weak tag pass reaches this pair first.
                await File.WriteAllTextAsync(Path.Combine(dir, "Alpha.md"),
                    "# Alpha\nSee [[Beta]].\n\nTags: #shared #topic");
                await File.WriteAllTextAsync(Path.Combine(dir, "Beta.md"),
                    "# Beta\nNo outgoing links.\n\nTags: #shared #topic");

                var doc = await new MindMapAutoLinker().BuildGalaxyFromDirectoryAsync(dir);

                var alpha = doc.Nodes.Single(n => n.Title == "Alpha");
                var beta = doc.Nodes.Single(n => n.Title == "Beta");
                var link = doc.Links.SingleOrDefault(l =>
                    (l.SourceNodeId == alpha.Id && l.TargetNodeId == beta.Id) ||
                    (l.SourceNodeId == beta.Id && l.TargetNodeId == alpha.Id));

                Assert.NotNull(link);
                Assert.Equal(MindMapLinkKind.WikiLink, link!.Kind);
                Assert.Equal(alpha.Id, link.SourceNodeId);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task TagsAreNotHarvestedFromCodeBlocksOrHexColours()
        {
            string dir = NewTempDir();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "Doc.md"),
                    "# Doc\n\nA real #topic tag.\n\n" +
                    "```css\n.a { color: #FF7C4D; }\n#sidebar { top: 0 }\n```\n\n" +
                    "Inline `#notatag` too, and issue #42, and a link to [x](y.md#anchor).\n");

                var doc = await new MindMapAutoLinker().BuildGalaxyFromDirectoryAsync(dir);
                var node = doc.Nodes.Single(n => n.Title == "Doc");

                Assert.Contains("#topic", node.Tags);
                Assert.DoesNotContain("#FF7C4D", node.Tags);
                Assert.DoesNotContain("#sidebar", node.Tags);
                Assert.DoesNotContain("#notatag", node.Tags);
                Assert.DoesNotContain("#anchor", node.Tags);
                Assert.DoesNotContain(node.Tags, t => t.Contains("42"));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task FrontMatterSuppliesTitleAndTags()
        {
            string dir = NewTempDir();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(dir, "note.md"),
                    "---\ntitle: Quarterly Review\ntags: [finance, q3]\n---\n\n# Ignored Heading\n\nBody.\n");

                var doc = await new MindMapAutoLinker().BuildGalaxyFromDirectoryAsync(dir);
                var node = doc.Nodes.Single(n => n.FileExtension == ".md");

                Assert.Equal("Quarterly Review", node.Title);
                Assert.Contains("#finance", node.Tags);
                Assert.Contains("#q3", node.Tags);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task SubfoldersBecomeFolderNodesRatherThanOneFlatFanOut()
        {
            string dir = NewTempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "research"));
                Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
                await File.WriteAllTextAsync(Path.Combine(dir, "index.md"), "# Index");
                await File.WriteAllTextAsync(Path.Combine(dir, "research", "deep.md"), "# Deep");
                await File.WriteAllTextAsync(Path.Combine(dir, "node_modules", "junk.md"), "# Junk");

                var doc = await new MindMapAutoLinker().BuildGalaxyFromDirectoryAsync(dir);

                var folder = doc.Nodes.SingleOrDefault(n => n.NodeType == MindMapNodeType.Folder);
                Assert.NotNull(folder);
                Assert.Equal("research", folder!.Title);

                var deep = doc.Nodes.Single(n => n.Title == "Deep");
                Assert.Equal(folder.Id, deep.ParentId);

                // Vendored directories are never worth mapping.
                Assert.DoesNotContain(doc.Nodes, n => n.Title == "Junk");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task RelativeLinksResolveFromTheLinkingFilesOwnDirectory()
        {
            string dir = NewTempDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                await File.WriteAllTextAsync(Path.Combine(dir, "sub", "a.md"), "# A\nSee [B](./b.md).");
                await File.WriteAllTextAsync(Path.Combine(dir, "sub", "b.md"), "# B");

                var doc = await new MindMapAutoLinker().BuildGalaxyFromDirectoryAsync(dir);
                var a = doc.Nodes.Single(n => n.Title == "A");
                var b = doc.Nodes.Single(n => n.Title == "B");

                Assert.Contains(doc.Links, l =>
                    l.Kind == MindMapLinkKind.CrossReference && l.SourceNodeId == a.Id && l.TargetNodeId == b.Id);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- Graph repair ----

        [Fact]
        public void NormalizeDropsDanglingAndSelfLinksAndMergesDuplicates()
        {
            var doc = new MindMapDocument
            {
                Nodes =
                {
                    new MindMapNode { Id = "a", Title = "A" },
                    new MindMapNode { Id = "b", Title = "B" }
                },
                Links =
                {
                    new MindMapLink { SourceNodeId = "a", TargetNodeId = "ghost" },
                    new MindMapLink { SourceNodeId = "a", TargetNodeId = "a" },
                    new MindMapLink { SourceNodeId = "a", TargetNodeId = "b", Kind = MindMapLinkKind.SharedTag },
                    new MindMapLink { SourceNodeId = "b", TargetNodeId = "a", Kind = MindMapLinkKind.Manual, Label = "kept" }
                }
            };

            var report = MindMapGraph.Normalize(doc);

            var link = Assert.Single(doc.Links);
            Assert.Equal("kept", link.Label);
            Assert.Equal(1, report.DroppedDanglingLinks);
            Assert.Equal(1, report.DroppedSelfLinks);
            Assert.Equal(1, report.MergedDuplicateLinks);
            Assert.Equal("a", doc.RootNodeId);
        }

        [Fact]
        public void NormalizeBreaksParentCyclesInsteadOfLettingWalksRecurseForever()
        {
            var doc = new MindMapDocument
            {
                RootNodeId = "a",
                Nodes =
                {
                    new MindMapNode { Id = "a", Title = "A", ParentId = "b" },
                    new MindMapNode { Id = "b", Title = "B", ParentId = "a" }
                }
            };

            var report = MindMapGraph.Normalize(doc);

            Assert.True(report.BrokenCycles > 0);
            Assert.Null(doc.Nodes.Single(n => n.Id == "a").ParentId);

            // The Mermaid walk is the thing that used to blow the stack on a map like this.
            string mermaid = MindMapStorageService.ExportToMermaid(doc);
            Assert.Contains("A", mermaid);
            Assert.Contains("B", mermaid);
        }

        [Fact]
        public void NormalizeIsIdempotentOnACleanDocument()
        {
            var doc = MindMapStorageService.CreateTutorialGalaxy();
            Assert.False(MindMapGraph.Normalize(doc).HasRepairs);
        }

        [Fact]
        public void NormalizeRejectsColoursThatAreNotRealHex()
        {
            var doc = new MindMapDocument { Nodes = { new MindMapNode { Id = "a", ColorHex = "notacolor" } } };
            MindMapGraph.Normalize(doc);
            Assert.Equal("#FF7C4D", doc.Nodes[0].ColorHex);

            Assert.Equal("#AABBCC", MindMapGraph.NormalizeHex("#abc", "#000000"));
            Assert.Equal("#112233", MindMapGraph.NormalizeHex("#FF112233", "#000000"));
            Assert.Equal("#000000", MindMapGraph.NormalizeHex("#GGHHII", "#000000"));
        }

        [Fact]
        public void ChildListsAreRebuiltFromParentPointers()
        {
            var doc = new MindMapDocument
            {
                RootNodeId = "root",
                Nodes =
                {
                    new MindMapNode { Id = "root", ChildIds = { "ghost", "kid" } },
                    new MindMapNode { Id = "kid", ParentId = "root" },
                    new MindMapNode { Id = "stray", ParentId = "root" }
                }
            };

            MindMapGraph.Normalize(doc);

            var root = doc.Nodes.Single(n => n.Id == "root");
            Assert.Equal(new[] { "kid", "stray" }, root.ChildIds);
        }

        // ---- Insights ----

        [Fact]
        public void AnalyzeFindsHubsIsolatedNodesAndClusters()
        {
            var doc = new MindMapDocument
            {
                RootNodeId = "hub",
                Nodes =
                {
                    new MindMapNode { Id = "hub", Title = "Hub" },
                    new MindMapNode { Id = "x", Title = "X", ParentId = "hub" },
                    new MindMapNode { Id = "y", Title = "Y", ParentId = "hub" },
                    new MindMapNode { Id = "lonely", Title = "Lonely" }
                }
            };
            MindMapGraph.Normalize(doc);

            var insights = MindMapGraph.Analyze(doc);

            Assert.Equal("hub", insights.Hubs[0].Id);
            Assert.Equal(2, insights.Hubs[0].Degree);
            Assert.Equal(new[] { "lonely" }, insights.IsolatedNodeIds);
            Assert.Equal(2, insights.ClusterCount); // the hub's tree, plus the floater
        }

        [Fact]
        public void NeighborsOfSpansBothHierarchyAndCrossLinks()
        {
            var doc = new MindMapDocument
            {
                RootNodeId = "a",
                Nodes =
                {
                    new MindMapNode { Id = "a" },
                    new MindMapNode { Id = "b", ParentId = "a" },
                    new MindMapNode { Id = "c" },
                    new MindMapNode { Id = "d" }
                },
                Links = { new MindMapLink { SourceNodeId = "c", TargetNodeId = "a" } }
            };
            MindMapGraph.Normalize(doc);

            var neighbors = MindMapGraph.NeighborsOf(doc, "a");
            Assert.Equal(new[] { "b", "c" }, neighbors.OrderBy(x => x));
        }

        // ---- Layout ----

        [Fact]
        public void DeepTreesArePlacedRelativeToTheirRealParentPosition()
        {
            // Each level used to be laid out from the parent's PREVIOUS coordinates, because the
            // parent was positioned only after its subtree had been walked. Anything below depth 2
            // landed in the wrong place, usually overlapping its grandparent.
            var doc = new MindMapDocument { RootNodeId = "r" };
            doc.Nodes.Add(new MindMapNode { Id = "r", Width = 200, Height = 60, X = 9999, Y = 9999 });
            doc.Nodes.Add(new MindMapNode { Id = "c1", ParentId = "r", Width = 200, Height = 60, X = -5000, Y = -5000 });
            doc.Nodes.Add(new MindMapNode { Id = "g1", ParentId = "c1", Width = 200, Height = 60, X = -5000, Y = -5000 });
            doc.Nodes.Add(new MindMapNode { Id = "gg1", ParentId = "g1", Width = 200, Height = 60, X = -5000, Y = -5000 });
            MindMapGraph.Normalize(doc);

            new MindMapLayoutEngine().ApplyLayout(doc, MindMapLayoutType.HorizontalTree);

            var byId = doc.Nodes.ToDictionary(n => n.Id);
            foreach (var (child, parent) in new[] { ("c1", "r"), ("g1", "c1"), ("gg1", "g1") })
            {
                Assert.True(byId[child].X > byId[parent].X + byId[parent].Width,
                    $"{child} should sit to the right of {parent}");
            }
        }

        [Fact]
        public void ForceLayoutSeparatesNodesThatAllStartAtTheOrigin()
        {
            // An imported vault arrives with every node at (0,0). The repulsion vector between two
            // coincident points is exactly zero, so the simulation used to run its full iteration
            // count and move nothing at all.
            var doc = new MindMapDocument { RootNodeId = "r" };
            doc.Nodes.Add(new MindMapNode { Id = "r", Width = 180, Height = 56 });
            for (int i = 0; i < 12; i++)
            {
                doc.Nodes.Add(new MindMapNode { Id = $"n{i}", ParentId = "r", Width = 180, Height = 56 });
            }
            MindMapGraph.Normalize(doc);

            new MindMapLayoutEngine().ApplyLayout(doc, MindMapLayoutType.ForceDirected);

            var positions = doc.Nodes.Select(n => (Math.Round(n.X, 3), Math.Round(n.Y, 3))).ToList();
            Assert.Equal(positions.Count, positions.Distinct().Count());
            Assert.All(doc.Nodes, n =>
            {
                Assert.True(double.IsFinite(n.X) && double.IsFinite(n.Y));
            });
            AssertNoOverlaps(doc);
        }

        [Theory]
        [InlineData(MindMapLayoutType.HorizontalTree)]
        [InlineData(MindMapLayoutType.VerticalHierarchy)]
        [InlineData(MindMapLayoutType.RadialGalaxy)]
        [InlineData(MindMapLayoutType.ForceDirected)]
        [InlineData(MindMapLayoutType.ConstellationClusters)]
        public void EveryLayoutPlacesOrphansInsteadOfLeavingThemAtStaleCoordinates(MindMapLayoutType layout)
        {
            var doc = MindMapStorageService.CreateTutorialGalaxy();
            doc.Nodes.Add(new MindMapNode { Id = "orphan", Title = "Orphan", X = 50000, Y = 50000, Width = 180, Height = 56 });
            MindMapGraph.Normalize(doc);

            new MindMapLayoutEngine().ApplyLayout(doc, layout);

            var orphan = doc.Nodes.Single(n => n.Id == "orphan");
            Assert.True(Math.Abs(orphan.X) < 20000 && Math.Abs(orphan.Y) < 20000,
                $"{layout} left the orphan at ({orphan.X}, {orphan.Y})");
        }

        [Fact]
        public void ClusterLayoutKeepsSeparateIslandsApart()
        {
            var doc = new MindMapDocument { RootNodeId = "a1" };
            foreach (string prefix in new[] { "a", "b" })
            {
                doc.Nodes.Add(new MindMapNode { Id = prefix + "1", Width = 180, Height = 56 });
                for (int i = 2; i <= 5; i++)
                {
                    doc.Nodes.Add(new MindMapNode { Id = prefix + i, ParentId = prefix + "1", Width = 180, Height = 56 });
                }
            }
            MindMapGraph.Normalize(doc);

            new MindMapLayoutEngine().ApplyLayout(doc, MindMapLayoutType.ConstellationClusters);

            AssertNoOverlaps(doc);
        }

        private static void AssertNoOverlaps(MindMapDocument doc)
        {
            var nodes = doc.Nodes.ToList();
            for (int i = 0; i < nodes.Count; i++)
            {
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    var a = nodes[i];
                    var b = nodes[j];
                    bool apart = a.X + a.Width <= b.X || b.X + b.Width <= a.X ||
                                 a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y;
                    Assert.True(apart, $"'{a.Id}' and '{b.Id}' overlap after layout");
                }
            }
        }

        // ---- Storage & the first-run tour ----

        [Fact]
        public async Task StudioReopensTheSavedGalaxyInsteadOfTheTour()
        {
            // The headline defect: nothing ever called Load. The studio rebuilt the sample map in
            // its constructor on every open, so a saved library was written and never read back.
            string path = Path.Combine(NewTempDir(), "library.msmap");
            try
            {
                var mine = new MindMapDocument { Title = "My Real Vault", RootNodeId = "r" };
                mine.Nodes.Add(new MindMapNode { Id = "r", Title = "My Root" });
                await new MindMapStorageService().SaveAsync(mine, path);

                var vm = new MindMapStudioViewModel();
                Assert.True(vm.IsTutorialActive); // fresh instance shows the tour

                await vm.InitializeAsync(path);

                Assert.False(vm.IsTutorialActive);
                Assert.Equal("My Real Vault", vm.Title);
                Assert.Equal("My Root", Assert.Single(vm.Nodes).Title);
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        }

        [Fact]
        public async Task FirstRunFallsBackToTheGuidedTour()
        {
            string dir = NewTempDir();
            try
            {
                var vm = new MindMapStudioViewModel();
                await vm.InitializeAsync(Path.Combine(dir, "nothing-here.msmap"));

                Assert.True(vm.IsTutorialActive);
                Assert.All(vm.Nodes, n => Assert.True(n.Model.IsTutorial));
                Assert.Contains(vm.Links, l => l.DisplayLabel == "grew into");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task ACorruptLibraryIsQuarantinedRatherThanOverwritten()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "library.msmap");
            try
            {
                await File.WriteAllTextAsync(path, "{ this is not json");

                var result = await new MindMapStorageService().LoadWithReportAsync(path);

                Assert.NotNull(result.LoadError);
                Assert.True(result.IsFirstRun);
                Assert.True(File.Exists(path + ".corrupt"), "the unreadable file must be preserved");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task SavingClearsTheTutorialFlagSoTheTourNeverComesBack()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "library.msmap");
            try
            {
                var vm = new MindMapStudioViewModel();
                Assert.True(vm.IsTutorialActive);

                await vm.SaveAsync(path);
                Assert.False(vm.IsTutorialActive);

                var reopened = new MindMapStudioViewModel();
                await reopened.InitializeAsync(path);
                Assert.False(reopened.IsTutorialActive);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void ClearingTheTourKeepsTheUsersOwnNodes()
        {
            var vm = new MindMapStudioViewModel();
            vm.AddRootNode();
            var mine = vm.SelectedNode!;
            mine.Title = "Mine";

            vm.ClearTutorial();

            Assert.False(vm.IsTutorialActive);
            var survivor = Assert.Single(vm.Nodes);
            Assert.Equal("Mine", survivor.Title);
        }

        // ---- Mermaid ----

        [Fact]
        public void MermaidLabelsAreSanitizedSoTheDiagramStillParses()
        {
            var doc = new MindMapDocument { RootNodeId = "r" };
            doc.Nodes.Add(new MindMapNode { Id = "r", Title = "Root (v2) [draft]", Icon = "🌌" });
            MindMapGraph.Normalize(doc);

            string mermaid = MindMapStorageService.ExportToMermaid(doc);

            Assert.StartsWith("mindmap", mermaid.TrimStart());
            string rootLine = mermaid.Split('\n').First(l => l.Contains("Root"));
            Assert.DoesNotContain("(v2)", rootLine);
            Assert.DoesNotContain("[draft]", rootLine);
        }

        [Fact]
        public void FlowchartExportCarriesCrossLinksAndTheirLabels()
        {
            var doc = MindMapStorageService.CreateTutorialGalaxy();

            string flow = MindMapStorageService.ExportToMermaidFlowchart(doc);

            Assert.StartsWith("flowchart LR", flow.TrimStart());
            Assert.Contains("grew into", flow);
            Assert.Contains("-.->", flow);
        }

        [Fact]
        public void MermaidExportIncludesNodesOutsideTheRootsTree()
        {
            var doc = MindMapStorageService.CreateTutorialGalaxy();
            doc.Nodes.Add(new MindMapNode { Id = "island", Title = "Detached Island" });
            MindMapGraph.Normalize(doc);

            Assert.Contains("Detached Island", MindMapStorageService.ExportToMermaid(doc));
        }

        // ---- Studio editing ----

        [Fact]
        public void UndoAndRedoRestoreStructuralEdits()
        {
            var vm = new MindMapStudioViewModel();
            int baseline = vm.Nodes.Count;

            vm.AddChildNode();
            Assert.Equal(baseline + 1, vm.Nodes.Count);
            Assert.True(vm.CanUndo);

            vm.Undo();
            Assert.Equal(baseline, vm.Nodes.Count);
            Assert.True(vm.CanRedo);

            vm.Redo();
            Assert.Equal(baseline + 1, vm.Nodes.Count);
        }

        [Fact]
        public void DeletingANodeAdoptsItsChildrenIntoTheGrandparent()
        {
            var vm = new MindMapStudioViewModel();
            var root = vm.Nodes.First(n => n.Id == vm.Document.RootNodeId);

            vm.SelectedNode = root;
            vm.AddChildNode();
            var middle = vm.SelectedNode!;
            vm.AddChildNode();
            var leaf = vm.SelectedNode!;

            vm.DeleteNode(middle);

            Assert.DoesNotContain(vm.Nodes, n => n.Id == middle.Id);
            Assert.Contains(vm.Nodes, n => n.Id == leaf.Id);
            Assert.Equal(root.Id, vm.Nodes.First(n => n.Id == leaf.Id).ParentId);
        }

        [Fact]
        public void SelectingANodeClearsTheLinkSelectionSoDeleteHitsTheHighlightedThing()
        {
            var vm = new MindMapStudioViewModel();
            vm.SelectedLink = vm.Links.First();
            Assert.Null(vm.SelectedNode);

            vm.SelectedNode = vm.Nodes.First();
            Assert.Null(vm.SelectedLink);
        }

        [Fact]
        public void NodeAndLinkCountersTrackTheCollections()
        {
            var vm = new MindMapStudioViewModel();
            string before = vm.NodesCountText;

            vm.AddRootNode();

            Assert.NotEqual(before, vm.NodesCountText);
            Assert.Equal($"{vm.Nodes.Count} Nodes", vm.NodesCountText);
        }

        [Fact]
        public void ReparentingIntoOwnBranchIsRefused()
        {
            var vm = new MindMapStudioViewModel();
            var root = vm.Nodes.First(n => n.Id == vm.Document.RootNodeId);
            vm.SelectedNode = root;
            vm.AddChildNode();
            var child = vm.SelectedNode!;

            Assert.False(vm.ReparentNode(root.Id, child.Id));
            Assert.Null(vm.Document.Nodes.First(n => n.Id == root.Id).ParentId);
        }

        [Fact]
        public void RelinkingAnExistingPairRenamesTheRelationship()
        {
            var vm = new MindMapStudioViewModel();
            var a = vm.Nodes[0];
            var b = vm.Nodes[1];
            int before = vm.Links.Count;

            vm.ConnectNodes(a.Id, b.Id, "first reason");
            Assert.Equal(before + 1, vm.Links.Count);

            vm.ConnectNodes(a.Id, b.Id, "better reason");
            Assert.Equal(before + 1, vm.Links.Count);
            Assert.Contains(vm.Links, l => l.Label == "better reason");
        }

        [Fact]
        public void DuplicatingANodeRegistersItWithItsParent()
        {
            var vm = new MindMapStudioViewModel();
            vm.SelectedNode = vm.Nodes.First(n => n.Id == vm.Document.RootNodeId);
            vm.AddChildNode();
            var child = vm.SelectedNode!;

            vm.DuplicateSelectedNode();
            var clone = vm.SelectedNode!;

            Assert.NotEqual(child.Id, clone.Id);
            var parent = vm.Document.Nodes.First(n => n.Id == clone.ParentId);
            Assert.Contains(clone.Id, parent.ChildIds);
        }

        [Fact]
        public void FocusModeDimsEverythingOutsideTheSelectionsConstellation()
        {
            var vm = new MindMapStudioViewModel();
            var root = vm.Nodes.First(n => n.Id == vm.Document.RootNodeId);
            vm.SelectedNode = root;
            vm.IsFocusModeEnabled = true;

            var neighbors = MindMapGraph.NeighborsOf(vm.Document, root.Id);
            Assert.NotEmpty(neighbors);

            foreach (var n in vm.Nodes)
            {
                bool connected = n.Id == root.Id || neighbors.Contains(n.Id);
                Assert.Equal(!connected, n.IsDimmed);
            }
        }

        [Fact]
        public void SearchReachesTagsAndFilePathsNotJustTitles()
        {
            var vm = new MindMapStudioViewModel();
            var doc = new MindMapDocument
            {
                RootNodeId = "1",
                Nodes =
                {
                    new MindMapNode { Id = "1", Title = "Alpha", FilePath = @"C:\vault\budget-2026.md" },
                    new MindMapNode { Id = "2", Title = "Beta", Tags = { "quarterly" } },
                    new MindMapNode { Id = "3", Title = "Gamma" }
                }
            };
            vm.LoadDocument(doc);

            vm.SearchQuery = "budget";
            Assert.Equal(1, vm.SearchMatchCount);
            Assert.False(vm.Nodes.First(n => n.Id == "1").IsDimmed);

            vm.SearchQuery = "quarterly";
            Assert.Equal(1, vm.SearchMatchCount);
            Assert.False(vm.Nodes.First(n => n.Id == "2").IsDimmed);
        }

        [Fact]
        public void SearchCyclesThroughEveryMatchInsteadOfSnappingBackToTheFirst()
        {
            var vm = new MindMapStudioViewModel();
            var doc = new MindMapDocument
            {
                RootNodeId = "1",
                Nodes =
                {
                    new MindMapNode { Id = "1", Title = "Report A" },
                    new MindMapNode { Id = "2", Title = "Report B" },
                    new MindMapNode { Id = "3", Title = "Unrelated" }
                }
            };
            vm.LoadDocument(doc);
            vm.SearchQuery = "Report";

            Assert.Equal("1", vm.FocusNextMatch()!.Id);
            Assert.Equal("2", vm.FocusNextMatch()!.Id);
            Assert.Equal("1", vm.FocusNextMatch()!.Id); // wraps
        }

        [Fact]
        public void TagFilteringAcceptsEitherSpellingOfATag()
        {
            var vm = new MindMapStudioViewModel();
            vm.LoadDocument(new MindMapDocument
            {
                RootNodeId = "1",
                Nodes =
                {
                    new MindMapNode { Id = "1", Title = "Tagged", Tags = { "api" } },
                    new MindMapNode { Id = "2", Title = "Untagged" }
                }
            });

            vm.SelectedTagFilter = "#api";
            Assert.False(vm.Nodes.First(n => n.Id == "1").IsDimmed);
            Assert.True(vm.Nodes.First(n => n.Id == "2").IsDimmed);
        }

        [Fact]
        public void ConnectionCountsFollowLinkEdits()
        {
            var vm = new MindMapStudioViewModel();
            var a = vm.Nodes[0];
            int before = a.ConnectionCount;

            vm.ConnectNodes(a.Id, vm.Nodes[^1].Id, "test");
            Assert.Equal(before + 1, a.ConnectionCount);
        }

        [Fact]
        public void TypingAPathIntoTheInspectorUpdatesTheFormatBadge()
        {
            var vm = new MindMapStudioViewModel();
            var node = vm.Nodes.First();

            node.FilePath = @"C:\vault\proposal.DOCX";

            Assert.Equal("DOCX", node.FormatBadge);
            Assert.Equal("proposal.DOCX", node.FileName);
        }
    }
}
