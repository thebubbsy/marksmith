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
        [InlineData(MindMapLayoutType.RightToLeftTree)]
        [InlineData(MindMapLayoutType.VerticalHierarchy)]
        [InlineData(MindMapLayoutType.BottomUpHierarchy)]
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
            Assert.Equal($"{vm.Nodes.Count} nodes", vm.NodesCountText);
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

        // ---- Rendering-cost regressions ----

        [Fact]
        public void MissingFileStateIsCachedRatherThanProbedOnEveryRead()
        {
            // This was a computed property that called File.Exists on every get, and the canvas
            // reads it for every node on every frame. Caching is the point: the value must NOT
            // change until something asks it to.
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "later.md");
                var node = new MindMapNodeViewModel(new MindMapNode { Title = "N", FilePath = path });

                node.RefreshFileState();
                Assert.True(node.IsFileMissing);

                File.WriteAllText(path, "# now it exists");
                Assert.True(node.IsFileMissing); // still cached — no disk access on read

                node.RefreshFileState();
                Assert.False(node.IsFileMissing);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void SettingAFilePathRefreshesTheMissingMarker()
        {
            string dir = NewTempDir();
            try
            {
                string real = Path.Combine(dir, "real.md");
                File.WriteAllText(real, "# real");

                var node = new MindMapNodeViewModel(new MindMapNode { Title = "N" });
                Assert.False(node.IsFileMissing);

                node.FilePath = Path.Combine(dir, "ghost.md");
                Assert.True(node.IsFileMissing);

                node.FilePath = real;
                Assert.False(node.IsFileMissing);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task LoadingAGalaxySweepsFileStateForEveryNode()
        {
            string dir = NewTempDir();
            try
            {
                string real = Path.Combine(dir, "here.md");
                File.WriteAllText(real, "# here");

                var vm = new MindMapStudioViewModel();
                vm.LoadDocument(new MindMapDocument
                {
                    RootNodeId = "1",
                    Nodes =
                    {
                        new MindMapNode { Id = "1", Title = "Present", FilePath = real },
                        new MindMapNode { Id = "2", Title = "Gone", FilePath = Path.Combine(dir, "gone.md") },
                        new MindMapNode { Id = "3", Title = "No file" }
                    }
                });

                await vm.RefreshFileStatesAsync();

                Assert.False(vm.Nodes.First(n => n.Id == "1").IsFileMissing);
                Assert.True(vm.Nodes.First(n => n.Id == "2").IsFileMissing);
                Assert.False(vm.Nodes.First(n => n.Id == "3").IsFileMissing);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void HoverPreviewIsExcerptedRatherThanRenderingTheWholeDocument()
        {
            // The auto-linker stores up to 20k characters per node, and the hover card fires every
            // time the pointer crosses a card. Throwing all of it at a TextBlock was a stutter on
            // its own.
            var vm = new MindMapStudioViewModel();
            string huge = string.Join("\n\n", Enumerable.Range(0, 800).Select(i => $"Paragraph {i} with some body text."));
            var node = new MindMapNodeViewModel(new MindMapNode { Title = "Big", MarkdownContent = huge });

            vm.ShowPreviewCard(node);

            Assert.True(vm.PreviewMarkdown.Length < huge.Length / 4,
                $"preview was {vm.PreviewMarkdown.Length} chars of {huge.Length}");
            Assert.Contains("Paragraph 0", vm.PreviewMarkdown);
            Assert.Contains("open the document", vm.PreviewMarkdown);
        }

        [Fact]
        public void ShortNotesArePreviewedInFull()
        {
            var vm = new MindMapStudioViewModel();
            var node = new MindMapNodeViewModel(new MindMapNode { Title = "Small", MarkdownContent = "# Small\n\nAll of it." });

            vm.ShowPreviewCard(node);

            Assert.Equal("# Small\n\nAll of it.", vm.PreviewMarkdown);
            Assert.DoesNotContain("open the document", vm.PreviewMarkdown);
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

        [Fact]
        public void ApplyLayout_AllStandardAlgorithms_UpdateNodePositionsAndMarkDirty()
        {
            var vm = new MindMapStudioViewModel();
            vm.AddChildNodeCommand.Execute(null);
            vm.AddSiblingNodeCommand.Execute(null);

            string[] layouts = { "tree", "vertical", "radial", "clusters", "force" };
            foreach (var layout in layouts)
            {
                vm.ApplyLayout(layout);
                Assert.NotNull(vm.StatusMessage);
                Assert.Contains("Arranged", vm.StatusMessage);
            }
        }

        // ---- Subtree Markdown Export Tests ----

        [Fact]
        public void ExportSubtreeToMarkdown_NullSelection_ReturnsEmpty()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            vm.SelectedNode = null;
            var md = vm.ExportSubtreeToMarkdown(null);
            Assert.Equal(string.Empty, md);
        }

        [Fact]
        public void ExportSubtreeToMarkdown_SingleNode_IncludesFrontmatterAndContent()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            var root = new MindMapNodeViewModel(new MindMapNode
            {
                Title = "Architecture Root",
                Id = "arch-root",
                MarkdownContent = "Core system description."
            });
            vm.Nodes.Add(root);

            var md = vm.ExportSubtreeToMarkdown(root);

            Assert.Contains("---", md);
            Assert.Contains("title: \"Architecture Root\"", md);
            Assert.Contains("type: mindmap_subtree", md);
            Assert.Contains("node_count: 1", md);
            Assert.Contains("# Architecture Root", md);
            Assert.Contains("Core system description.", md);
        }

        [Fact]
        public void ExportSubtreeToMarkdown_HierarchicalTree_GeneratesIndentedHeadings()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            var root = new MindMapNodeViewModel(new MindMapNode
            {
                Title = "Project Alpha",
                Id = "alpha-root",
                MarkdownContent = "Root overview"
            });
            vm.Nodes.Add(root);

            var child = new MindMapNodeViewModel(new MindMapNode
            {
                Title = "Phase One",
                Id = "phase-one",
                ParentId = root.Id,
                MarkdownContent = "Initial rollout"
            });
            vm.Nodes.Add(child);

            var md = vm.ExportSubtreeToMarkdown(root);

            Assert.Contains("node_count: 2", md);
            Assert.Contains("# Project Alpha", md);
            Assert.Contains("Root overview", md);
            Assert.Contains("## Phase One", md);
            Assert.Contains("Initial rollout", md);
        }

        [Fact]
        public void ExportSubtreeToMarkdown_CycleInLinks_TerminatesSafely()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            var nodeA = new MindMapNodeViewModel(new MindMapNode { Title = "Node A", Id = "node-a" });
            var nodeB = new MindMapNodeViewModel(new MindMapNode { Title = "Node B", Id = "node-b" });
            vm.Nodes.Add(nodeA);
            vm.Nodes.Add(nodeB);

            // Create cyclic links: A -> B and B -> A
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = nodeA.Id, TargetNodeId = nodeB.Id }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = nodeB.Id, TargetNodeId = nodeA.Id }));

            var md = vm.ExportSubtreeToMarkdown(nodeA);

            Assert.NotEmpty(md);
            Assert.Contains("node_count: 2", md);
            Assert.Contains("# Node A", md);
            Assert.Contains("## Node B", md);
        }

        [Fact]
        public void Run125_SelectedTagFilter_HighlightsMatchingNodes_AndDimsNonMatching()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();

            var node1 = new MindMapNodeViewModel(new MindMapNode { Title = "Feature Spec", Id = "n1", Tags = new List<string> { "spec", "ux" } });
            var node2 = new MindMapNodeViewModel(new MindMapNode { Title = "Backend API", Id = "n2", Tags = new List<string> { "backend" } });
            vm.Nodes.Add(node1);
            vm.Nodes.Add(node2);

            vm.SelectedTagFilter = "ux";

            Assert.True(node1.IsHighlighted);
            Assert.False(node1.IsDimmed);

            Assert.False(node2.IsHighlighted);
            Assert.True(node2.IsDimmed);

            vm.SelectedTagFilter = null;

            Assert.False(node1.IsHighlighted);
            Assert.False(node1.IsDimmed);
            Assert.False(node2.IsHighlighted);
            Assert.False(node2.IsDimmed);
        }

        [Fact]
        public void Run126_GetRootNode_ResolvesRootOrFirstNode()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            Assert.Null(vm.GetRootNode());

            var root = new MindMapNodeViewModel(new MindMapNode { Title = "Central Brain", Id = "root-node" });
            var child1 = new MindMapNodeViewModel(new MindMapNode { Title = "Subtopic 1", Id = "child-1", ParentId = root.Id });
            var child2 = new MindMapNodeViewModel(new MindMapNode { Title = "Subtopic 2", Id = "child-2", ParentId = root.Id });

            vm.Nodes.Add(child1);
            vm.Nodes.Add(root);
            vm.Nodes.Add(child2);

            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = root.Id, TargetNodeId = child1.Id }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = root.Id, TargetNodeId = child2.Id }));

            var resolvedRoot = vm.GetRootNode();
            Assert.NotNull(resolvedRoot);
            Assert.Equal("root-node", resolvedRoot.Id);
            Assert.Equal("Central Brain", resolvedRoot.Title);
        }

        [Fact]
        public void Run128_HierarchyDepthAndNodeSelectors_WorkCorrectly()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            var root = new MindMapNodeViewModel(new MindMapNode { Title = "Central Brain", Id = "root-node" });
            var child1 = new MindMapNodeViewModel(new MindMapNode { Title = "Branch 1", Id = "child-1", ParentId = root.Id });
            var grandchild = new MindMapNodeViewModel(new MindMapNode { Title = "Leaf Detail", Id = "grandchild-1", ParentId = child1.Id });
            var orphan = new MindMapNodeViewModel(new MindMapNode { Title = "Isolated Note", Id = "orphan-1" });

            vm.Nodes.Add(root);
            vm.Nodes.Add(child1);
            vm.Nodes.Add(grandchild);
            vm.Nodes.Add(orphan);

            Assert.Equal(0, vm.GetNodeDepth(root));
            Assert.Equal(1, vm.GetNodeDepth(child1));
            Assert.Equal(2, vm.GetNodeDepth(grandchild));
            Assert.Equal(0, vm.GetNodeDepth(orphan));
            Assert.Equal(2, vm.GetMaxHierarchyDepth());

            var deepest = vm.SelectDeepestNode();
            Assert.NotNull(deepest);
            Assert.Equal("grandchild-1", deepest.Id);

            var leaf = vm.SelectNextLeafNode();
            Assert.NotNull(leaf);

            var orphanFound = vm.SelectNextOrphanNode();
            Assert.NotNull(orphanFound);
            Assert.Equal("orphan-1", orphanFound.Id);
        }

        [Theory]
        [InlineData("curved")]
        [InlineData("straight")]
        [InlineData("orthogonal")]
        public void Run129_ComputeHierarchyEdgeRouting_AdaptsToAllFourDirections(string style)
        {
            // Case 1: Child is to the Right (Horizontal Tree LTR)
            var right = MindMapStudioViewModel.ComputeHierarchyEdgeRouting(
                srcX: 100, srcY: 100, srcW: 100, srcH: 50,
                tgtX: 300, tgtY: 100, tgtW: 100, tgtH: 50,
                routingStyle: style);
            Assert.Equal(200, right.startX); // Right edge of parent
            Assert.Equal(300, right.endX);   // Left edge of child
            Assert.Equal(125, right.startY); // Center Y of parent
            Assert.Equal(125, right.endY);   // Center Y of child

            // Case 2: Child is to the Left (Right-to-Left Tree RTL)
            var left = MindMapStudioViewModel.ComputeHierarchyEdgeRouting(
                srcX: 300, srcY: 100, srcW: 100, srcH: 50,
                tgtX: 100, tgtY: 100, tgtW: 100, tgtH: 50,
                routingStyle: style);
            Assert.Equal(300, left.startX); // Left edge of parent
            Assert.Equal(200, left.endX);   // Right edge of child
            Assert.Equal(125, left.startY);
            Assert.Equal(125, left.endY);

            // Case 3: Child is Below (Top-Down Hierarchy)
            var down = MindMapStudioViewModel.ComputeHierarchyEdgeRouting(
                srcX: 100, srcY: 100, srcW: 100, srcH: 50,
                tgtX: 100, tgtY: 300, tgtW: 100, tgtH: 50,
                routingStyle: style);
            Assert.Equal(150, down.startX); // Center X of parent
            Assert.Equal(150, down.endX);   // Center X of child
            Assert.Equal(150, down.startY); // Bottom of parent (100 + 50)
            Assert.Equal(300, down.endY);   // Top of child

            // Case 4: Child is Above (Bottom-Up Hierarchy)
            var up = MindMapStudioViewModel.ComputeHierarchyEdgeRouting(
                srcX: 100, srcY: 300, srcW: 100, srcH: 50,
                tgtX: 100, tgtY: 100, tgtW: 100, tgtH: 50,
                routingStyle: style);
            Assert.Equal(150, up.startX); // Center X of parent
            Assert.Equal(150, up.endX);   // Center X of child
            Assert.Equal(300, up.startY); // Top of parent
            Assert.Equal(150, up.endY);   // Bottom of child (100 + 50)
        }

        [Theory]
        [InlineData("Solid", new double[0])]
        [InlineData("Dashed", new[] { 4.0, 3.0 })]
        [InlineData("Dotted", new[] { 1.5, 2.0 })]
        [InlineData("Unknown", new double[0])]
        public void GetDashArrayPattern_returns_expected_dash_intervals(string style, double[] expected)
        {
            var pattern = MindMapStudioViewModel.GetDashArrayPattern(style);
            Assert.Equal(expected, pattern);
        }

        [Theory]
        [InlineData(MindMapStudioViewModel.ConnectionStrokeWeight.Thin, 1.2)]
        [InlineData(MindMapStudioViewModel.ConnectionStrokeWeight.Normal, 1.8)]
        [InlineData(MindMapStudioViewModel.ConnectionStrokeWeight.Thick, 2.8)]
        public void Run131_GetStrokeThickness_ReturnsExpectedValues(MindMapStudioViewModel.ConnectionStrokeWeight weight, double expected)
        {
            Assert.Equal(expected, MindMapStudioViewModel.GetStrokeThickness(weight));
            Assert.Equal(expected, MindMapStudioViewModel.GetStrokeThickness(weight.ToString().ToLowerInvariant()));
        }

        [Fact]
        public void Run131_ComputeHierarchyEdgeRouting_RespectsTensionParameter()
        {
            // Horizontal LTR with different tensions
            var lowTension = MindMapStudioViewModel.ComputeHierarchyEdgeRouting(
                srcX: 100, srcY: 100, srcW: 100, srcH: 50,
                tgtX: 300, tgtY: 100, tgtW: 100, tgtH: 50,
                routingStyle: "curved", tension: 0.2);

            var highTension = MindMapStudioViewModel.ComputeHierarchyEdgeRouting(
                srcX: 100, srcY: 100, srcW: 100, srcH: 50,
                tgtX: 300, tgtY: 100, tgtW: 100, tgtH: 50,
                routingStyle: "curved", tension: 0.8);

            Assert.Equal(200, lowTension.startX);
            Assert.Equal(300, lowTension.endX);
            Assert.Equal(200, highTension.startX);
            Assert.Equal(300, highTension.endX);

            // With higher tension, control points push out further horizontally
            Assert.True(highTension.c1X > lowTension.c1X, $"high c1X {highTension.c1X} should be > low c1X {lowTension.c1X}");
            Assert.True(highTension.c2X < lowTension.c2X, $"high c2X {highTension.c2X} should be < low c2X {lowTension.c2X}");
        }

        [Theory]
        [InlineData("Curved", "Normal", true, "Curved · Normal · Arrows On")]
        [InlineData("Straight", "Thin", false, "Straight · Thin · Arrows Off")]
        [InlineData("Stepped", "Thick", true, "Stepped · Thick · Arrows On")]
        [InlineData("", null, false, "Curved · Normal · Arrows Off")]
        public void Run132_FormatConnectionStyleSummary_FormatsExpectedSummary(string style, string? weight, bool arrows, string expected)
        {
            var summary = MindMapStudioViewModel.FormatConnectionStyleSummary(style, weight, arrows);
            Assert.Equal(expected, summary);
        }

        [Theory]
        [InlineData(true, false, "Arrowheads Hidden")]
        [InlineData(false, true, "Arrowheads Visible")]
        public void Run133_ToggleArrowheads_TogglesVisibilityAndReturnsExpectedLabel(bool initial, bool expectedNext, string expectedLabel)
        {
            var (next, label) = MindMapStudioViewModel.ToggleArrowheads(initial);
            Assert.Equal(expectedNext, next);
            Assert.Equal(expectedLabel, label);
        }

        [Theory]
        [InlineData("curved", "Straight", "Straight Lines")]
        [InlineData("straight", "Orthogonal", "Stepped (Orthogonal)")]
        [InlineData("orthogonal", "Curved", "Curved (Bezier)")]
        [InlineData(null, "Curved", "Curved (Bezier)")]
        public void Run134_CycleRoutingStyle_CyclesCorrectly(string? current, string expectedNext, string expectedLabel)
        {
            var (next, label) = MindMapStudioViewModel.CycleRoutingStyle(current);
            Assert.Equal(expectedNext, next);
            Assert.Equal(expectedLabel, label);
        }

        [Fact]
        public void Run134_CycleStrokeWeight_CyclesThroughWeightsDeterministically()
        {
            var (w1, l1) = MindMapStudioViewModel.CycleStrokeWeight(MindMapStudioViewModel.ConnectionStrokeWeight.Thin);
            Assert.Equal(MindMapStudioViewModel.ConnectionStrokeWeight.Normal, w1);
            Assert.Contains("Normal", l1);

            var (w2, l2) = MindMapStudioViewModel.CycleStrokeWeight(w1);
            Assert.Equal(MindMapStudioViewModel.ConnectionStrokeWeight.Thick, w2);
            Assert.Contains("Thick", l2);

            var (w3, l3) = MindMapStudioViewModel.CycleStrokeWeight(w2);
            Assert.Equal(MindMapStudioViewModel.ConnectionStrokeWeight.Thin, w3);
            Assert.Contains("Thin", l3);
        }

        [Fact]
        public void Run135_CalculateNodeAnchorPoint_ComputesCardinalAnchors()
        {
            double x = 100, y = 200, w = 80, h = 40;
            var center = MindMapStudioViewModel.CalculateNodeAnchorPoint(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Center);
            Assert.Equal((140.0, 220.0), center);

            var left = MindMapStudioViewModel.CalculateNodeAnchorPoint(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Left);
            Assert.Equal((100.0, 220.0), left);

            var right = MindMapStudioViewModel.CalculateNodeAnchorPoint(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Right);
            Assert.Equal((180.0, 220.0), right);

            var top = MindMapStudioViewModel.CalculateNodeAnchorPoint(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Top);
            Assert.Equal((140.0, 200.0), top);

            var bottom = MindMapStudioViewModel.CalculateNodeAnchorPoint(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Bottom);
            Assert.Equal((140.0, 240.0), bottom);
        }

        [Fact]
        public void Run135_CycleAnchorPosition_CyclesDeterministically()
        {
            var (a1, l1) = MindMapStudioViewModel.CycleAnchorPosition(MindMapStudioViewModel.NodeAnchorPosition.Auto);
            Assert.Equal(MindMapStudioViewModel.NodeAnchorPosition.Left, a1);
            Assert.Contains("Left", l1);

            var (a2, _) = MindMapStudioViewModel.CycleAnchorPosition(a1);
            Assert.Equal(MindMapStudioViewModel.NodeAnchorPosition.Right, a2);

            var (a3, _) = MindMapStudioViewModel.CycleAnchorPosition(a2);
            Assert.Equal(MindMapStudioViewModel.NodeAnchorPosition.Top, a3);

            var (a4, _) = MindMapStudioViewModel.CycleAnchorPosition(a3);
            Assert.Equal(MindMapStudioViewModel.NodeAnchorPosition.Bottom, a4);

            var (a5, _) = MindMapStudioViewModel.CycleAnchorPosition(a4);
            Assert.Equal(MindMapStudioViewModel.NodeAnchorPosition.Center, a5);

            var (a6, l6) = MindMapStudioViewModel.CycleAnchorPosition(a5);
            Assert.Equal(MindMapStudioViewModel.NodeAnchorPosition.Auto, a6);
            Assert.Contains("Auto", l6);
        }

        [Fact]
        public void Run136_CalculateNodeAnchorPointWithPadding_ComputesClearance()
        {
            double x = 100, y = 200, w = 80, h = 40, pad = 8.0;
            var left = MindMapStudioViewModel.CalculateNodeAnchorPointWithPadding(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Left, pad);
            Assert.Equal((92.0, 220.0), left); // 100 - 8 = 92

            var right = MindMapStudioViewModel.CalculateNodeAnchorPointWithPadding(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Right, pad);
            Assert.Equal((188.0, 220.0), right); // 180 + 8 = 188

            var top = MindMapStudioViewModel.CalculateNodeAnchorPointWithPadding(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Top, pad);
            Assert.Equal((140.0, 192.0), top); // 200 - 8 = 192

            var bottom = MindMapStudioViewModel.CalculateNodeAnchorPointWithPadding(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Bottom, pad);
            Assert.Equal((140.0, 248.0), bottom); // 240 + 8 = 248

            var center = MindMapStudioViewModel.CalculateNodeAnchorPointWithPadding(x, y, w, h, MindMapStudioViewModel.NodeAnchorPosition.Center, pad);
            Assert.Equal((140.0, 220.0), center); // Center stays unaffected by cardinal padding
        }

        [Fact]
        public void Run136_CycleAnchorPadding_CyclesCorrectly()
        {
            var (p1, l1) = MindMapStudioViewModel.CycleAnchorPadding(0.0);
            Assert.Equal(4.0, p1);
            Assert.Contains("4px", l1);

            var (p2, _) = MindMapStudioViewModel.CycleAnchorPadding(p1);
            Assert.Equal(8.0, p2);

            var (p3, _) = MindMapStudioViewModel.CycleAnchorPadding(p2);
            Assert.Equal(12.0, p3);

            var (p4, l4) = MindMapStudioViewModel.CycleAnchorPadding(p3);
            Assert.Equal(0.0, p4);
            Assert.Contains("0px", l4);
        }

        [Fact]
        public void Run137_FormatFullConnectionConfiguration_FormatsExpectedSummary()
        {
            var summary = MindMapStudioViewModel.FormatFullConnectionConfiguration(
                "curved",
                1.8,
                MindMapStudioViewModel.NodeAnchorPosition.Auto,
                4.0,
                true);

            Assert.Equal("Connection: Curved (Bezier) · Weight: Normal (1.8px) · Anchor: Auto (4px) · Arrows: On", summary);

            var straightSummary = MindMapStudioViewModel.FormatFullConnectionConfiguration(
                "straight",
                1.2,
                MindMapStudioViewModel.NodeAnchorPosition.Right,
                0.0,
                false);

            Assert.Equal("Connection: Straight (Direct) · Weight: Thin (1.2px) · Anchor: Right Edge (Flush (0px)) · Arrows: Off", straightSummary);
        }

        [Fact]
        public void Run138_CycleAnchorSnapAngle_CyclesAndFormatsDeterministically()
        {
            var (a1, l1) = MindMapStudioViewModel.CycleAnchorSnapAngle(0.0);
            Assert.Equal(45.0, a1);
            Assert.Contains("45°", l1);

            var (a2, l2) = MindMapStudioViewModel.CycleAnchorSnapAngle(a1);
            Assert.Equal(90.0, a2);
            Assert.Contains("90°", l2);

            var (a3, l3) = MindMapStudioViewModel.CycleAnchorSnapAngle(a2);
            Assert.Equal(0.0, a3);
            Assert.Contains("0°", l3);

            Assert.Equal("Anchor snap: Free / Direct (0°)", MindMapStudioViewModel.FormatAnchorSnapSummary(0.0));
            Assert.Equal("Anchor snap: Diagonal / Octant (45°)", MindMapStudioViewModel.FormatAnchorSnapSummary(45.0));
            Assert.Equal("Anchor snap: Cardinal / Orthogonal (90°)", MindMapStudioViewModel.FormatAnchorSnapSummary(90.0));
        }

        [Fact]
        public void Run139_ToggleConnectionLabels_And_FormatConnectionLabelsSummary()
        {
            var (s1, l1) = MindMapStudioViewModel.ToggleConnectionLabels(true);
            Assert.False(s1);
            Assert.Equal("Hide connection labels", l1);

            var (s2, l2) = MindMapStudioViewModel.ToggleConnectionLabels(false);
            Assert.True(s2);
            Assert.Equal("Show connection labels", l2);

            var summary1 = MindMapStudioViewModel.FormatConnectionLabelsSummary(true, 5);
            Assert.Equal("Connection labels: Visible (5 connections)", summary1);

            var summary2 = MindMapStudioViewModel.FormatConnectionLabelsSummary(false, 1);
            Assert.Equal("Connection labels: Hidden (1 connection)", summary2);
        }

        [Fact]
        public void Run140_StepStrokeThickness_And_FormatStrokeThicknessSummary()
        {
            var (t1, l1) = MindMapStudioViewModel.StepStrokeThickness(1.0);
            Assert.Equal(1.8, t1);
            Assert.Contains("1.8px", l1);

            var (t2, l2) = MindMapStudioViewModel.StepStrokeThickness(1.8);
            Assert.Equal(2.8, t2);
            Assert.Contains("2.8px", l2);

            var (t3, l3) = MindMapStudioViewModel.StepStrokeThickness(2.8);
            Assert.Equal(4.0, t3);
            Assert.Contains("4.0px", l3);

            var (t4, l4) = MindMapStudioViewModel.StepStrokeThickness(4.0);
            Assert.Equal(1.0, t4);
            Assert.Contains("1.0px", l4);

            Assert.Equal("Stroke thickness: 1px (Hairline)", MindMapStudioViewModel.FormatStrokeThicknessSummary(1.0));
            Assert.Equal("Stroke thickness: 1.8px (Standard)", MindMapStudioViewModel.FormatStrokeThicknessSummary(1.8));
            Assert.Equal("Stroke thickness: 2.8px (Bold)", MindMapStudioViewModel.FormatStrokeThicknessSummary(2.8));
            Assert.Equal("Stroke thickness: 4px (Heavy)", MindMapStudioViewModel.FormatStrokeThicknessSummary(4.0));
        }

        [Fact]
        public void Run141_CycleLinkDirection_And_FormatLinkDirectionSummary()
        {
            var (d1, l1) = MindMapStudioViewModel.CycleLinkDirection(MindMapLinkDirection.SourceToTarget);
            Assert.Equal(MindMapLinkDirection.Bidirectional, d1);
            Assert.Contains("Bidirectional", l1);

            var (d2, l2) = MindMapStudioViewModel.CycleLinkDirection(MindMapLinkDirection.Bidirectional);
            Assert.Equal(MindMapLinkDirection.TargetToSource, d2);
            Assert.Contains("Target to source", l2);

            var (d3, l3) = MindMapStudioViewModel.CycleLinkDirection(MindMapLinkDirection.TargetToSource);
            Assert.Equal(MindMapLinkDirection.None, d3);
            Assert.Contains("Plain line", l3);

            var (d4, l4) = MindMapStudioViewModel.CycleLinkDirection(MindMapLinkDirection.None);
            Assert.Equal(MindMapLinkDirection.SourceToTarget, d4);
            Assert.Contains("Source to target", l4);

            Assert.Equal("Arrow direction: Forward (Source → Target)", MindMapStudioViewModel.FormatLinkDirectionSummary(MindMapLinkDirection.SourceToTarget));
            Assert.Equal("Arrow direction: Reverse (Target → Source)", MindMapStudioViewModel.FormatLinkDirectionSummary(MindMapLinkDirection.TargetToSource));
            Assert.Equal("Arrow direction: Mutual (Bidirectional ↔)", MindMapStudioViewModel.FormatLinkDirectionSummary(MindMapLinkDirection.Bidirectional));
            Assert.Equal("Arrow direction: Undirected (Plain line —)", MindMapStudioViewModel.FormatLinkDirectionSummary(MindMapLinkDirection.None));
        }

        [Fact]
        public void Run142_AutoColorizeByHierarchy_And_FormatHierarchyColorSummary()
        {
            var vm = new MindMapStudioViewModel();
            // Tutorial galaxy has multiple nodes
            var (nodeCount, maxDepth, distinctColors) = vm.AutoColorizeByHierarchy();
            Assert.True(nodeCount > 0);
            Assert.True(maxDepth >= 1);
            Assert.True(distinctColors >= 2);

            var summary = MindMapStudioViewModel.FormatHierarchyColorSummary(nodeCount, maxDepth, distinctColors);
            Assert.Contains($"{nodeCount} nodes across", summary);
            Assert.Contains($"{distinctColors} distinct colors applied", summary);

            // Undo test
            Assert.True(vm.CanUndo);
            vm.Undo();

            // Empty summary formatting
            Assert.Equal("Auto-colorize: 0 nodes (Empty map)", MindMapStudioViewModel.FormatHierarchyColorSummary(0, 0, 0));
            Assert.Equal("Auto-colorized 1 node across 1 level (Flat) (1 color applied)", MindMapStudioViewModel.FormatHierarchyColorSummary(1, 0, 1));
        }

        [Fact]
        public void Run143_AttachOrphansToRoot_And_FormatOrphanAttachmentSummary()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            Assert.Equal("Orphan attachment: 0 nodes (Empty map)", MindMapStudioViewModel.FormatOrphanAttachmentSummary(0, "", 0));

            var root = new MindMapNodeViewModel(new MindMapNode { Id = "root", Title = "Central Hub", X = 100, Y = 100 });
            var connectedChild = new MindMapNodeViewModel(new MindMapNode { Id = "c1", Title = "Connected Child", X = 250, Y = 100 });
            var orphan1 = new MindMapNodeViewModel(new MindMapNode { Id = "o1", Title = "Orphan Alpha", X = 800, Y = 800 });
            var orphan2 = new MindMapNodeViewModel(new MindMapNode { Id = "o2", Title = "Orphan Beta", X = 900, Y = 900 });

            vm.Nodes.Add(root);
            vm.Nodes.Add(connectedChild);
            vm.Nodes.Add(orphan1);
            vm.Nodes.Add(orphan2);
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "root", TargetNodeId = "c1" }));

            var (attached, rootLabel, totalNodes) = vm.AttachOrphansToRoot(200.0);
            Assert.Equal(2, attached);
            Assert.Equal("Central Hub", rootLabel);
            Assert.Equal(4, totalNodes);
            Assert.Equal(3, vm.Links.Count);

            var summary = MindMapStudioViewModel.FormatOrphanAttachmentSummary(attached, rootLabel, totalNodes);
            Assert.Equal("Attached 2 orphaned nodes to root 'Central Hub' (4 total nodes · 0 orphans remaining)", summary);

            // Second call finds 0 remaining orphans
            var (attachedAgain, _, _) = vm.AttachOrphansToRoot(200.0);
            Assert.Equal(0, attachedAgain);
            var cleanSummary = MindMapStudioViewModel.FormatOrphanAttachmentSummary(0, rootLabel, totalNodes);
            Assert.Equal("Orphan check: 0 orphaned nodes found (4 total nodes connected to 'Central Hub')", cleanSummary);
        }

        [Fact]
        public void Run144_AuditLinkedFilesStatus_And_FormatLinkedFilesAuditSummary()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            Assert.Equal("Linked files audit: 0 linked files (Empty map)",
                MindMapStudioViewModel.FormatLinkedFilesAuditSummary(0, 0, 0, null, 0));

            var n1 = new MindMapNodeViewModel(new MindMapNode { Id = "n1", Title = "Architecture Spec", FilePath = @"C:\docs\arch.md" });
            var n2 = new MindMapNodeViewModel(new MindMapNode { Id = "n2", Title = "Missing Roadmap", FilePath = @"C:\docs\missing.md" });
            var n3 = new MindMapNodeViewModel(new MindMapNode { Id = "n3", Title = "Unlinked Note", FilePath = "" });

            vm.Nodes.Add(n1);
            vm.Nodes.Add(n2);
            vm.Nodes.Add(n3);

            var (totalLinked, validCount, missingCount, missingTitles) =
                vm.AuditLinkedFilesStatus(path => path.EndsWith("arch.md", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(2, totalLinked);
            Assert.Equal(1, validCount);
            Assert.Equal(1, missingCount);
            Assert.Single(missingTitles);
            Assert.Equal("Missing Roadmap", missingTitles[0]);

            var summary = MindMapStudioViewModel.FormatLinkedFilesAuditSummary(totalLinked, validCount, missingCount, missingTitles, vm.Nodes.Count);
            Assert.Equal("Linked files audit (2 linked files): 1 valid (50%), 1 broken/missing ('Missing Roadmap')", summary);

            var allValidSummary = MindMapStudioViewModel.FormatLinkedFilesAuditSummary(2, 2, 0, Array.Empty<string>(), 3);
            Assert.Equal("Linked files audit (2 linked files): 100% valid on disk (2 verified · 0 broken links)", allValidSummary);
        }

        [Fact]
        public void Run145_RollupSubtreeProgress_And_FormatSubtreeProgressRollupSummary()
        {
            var vm = new MindMapStudioViewModel();

            Assert.Equal("Subtree progress rollup: 0 nodes (Empty map)",
                MindMapStudioViewModel.FormatSubtreeProgressRollupSummary(0, 0, 0, 0));

            var doc = new MindMapDocument
            {
                RootNodeId = "r",
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "r", Title = "Root", Progress = 0 },
                    new MindMapNode { Id = "a", Title = "Branch A", ParentId = "r", Progress = 0 },
                    new MindMapNode { Id = "a1", Title = "Leaf A1", ParentId = "a", Progress = 100 },
                    new MindMapNode { Id = "a2", Title = "Leaf A2", ParentId = "a", Progress = 50 },
                    new MindMapNode { Id = "b", Title = "Leaf B", ParentId = "r", Progress = 25 }
                }
            };
            vm.LoadDocument(doc);

            var (updatedParents, leafCount, rootProgress) = vm.RollupSubtreeProgress();
            // Leaf A1 (100) + Leaf A2 (50) => Branch A = 75
            // Branch A (75) + Leaf B (25) => Root = 50
            Assert.Equal(2, updatedParents);
            Assert.Equal(3, leafCount);
            Assert.Equal(75, vm.Nodes.Single(n => n.Id == "a").Progress);
            Assert.Equal(50, vm.Nodes.Single(n => n.Id == "r").Progress);
            Assert.Equal(50, rootProgress);

            var summary = MindMapStudioViewModel.FormatSubtreeProgressRollupSummary(updatedParents, leafCount, rootProgress, vm.Nodes.Count);
            Assert.Equal("Subtree progress rollup: 2 parent hubs updated from 3 leaf nodes (Root completion: 50%)", summary);

            Assert.True(vm.CanUndo);
            vm.Undo();
            Assert.Equal(0, vm.Nodes.Single(n => n.Id == "r").Progress);
        }

        [Fact]
        public void Run146_AutoColorizeByPrimaryTag_And_FormatTagCoOccurrenceSummary()
        {
            var vm = new MindMapStudioViewModel();

            Assert.Equal("Tag co-occurrence audit: 0 nodes (Empty map)",
                MindMapStudioViewModel.FormatTagCoOccurrenceSummary(0, 0, null, 0));

            var doc = new MindMapDocument
            {
                RootNodeId = "r",
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "r", Title = "Architecture", Tags = new List<string> { "core", "winui" } },
                    new MindMapNode { Id = "a", Title = "Markdown Pipeline", ParentId = "r", Tags = new List<string> { "core", "parser" } },
                    new MindMapNode { Id = "b", Title = "Desktop Views", ParentId = "r", Tags = new List<string> { "winui", "ux" } },
                    new MindMapNode { Id = "c", Title = "Core Services", ParentId = "r", Tags = new List<string> { "core", "winui" } },
                    new MindMapNode { Id = "d", Title = "Untagged Leaf", ParentId = "r" }
                }
            };
            vm.LoadDocument(doc);

            // Auto-colorize by primary tag
            var (colorized, uniqueTags, appliedColors) = vm.AutoColorizeByPrimaryTag();
            Assert.Equal(4, colorized);
            Assert.Equal(2, uniqueTags); // "core" and "winui" as primary tags
            Assert.Equal(2, appliedColors);
            Assert.Contains("Auto-colorized 4 nodes across 2 tag categories", vm.StatusMessage);

            // Node 'r', 'a', 'c' have primary tag "core", so they share the same color
            Assert.Equal(vm.Nodes.Single(n => n.Id == "r").ColorHex, vm.Nodes.Single(n => n.Id == "a").ColorHex);
            Assert.Equal(vm.Nodes.Single(n => n.Id == "r").ColorHex, vm.Nodes.Single(n => n.Id == "c").ColorHex);

            // Tag co-occurrence audit
            var (multiTagged, totalInstances, pairs) = vm.AuditTagCoOccurrence();
            Assert.Equal(4, multiTagged); // r, a, b, c all have 2 tags
            Assert.Equal(8, totalInstances);
            Assert.True(pairs.Count >= 3);

            // 'core' + 'winui' appears in r and c (2 times)
            var coreWinuiPair = pairs.FirstOrDefault(p => (p.TagA == "core" && p.TagB == "winui") || (p.TagA == "winui" && p.TagB == "core"));
            Assert.Equal(2, coreWinuiPair.CoOccurrenceCount);

            var summary = vm.FormatTagCoOccurrenceSummary();
            Assert.Contains("Tag co-occurrence audit (4 multi-tagged nodes", summary);
            Assert.Contains("'core' + 'winui' (2×)", summary);

            Assert.True(vm.CanUndo);
            vm.Undo();
        }

        [Fact]
        public void Run147_CenterGalaxyOnCenterOfMass_And_SpatialQuadrantBalance()
        {
            var vm = new MindMapStudioViewModel();
            Assert.Equal("Spatial quadrant balance: 0 nodes (Empty map)",
                MindMapStudioViewModel.FormatSpatialQuadrantBalanceSummary(0, 0, 0, 0, 0, 0, 0, 0, 0));

            var doc = new MindMapDocument
            {
                RootNodeId = "root",
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "root", Title = "Root", X = 0, Y = 0, Width = 100, Height = 50 },
                    new MindMapNode { Id = "nw", Title = "Top-Left", X = -200, Y = -200, Width = 100, Height = 50 },
                    new MindMapNode { Id = "ne", Title = "Top-Right", X = 200, Y = -200, Width = 100, Height = 50 },
                    new MindMapNode { Id = "sw", Title = "Bottom-Left", X = -200, Y = 200, Width = 100, Height = 50 },
                    new MindMapNode { Id = "se", Title = "Bottom-Right", X = 200, Y = 200, Width = 100, Height = 50 }
                }
            };
            vm.LoadDocument(doc);

            // Center of mass
            var (cx, cy) = vm.CenterGalaxyOnCenterOfMass();
            // All 4 outer nodes symmetric + root at (0,0) -> average center is (50, 25)
            Assert.Equal(50, cx);
            Assert.Equal(25, cy);
            Assert.Equal(-50, vm.ViewportOffsetX);
            Assert.Equal(-25, vm.ViewportOffsetY);
            Assert.Contains("Centered galaxy viewport on center of mass", vm.StatusMessage);

            // Quadrant balance
            var (nw, ne, sw, se, quadCx, quadCy, driftX, driftY) = vm.AuditSpatialQuadrantBalance();
            Assert.Equal(1, nw);
            Assert.Equal(1, ne);
            Assert.Equal(1, sw);
            Assert.Equal(2, se); // Root + SE node both reside in SE (nx >= cx && ny >= cy)
            Assert.Equal(5, nw + ne + sw + se);

            var summary = vm.FormatSpatialQuadrantBalanceSummary();
            Assert.Contains("Spatial quadrant balance (5 nodes", summary);
            Assert.Contains("Center of Mass (50, 25)", summary);
        }

        [Fact]
        public void Run148_NormalizeNodeTextCasing()
        {
            var vm = new MindMapStudioViewModel();
            Assert.Equal("Normalize node text casing: 0 nodes (Empty map)",
                MindMapStudioViewModel.FormatNodeCasingSummary(0, 0, NodeCasingMode.TitleCase));

            var doc = new MindMapDocument
            {
                RootNodeId = "root",
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "root", Title = "core ARCHITECTURE" },
                    new MindMapNode { Id = "n1", Title = "USER interface" },
                    new MindMapNode { Id = "n2", Title = "PIPELINE" }
                }
            };
            vm.LoadDocument(doc);

            // Normalize to Title Case
            var (modTitle, totalTitle, sumTitle) = vm.NormalizeNodeTextCasing(NodeCasingMode.TitleCase);
            Assert.Equal(3, modTitle);
            Assert.Equal(3, totalTitle);
            Assert.Equal("Core Architecture", vm.Nodes.First(n => n.Id == "root").Title);
            Assert.Equal("User Interface", vm.Nodes.First(n => n.Id == "n1").Title);
            Assert.Equal("Pipeline", vm.Nodes.First(n => n.Id == "n2").Title);
            Assert.Contains("Normalize node text casing (TitleCase): 3 nodes updated", sumTitle);

            // Test Undo restores original titles
            Assert.True(vm.CanUndo);
            vm.Undo();
            Assert.Equal("core ARCHITECTURE", vm.Nodes.First(n => n.Id == "root").Title);
            Assert.Equal("USER interface", vm.Nodes.First(n => n.Id == "n1").Title);
            Assert.Equal("PIPELINE", vm.Nodes.First(n => n.Id == "n2").Title);

            // Normalize to Upper Case
            var (modUpper, _, _) = vm.NormalizeNodeTextCasing(NodeCasingMode.UpperCase);
            Assert.Equal(2, modUpper); // "PIPELINE" is already uppercase
            Assert.Equal("CORE ARCHITECTURE", vm.Nodes.First(n => n.Id == "root").Title);

            // Normalize to Lower Case
            var (modLower, _, _) = vm.NormalizeNodeTextCasing(NodeCasingMode.LowerCase);
            Assert.Equal(3, modLower);
            Assert.Equal("core architecture", vm.Nodes.First(n => n.Id == "root").Title);

            // Normalize to Sentence Case
            var (modSentence, _, _) = vm.NormalizeNodeTextCasing(NodeCasingMode.SentenceCase);
            Assert.Equal(3, modSentence); // All 3 lowercased nodes capitalized to sentence case
            Assert.Equal("Core architecture", vm.Nodes.First(n => n.Id == "root").Title);
        }

        [Fact]
        public void Run149_AuditInactiveSubtrees_DetectsDormantNodesAndSubtrees()
        {
            var refDate = new DateTime(2026, 10, 18);
            var nodes = new List<MindMapNode>
            {
                new MindMapNode { Id = "root", Title = "Active Root", ModifiedDate = "2026-10-15 10:00" },
                new MindMapNode { Id = "active_child", ParentId = "root", Title = "Recent Task", Progress = 50, ModifiedDate = "2026-10-17 12:00" },
                new MindMapNode { Id = "done_child", ParentId = "root", Title = "Completed Feature", Progress = 100 },
                new MindMapNode { Id = "dormant_branch", ParentId = "root", Title = "Old Module", ModifiedDate = "2026-05-01 09:00" },
                new MindMapNode { Id = "dormant_leaf", ParentId = "dormant_branch", Title = "Old Spec", ModifiedDate = "2026-04-10 09:00", Tags = new List<string> { "archived" } }
            };

            var (inactive, total, roots, pct, summary) = MindMapStudioViewModel.AuditInactiveSubtrees(
                nodes, null, inactiveDaysThreshold: 60, referenceDate: refDate);

            Assert.Equal(5, total);
            Assert.Equal(3, inactive); // done_child, dormant_branch, dormant_leaf
            Assert.Equal(2, roots);    // done_child, dormant_branch
            Assert.Equal(60.0, pct);
            Assert.Contains("3 dormant nodes across 2 subtree roots", summary);
            Assert.Contains("60% dormant", summary);

            // Clean active map
            var activeNodes = new List<MindMapNode>
            {
                new MindMapNode { Id = "n1", Title = "Active 1", ModifiedDate = "2026-10-17 10:00" },
                new MindMapNode { Id = "n2", Title = "Active 2", ModifiedDate = "2026-10-18 09:00" }
            };
            var cleanResult = MindMapStudioViewModel.AuditInactiveSubtrees(activeNodes, null, 60, refDate);
            Assert.Equal(0, cleanResult.InactiveCount);
            Assert.Contains("All 2 nodes are active and recently updated", cleanResult.Summary);
        }

        [Fact]
        public void Run150_AuditTopologicalDepth_ComputesHierarchyLevelsAndBreadth()
        {
            // 1. Empty map
            var emptyVm = new MindMapStudioViewModel();
            emptyVm.LoadDocument(new MindMapDocument { Nodes = new List<MindMapNode>() });
            var emptyAudit = emptyVm.AuditTopologicalDepth();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.RootCount);
            Assert.Equal(0, emptyAudit.MaxDepth);
            Assert.Equal(0.0, emptyAudit.AverageDepth);
            Assert.Contains("0 nodes (Empty map)", emptyAudit.Summary);

            // 2. Multi-level hierarchy with 2 roots
            // Root1 (L0) -> Child1 (L1), Child2 (L1) -> Grandchild1 (L2)
            // Root2 (L0, isolated)
            // Levels: L0: 2 (Root1, Root2), L1: 2 (Child1, Child2), L2: 1 (Grandchild1)
            var doc = new MindMapDocument
            {
                RootNodeId = "root1",
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "root1", Title = "Architecture Core" },
                    new MindMapNode { Id = "child1", ParentId = "root1", Title = "Services Subsystem" },
                    new MindMapNode { Id = "child2", ParentId = "root1", Title = "UI Layer" },
                    new MindMapNode { Id = "grandchild1", ParentId = "child1", Title = "Data Pipeline" },
                    new MindMapNode { Id = "root2", Title = "Standalone Utilities" }
                }
            };

            var vm = new MindMapStudioViewModel();
            vm.LoadDocument(doc);
            var audit = vm.AuditTopologicalDepth();

            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal(2, audit.RootCount);
            Assert.Equal(2, audit.MaxDepth); // L0, L1, L2 -> Max depth index = 2
            Assert.Equal(0.8, audit.AverageDepth); // (0 + 1 + 1 + 2 + 0) / 5 = 4 / 5 = 0.8
            Assert.Equal(0, audit.WidestLevel); // Level 0 has 2 nodes (first max)
            Assert.Equal(2, audit.MaxWidth);
            Assert.Equal(3, audit.LevelCounts.Count);
            Assert.Equal(2, audit.LevelCounts[0]);
            Assert.Equal(2, audit.LevelCounts[1]);
            Assert.Equal(1, audit.LevelCounts[2]);

            Assert.Contains("Topological depth audit (5 nodes across 3 levels (Depth 0–2)): 2 roots", audit.Summary);
            Assert.Contains("Avg depth: 0.8", audit.Summary);
            Assert.Contains("Widest: Level 0 (2 nodes)", audit.Summary);
            Assert.Contains("[L0:2 · L1:2 · L2:1]", audit.Summary);

            // 3. Cycle protection fallback
            var cyclicDoc = new MindMapDocument
            {
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "cycleA", ParentId = "cycleB", Title = "Cycle A" },
                    new MindMapNode { Id = "cycleB", ParentId = "cycleA", Title = "Cycle B" }
                }
            };
            var cyclicVm = new MindMapStudioViewModel();
            cyclicVm.LoadDocument(cyclicDoc);
            var cyclicAudit = cyclicVm.AuditTopologicalDepth();
            Assert.Equal(2, cyclicAudit.TotalNodes);
            Assert.True(cyclicAudit.MaxDepth <= 1);
        }

        [Fact]
        public void Run151_AuditOrphanedNodes_And_ConnectToClosestCluster()
        {
            // 1. Empty map
            var emptyAudit = MindMapStudioViewModel.AuditOrphanedNodes(new List<MindMapNodeViewModel>());
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Contains("0 nodes (Empty map)", emptyAudit.Summary);

            // 2. Map with connected cluster and 2 orphans
            var doc = new MindMapDocument
            {
                Nodes = new List<MindMapNode>
                {
                    new MindMapNode { Id = "hub", Title = "Main Hub", X = 100, Y = 100 },
                    new MindMapNode { Id = "c1", Title = "Child 1", X = 200, Y = 100 },
                    new MindMapNode { Id = "o1", Title = "Orphan Alpha", X = 250, Y = 100 },
                    new MindMapNode { Id = "o2", Title = "Orphan Beta", X = 100, Y = 220 }
                },
                Links = new List<MindMapLink>
                {
                    new MindMapLink { SourceNodeId = "hub", TargetNodeId = "c1" }
                }
            };

            var vm = new MindMapStudioViewModel();
            vm.LoadDocument(doc);

            var audit = vm.AuditOrphanedNodes();
            Assert.Equal(4, audit.TotalNodes);
            Assert.Equal(2, audit.OrphanCount);
            Assert.Equal(2, audit.ConnectedCount);
            Assert.True(audit.AvgDistanceToNearestCluster > 0);
            Assert.Contains("2 orphans disconnected", audit.Summary);
            Assert.Contains("Orphan Alpha", audit.Summary);

            // 3. Connect orphans to closest cluster
            var (connected, avgDist, summary) = vm.ConnectOrphanedNodesToClosestCluster();
            Assert.Equal(2, connected);
            Assert.Equal(3, vm.Links.Count);
            Assert.True(avgDist > 0);
            Assert.Contains("Connected 2 orphaned nodes", summary);

            // Verify Alpha is connected to c1 (distance 50px < 150px to hub)
            Assert.Contains(vm.Links, l => (l.SourceNodeId == "c1" && l.TargetNodeId == "o1") || (l.SourceNodeId == "o1" && l.TargetNodeId == "c1"));

            // 4. Subsequent audit reports 0 orphans
            var cleanAudit = vm.AuditOrphanedNodes();
            Assert.Equal(0, cleanAudit.OrphanCount);
            Assert.Contains("0 orphans", cleanAudit.Summary);
        }

        [Fact]
        public void Run152_AuditClusterConvexHulls()
        {
            // 1. Empty map
            var empty = MindMapStudioViewModel.AuditClusterConvexHulls(new List<MindMapNodeViewModel>());
            Assert.Equal(0, empty.TotalClusters);
            Assert.Equal(0, empty.TotalNodes);
            Assert.Contains("0 clusters, 0 nodes (Empty map)", empty.Summary);

            // 2. Tagged map with multiple clusters
            var doc = new MindMapDocument
            {
                Nodes = new List<MindMapNode>
                {
                    // Cluster "Architecture" (triangle: 0,0; 100,0; 50,100) -> Area 5000
                    new MindMapNode { Id = "a1", Title = "Arch 1", X = 0, Y = 0, Tags = new List<string> { "Architecture" } },
                    new MindMapNode { Id = "a2", Title = "Arch 2", X = 100, Y = 0, Tags = new List<string> { "Architecture" } },
                    new MindMapNode { Id = "a3", Title = "Arch 3", X = 50, Y = 100, Tags = new List<string> { "Architecture" } },

                    // Cluster "Backend" (2 nodes: segment)
                    new MindMapNode { Id = "b1", Title = "Back 1", X = 200, Y = 200, Tags = new List<string> { "Backend" } },
                    new MindMapNode { Id = "b2", Title = "Back 2", X = 300, Y = 200, Tags = new List<string> { "Backend" } }
                }
            };

            var vm = new MindMapStudioViewModel();
            vm.LoadDocument(doc);

            var audit = vm.AuditClusterConvexHulls();
            Assert.Equal(2, audit.TotalClusters);
            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal("Architecture", audit.LargestClusterTag);
            Assert.Equal(5000.0, audit.MaxClusterArea);

            var arch = audit.Clusters.First(c => c.Tag == "Architecture");
            Assert.Equal(3, arch.NodeCount);
            Assert.Equal(5000.0, arch.HullArea);
            Assert.True(arch.HullPerimeter > 200.0);
            Assert.Equal(100.0, arch.BoundingWidth);
            Assert.Equal(100.0, arch.BoundingHeight);
            Assert.True(arch.Density > 0);

            var back = audit.Clusters.First(c => c.Tag == "Backend");
            Assert.Equal(2, back.NodeCount);
            Assert.Equal(0.0, back.HullArea);
            Assert.Equal(200.0, back.HullPerimeter);

            Assert.Contains("Largest 'Architecture'", audit.Summary);
        }

        [Fact]
        public void Run153_AuditMinimumSpanningTree()
        {
            // 1. Empty / single node
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            var emptyAudit = vm.AuditMinimumSpanningTree();
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Contains("Insufficient nodes", emptyAudit.Summary);

            // 2. 5 nodes forming a star around hub at (100, 100)
            // Hub: (100, 100), N1: (100, 0), N2: (200, 100), N3: (100, 200), N4: (0, 100)
            var hub = new MindMapNode { Id = "hub", Title = "Trunk Hub", X = 100, Y = 100 };
            var n1 = new MindMapNode { Id = "n1", Title = "Leaf 1", X = 100, Y = 0 };
            var n2 = new MindMapNode { Id = "n2", Title = "Leaf 2", X = 200, Y = 100 };
            var n3 = new MindMapNode { Id = "n3", Title = "Leaf 3", X = 100, Y = 200 };
            var n4 = new MindMapNode { Id = "n4", Title = "Leaf 4", X = 0, Y = 100 };

            vm.Nodes.Add(new MindMapNodeViewModel(hub));
            vm.Nodes.Add(new MindMapNodeViewModel(n1));
            vm.Nodes.Add(new MindMapNodeViewModel(n2));
            vm.Nodes.Add(new MindMapNodeViewModel(n3));
            vm.Nodes.Add(new MindMapNodeViewModel(n4));

            var audit = vm.AuditMinimumSpanningTree();
            Assert.Equal(4, audit.TotalEdges);
            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal(400.0, audit.TotalWireLength);
            Assert.Equal(100.0, audit.AverageEdgeLength);
            Assert.Equal(100.0, audit.MaxEdgeLength);
            Assert.Equal(1, audit.TrunkCorridorCount);
            Assert.Contains("Trunk Hub", audit.TrunkNodeTitles[0]);
            Assert.Contains("4 edges · 400px wire", audit.Summary);
        }

        [Fact]
        public void Run154_AuditDegreeCentrality()
        {
            // 1. Empty / single node
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            var emptyAudit = vm.AuditDegreeCentrality();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalLinks);
            Assert.Contains("Insufficient nodes", emptyAudit.Summary);

            // 2. Star graph: Central Hub connected to 4 satellite leaves and 1 orphan
            var hub = new MindMapNode { Id = "hub", Title = "Central API Gateway" };
            var leaf1 = new MindMapNode { Id = "leaf1", Title = "Auth Microservice" };
            var leaf2 = new MindMapNode { Id = "leaf2", Title = "Billing Worker" };
            var leaf3 = new MindMapNode { Id = "leaf3", Title = "Telemetry Streamer" };
            var leaf4 = new MindMapNode { Id = "leaf4", Title = "Notification Sender" };
            var orphan = new MindMapNode { Id = "orphan", Title = "Draft Scratchpad" };

            vm.Nodes.Add(new MindMapNodeViewModel(hub));
            vm.Nodes.Add(new MindMapNodeViewModel(leaf1));
            vm.Nodes.Add(new MindMapNodeViewModel(leaf2));
            vm.Nodes.Add(new MindMapNodeViewModel(leaf3));
            vm.Nodes.Add(new MindMapNodeViewModel(leaf4));
            vm.Nodes.Add(new MindMapNodeViewModel(orphan));

            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "hub", TargetNodeId = "leaf1" }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "hub", TargetNodeId = "leaf2" }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "hub", TargetNodeId = "leaf3" }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "hub", TargetNodeId = "leaf4" }));

            var audit = vm.AuditDegreeCentrality();
            Assert.Equal(6, audit.TotalNodes);
            Assert.Equal(4, audit.TotalLinks);
            Assert.Equal(4, audit.MaxDegree);
            Assert.Equal("Central API Gateway", audit.TopHubTitle);
            Assert.Equal(4, audit.LeafNodeCount);
            Assert.Equal(1, audit.OrphanNodeCount);
            Assert.Contains("Top hub 'Central API Gateway' (deg:4)", audit.Summary);
            Assert.Contains("4 leaves, 1 orphan", audit.Summary);
        }

        [Fact]
        public void Run155_AuditPeripheryEccentricity()
        {
            // 1. Empty / 0 nodes
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            var emptyAudit = vm.AuditPeripheryEccentricity();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.LeafNodeCount);
            Assert.Contains("Insufficient nodes", emptyAudit.Summary);

            // 2. Linear Chain graph: Root -> Node1 -> Node2 -> Node3 (Leaf)
            // Depth from Root: 0 -> 1 -> 2 -> 3
            vm.Document.RootNodeId = "root";
            var root = new MindMapNode { Id = "root", Title = "Root System" };
            var n1 = new MindMapNode { Id = "n1", Title = "Subsystem A" };
            var n2 = new MindMapNode { Id = "n2", Title = "Subsystem B" };
            var leaf = new MindMapNode { Id = "leaf", Title = "Terminal Sensor" };

            vm.Nodes.Add(new MindMapNodeViewModel(root));
            vm.Nodes.Add(new MindMapNodeViewModel(n1));
            vm.Nodes.Add(new MindMapNodeViewModel(n2));
            vm.Nodes.Add(new MindMapNodeViewModel(leaf));

            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "root", TargetNodeId = "n1" }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "n1", TargetNodeId = "n2" }));
            vm.Links.Add(new MindMapLinkViewModel(new MindMapLink { SourceNodeId = "n2", TargetNodeId = "leaf" }));

            var audit = vm.AuditPeripheryEccentricity();
            Assert.Equal(4, audit.TotalNodes);
            Assert.Equal(1, audit.LeafNodeCount);
            Assert.Equal(3, audit.MaxEccentricity);
            Assert.Equal(3.0, audit.AverageLeafDepth);
            Assert.Contains("Terminal Sensor", audit.DeepestLeafTitles[0]);
            Assert.Equal("Balanced Tree", audit.EccentricityCategory);
            Assert.Contains("Max depth 3 (top: 'Terminal Sensor')", audit.Summary);
        }

        [Fact]
        public void Run156_AuditRadialAngularSpacing()
        {
            // 1. Empty / 0-1 nodes
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            var emptyAudit = vm.AuditRadialAngularSpacing();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal("Insufficient Nodes", emptyAudit.DispersionCategory);
            Assert.Contains("Insufficient nodes", emptyAudit.Summary);

            // 2. 4 symmetrically placed orthogonal branches around root (0°, 90°, 180°, 270°)
            vm.Document.RootNodeId = "root";
            var root = new MindMapNode { Id = "root", Title = "Core Hub", X = 0, Y = 0 };
            var east = new MindMapNode { Id = "e", Title = "East Node", X = 100, Y = 0 };
            var north = new MindMapNode { Id = "n", Title = "North Node", X = 0, Y = 100 };
            var west = new MindMapNode { Id = "w", Title = "West Node", X = -100, Y = 0 };
            var south = new MindMapNode { Id = "s", Title = "South Node", X = 0, Y = -100 };

            vm.Nodes.Add(new MindMapNodeViewModel(root));
            vm.Nodes.Add(new MindMapNodeViewModel(east));
            vm.Nodes.Add(new MindMapNodeViewModel(north));
            vm.Nodes.Add(new MindMapNodeViewModel(west));
            vm.Nodes.Add(new MindMapNodeViewModel(south));

            var audit = vm.AuditRadialAngularSpacing();
            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal(4, audit.BranchCount);
            Assert.Equal(90.0, audit.AverageGapDegrees);
            Assert.Equal(90.0, audit.MinGapDegrees);
            Assert.Equal(90.0, audit.MaxGapDegrees);
            Assert.Equal(0.0, audit.GapStandardDeviation);
            Assert.Equal("Uniform Radial Dispersion (≤15° SD)", audit.DispersionCategory);
            Assert.Contains("avg gap 90°", audit.Summary);
            Assert.Contains("Uniform Radial Dispersion", audit.Summary);
        }

        [Fact]
        public void Run157_AuditClusterCohesionAndDensity()
        {
            // 1. Empty / degenerate vault
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            var emptyAudit = vm.AuditClusterCohesionAndDensity();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.ClusterCount);
            Assert.Equal("Insufficient Nodes", emptyAudit.CohesionCategory);
            Assert.Contains("Insufficient nodes", emptyAudit.Summary);

            // 2. 2 Tag clusters: "Frontend" (tight) and "Backend" (tight)
            var n1 = new MindMapNode { Id = "fe1", Title = "UI Shell", X = 100, Y = 100, Tags = new List<string> { "frontend" } };
            var n2 = new MindMapNode { Id = "fe2", Title = "Style System", X = 120, Y = 110, Tags = new List<string> { "frontend" } };
            var n3 = new MindMapNode { Id = "be1", Title = "API Server", X = 500, Y = 500, Tags = new List<string> { "backend" } };
            var n4 = new MindMapNode { Id = "be2", Title = "Database", X = 520, Y = 510, Tags = new List<string> { "backend" } };
            var orphan = new MindMapNode { Id = "orp", Title = "Scratch Note", X = 900, Y = 900 };

            vm.Nodes.Add(new MindMapNodeViewModel(n1));
            vm.Nodes.Add(new MindMapNodeViewModel(n2));
            vm.Nodes.Add(new MindMapNodeViewModel(n3));
            vm.Nodes.Add(new MindMapNodeViewModel(n4));
            vm.Nodes.Add(new MindMapNodeViewModel(orphan));

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "fe1", TargetNodeId = "fe2" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "be1", TargetNodeId = "be2" };
            vm.Links.Add(new MindMapLinkViewModel(l1));
            vm.Links.Add(new MindMapLinkViewModel(l2));

            var audit = vm.AuditClusterCohesionAndDensity();
            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal(4, audit.ClusteredNodes);
            Assert.Equal(1, audit.OrphanNodes);
            Assert.Equal(2, audit.ClusterCount);
            Assert.True(audit.GlobalCohesionScore >= 0.70, $"Score {audit.GlobalCohesionScore} should be >= 0.70");
            Assert.Equal("Tight Semantic Constellations", audit.CohesionCategory);
            Assert.Contains("2 clusters, 5 nodes", audit.Summary);
            Assert.Contains("1 untagged", audit.Summary);

            // Model overload test
            var staticAudit = MindMapStudioViewModel.AuditClusterCohesionAndDensity(
                new[] { n1, n2, n3, n4, orphan }, new[] { l1, l2 });
            Assert.Equal(2, staticAudit.ClusterCount);
            Assert.Equal(audit.GlobalCohesionScore, staticAudit.GlobalCohesionScore);
        }

        [Fact]
        public void Run158_AuditBipartitePartitioning_BipartiteAndOddCycle()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            // 1. Empty map
            var emptyAudit = vm.AuditBipartitePartitioning();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.True(emptyAudit.IsBipartite);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Clean bipartite graph: Producer -> Consumer (A1 -> B1, A1 -> B2, A2 -> B2)
            var a1 = new MindMapNode { Id = "a1", Title = "Producer 1" };
            var a2 = new MindMapNode { Id = "a2", Title = "Producer 2" };
            var b1 = new MindMapNode { Id = "b1", Title = "Consumer 1" };
            var b2 = new MindMapNode { Id = "b2", Title = "Consumer 2" };

            vm.Nodes.Add(new MindMapNodeViewModel(a1));
            vm.Nodes.Add(new MindMapNodeViewModel(a2));
            vm.Nodes.Add(new MindMapNodeViewModel(b1));
            vm.Nodes.Add(new MindMapNodeViewModel(b2));

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "a1", TargetNodeId = "b1" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "a1", TargetNodeId = "b2" };
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "a2", TargetNodeId = "b2" };

            vm.Links.Add(new MindMapLinkViewModel(l1));
            vm.Links.Add(new MindMapLinkViewModel(l2));
            vm.Links.Add(new MindMapLinkViewModel(l3));

            var bipAudit = vm.AuditBipartitePartitioning();
            Assert.True(bipAudit.IsBipartite);
            Assert.Equal(4, bipAudit.TotalNodes);
            Assert.Equal(3, bipAudit.TotalLinks);
            Assert.Equal(2, bipAudit.SetACount);
            Assert.Equal(2, bipAudit.SetBCount);
            Assert.Equal(1.0, bipAudit.PartitionBalance);
            Assert.Equal("Balanced Bipartite Partition", bipAudit.Category);
            Assert.Contains("Set A = 2, Set B = 2", bipAudit.Summary);

            // Static overload test
            var staticBip = MindMapStudioViewModel.AuditBipartitePartitioning(
                new[] { a1, a2, b1, b2 }, new[] { l1, l2, l3 });
            Assert.True(staticBip.IsBipartite);
            Assert.Equal(bipAudit.PartitionBalance, staticBip.PartitionBalance);

            // 3. Odd cycle (Triangle: a1 - b1 - c1 - a1)
            var c1 = new MindMapNode { Id = "c1", Title = "Node C" };
            vm.Nodes.Add(new MindMapNodeViewModel(c1));
            var l4 = new MindMapLink { Id = "l4", SourceNodeId = "b1", TargetNodeId = "c1" };
            var l5 = new MindMapLink { Id = "l5", SourceNodeId = "c1", TargetNodeId = "a1" };
            vm.Links.Add(new MindMapLinkViewModel(l4));
            vm.Links.Add(new MindMapLinkViewModel(l5));

            var oddAudit = vm.AuditBipartitePartitioning();
            Assert.False(oddAudit.IsBipartite);
            Assert.Equal("Non-Bipartite (Odd Cycle)", oddAudit.Category);
            Assert.Contains("Odd cycle", oddAudit.OddCycleDescription);
        }

        [Fact]
        public void Run159_AuditTopologicalOrderAndDepth_AcyclicAndCycleDetection()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            // 1. Empty map
            var emptyAudit = vm.AuditTopologicalOrderAndDepth();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.True(emptyAudit.IsAcyclic);
            Assert.Equal(0, emptyAudit.MaxSequenceDepth);
            Assert.Equal("Empty Graph", emptyAudit.Category);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Linear DAG pipeline (A -> B -> C)
            var nA = new MindMapNode { Id = "na", Title = "Source Tier" };
            var nB = new MindMapNode { Id = "nb", Title = "Transform Tier" };
            var nC = new MindMapNode { Id = "nc", Title = "Sink Tier" };
            vm.Nodes.Add(new MindMapNodeViewModel(nA));
            vm.Nodes.Add(new MindMapNodeViewModel(nB));
            vm.Nodes.Add(new MindMapNodeViewModel(nC));

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "na", TargetNodeId = "nb" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "nb", TargetNodeId = "nc" };
            vm.Links.Add(new MindMapLinkViewModel(l1));
            vm.Links.Add(new MindMapLinkViewModel(l2));

            var dagAudit = vm.AuditTopologicalOrderAndDepth();
            Assert.Equal(3, dagAudit.TotalNodes);
            Assert.Equal(2, dagAudit.TotalLinks);
            Assert.True(dagAudit.IsAcyclic);
            Assert.Equal(2, dagAudit.MaxSequenceDepth);
            Assert.Equal(1, dagAudit.EntryPointsCount);
            Assert.Equal(1, dagAudit.TerminalSinksCount);
            Assert.Equal(3, dagAudit.OrderedNodeTitles.Count);
            Assert.Equal("Source Tier", dagAudit.OrderedNodeTitles[0]);
            Assert.Equal("Sink Tier", dagAudit.OrderedNodeTitles[2]);
            Assert.Equal("Linear Sequential Pipeline", dagAudit.Category);

            // Static overload
            var staticAudit = MindMapStudioViewModel.AuditTopologicalOrderAndDepth(
                new[] { nA, nB, nC }, new[] { l1, l2 });
            Assert.True(staticAudit.IsAcyclic);
            Assert.Equal(2, staticAudit.MaxSequenceDepth);

            // 3. Cyclic feedback loop (C -> A)
            var lCycle = new MindMapLink { Id = "lc", SourceNodeId = "nc", TargetNodeId = "na" };
            vm.Links.Add(new MindMapLinkViewModel(lCycle));

            var cycleAudit = vm.AuditTopologicalOrderAndDepth();
            Assert.False(cycleAudit.IsAcyclic);
            Assert.Equal("Cyclic Dependency Loop", cycleAudit.Category);
            Assert.Contains("Cycle in:", cycleAudit.CycleDescription);
        }

        [Fact]
        public void Run160_AuditGraphBridgesAndArticulationPoints_TarjanConnectivity()
        {
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();

            // 1. Empty map
            var emptyAudit = vm.AuditGraphBridgesAndArticulationPoints();
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.BridgeLinkCount);
            Assert.Equal("Empty Graph", emptyAudit.VulnerabilityCategory);

            // 2. Pure tree hierarchy (Root -> Child 1, Root -> Child 2)
            var nRoot = new MindMapNode { Id = "root", Title = "Central Concept" };
            var n1 = new MindMapNode { Id = "n1", Title = "Branch Alpha" };
            var n2 = new MindMapNode { Id = "n2", Title = "Branch Beta" };
            vm.Nodes.Add(new MindMapNodeViewModel(nRoot));
            vm.Nodes.Add(new MindMapNodeViewModel(n1));
            vm.Nodes.Add(new MindMapNodeViewModel(n2));

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "root", TargetNodeId = "n1" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "root", TargetNodeId = "n2" };
            vm.Links.Add(new MindMapLinkViewModel(l1));
            vm.Links.Add(new MindMapLinkViewModel(l2));

            var treeAudit = vm.AuditGraphBridgesAndArticulationPoints();
            Assert.Equal(3, treeAudit.TotalNodes);
            Assert.Equal(2, treeAudit.TotalLinks);
            Assert.Equal(2, treeAudit.BridgeLinkCount);
            Assert.Equal(1, treeAudit.ArticulationPointCount);
            Assert.Contains("Central Concept", treeAudit.ArticulationNodeTitles);
            Assert.Equal(100.0, treeAudit.StructuralFragilityIndex);
            Assert.Equal("Pure Tree Hierarchy (Max Fragility)", treeAudit.VulnerabilityCategory);

            // Static overload
            var staticAudit = MindMapStudioViewModel.AuditGraphBridgesAndArticulationPoints(
                new[] { nRoot, n1, n2 }, new[] { l1, l2 });
            Assert.Equal(2, staticAudit.BridgeLinkCount);
            Assert.Equal(1, staticAudit.ArticulationPointCount);

            // 3. Resilient triangle mesh (no bridges, no articulation points)
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "n1", TargetNodeId = "n2" };
            vm.Links.Add(new MindMapLinkViewModel(l3));

            var meshAudit = vm.AuditGraphBridgesAndArticulationPoints();
            Assert.Equal(3, meshAudit.TotalNodes);
            Assert.Equal(3, meshAudit.TotalLinks);
            Assert.Equal(0, meshAudit.BridgeLinkCount);
            Assert.Equal(0, meshAudit.ArticulationPointCount);
            Assert.Equal(0.0, meshAudit.StructuralFragilityIndex);
            Assert.Equal("Robust Resilient Mesh (0 Single Points of Failure)", meshAudit.VulnerabilityCategory);
        }

        [Fact]
        public void Run161_AuditNetworkSpanningBackboneAndRedundancy_ComputesBackboneAndRedundantChords()
        {
            // 1. Empty map
            var emptyAudit = MindMapStudioViewModel.AuditNetworkSpanningBackboneAndRedundancy(
                Array.Empty<MindMapNode>(), Array.Empty<MindMapLink>());
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalLinks);
            Assert.Equal("Empty MindMap", emptyAudit.TopologyCategory);

            // 2. Pure tree hierarchy (0% redundancy)
            var nRoot = new MindMapNode { Id = "root", Title = "Central Concept", X = 0, Y = 0 };
            var n1 = new MindMapNode { Id = "n1", Title = "Branch Alpha", X = 100, Y = 0 };
            var n2 = new MindMapNode { Id = "n2", Title = "Branch Beta", X = 0, Y = 100 };

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "root", TargetNodeId = "n1" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "root", TargetNodeId = "n2" };

            var treeAudit = MindMapStudioViewModel.AuditNetworkSpanningBackboneAndRedundancy(
                new[] { nRoot, n1, n2 }, new[] { l1, l2 });
            Assert.Equal(3, treeAudit.TotalNodes);
            Assert.Equal(2, treeAudit.TotalLinks);
            Assert.Equal(2, treeAudit.BackboneLinkCount);
            Assert.Equal(0, treeAudit.RedundantChordCount);
            Assert.Equal(0.0, treeAudit.RedundancyPercentage);
            Assert.Equal("Optimal Tree (0% Redundancy)", treeAudit.TopologyCategory);

            // 3. Triangle mesh with redundant chord
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "n1", TargetNodeId = "n2" }; // Hypotenuse ~141.4px
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            vm.Nodes.Add(new MindMapNodeViewModel(nRoot));
            vm.Nodes.Add(new MindMapNodeViewModel(n1));
            vm.Nodes.Add(new MindMapNodeViewModel(n2));
            vm.Links.Add(new MindMapLinkViewModel(l1));
            vm.Links.Add(new MindMapLinkViewModel(l2));
            vm.Links.Add(new MindMapLinkViewModel(l3));

            var meshAudit = vm.AuditNetworkSpanningBackboneAndRedundancy();
            Assert.Equal(3, meshAudit.TotalNodes);
            Assert.Equal(3, meshAudit.TotalLinks);
            Assert.Equal(2, meshAudit.BackboneLinkCount);
            Assert.Equal(1, meshAudit.RedundantChordCount);
            Assert.True(meshAudit.RedundancyPercentage > 30.0);
            Assert.True(meshAudit.BackboneLength < meshAudit.TotalNetworkLength);
            Assert.Contains(meshAudit.Links, l => l.LinkId == "l3" && l.IsRedundantChord);
            Assert.Contains(meshAudit.Links, l => l.LinkId == "l1" && l.IsBackbone);
            Assert.Contains("redundant chord", meshAudit.Summary);
        }

        [Fact]
        public void Run162_AuditGraphModularityAndCommunities_DetectsClustersAndCalculatesQ()
        {
            // 1. Empty mindmap
            var emptyAudit = MindMapStudioViewModel.AuditGraphModularityAndCommunities(
                Array.Empty<MindMapNode>(), Array.Empty<MindMapLink>());
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalLinks);
            Assert.Equal(0, emptyAudit.CommunityCount);
            Assert.Equal(0.0, emptyAudit.ModularityQ);
            Assert.Equal("Empty Mindmap", emptyAudit.Category);

            // 2. Disconnected components / distinct clusters
            // Cluster A: a1, a2, a3 in triangle
            var a1 = new MindMapNode { Id = "a1", Title = "Cluster A1" };
            var a2 = new MindMapNode { Id = "a2", Title = "Cluster A2" };
            var a3 = new MindMapNode { Id = "a3", Title = "Cluster A3" };
            var la1 = new MindMapLink { Id = "la1", SourceNodeId = "a1", TargetNodeId = "a2" };
            var la2 = new MindMapLink { Id = "la2", SourceNodeId = "a2", TargetNodeId = "a3" };
            var la3 = new MindMapLink { Id = "la3", SourceNodeId = "a3", TargetNodeId = "a1" };

            // Cluster B: b1, b2, b3 in triangle
            var b1 = new MindMapNode { Id = "b1", Title = "Cluster B1" };
            var b2 = new MindMapNode { Id = "b2", Title = "Cluster B2" };
            var b3 = new MindMapNode { Id = "b3", Title = "Cluster B3" };
            var lb1 = new MindMapLink { Id = "lb1", SourceNodeId = "b1", TargetNodeId = "b2" };
            var lb2 = new MindMapLink { Id = "lb2", SourceNodeId = "b2", TargetNodeId = "b3" };
            var lb3 = new MindMapLink { Id = "lb3", SourceNodeId = "b3", TargetNodeId = "b1" };

            var nodes = new[] { a1, a2, a3, b1, b2, b3 };
            var links = new[] { la1, la2, la3, lb1, lb2, lb3 };

            // Static model overload
            var staticAudit = MindMapStudioViewModel.AuditGraphModularityAndCommunities(nodes, links);
            Assert.Equal(6, staticAudit.TotalNodes);
            Assert.Equal(6, staticAudit.TotalLinks);
            Assert.Equal(2, staticAudit.CommunityCount);
            Assert.True(staticAudit.ModularityQ > 0.0);
            Assert.Equal(0, staticAudit.BridgingNodeCount);
            Assert.Contains("Q=", staticAudit.Summary);

            // VM instance overload
            var vm = new MindMapStudioViewModel();
            vm.Nodes.Clear();
            vm.Links.Clear();
            foreach (var n in nodes) vm.Nodes.Add(new MindMapNodeViewModel(n));
            foreach (var l in links) vm.Links.Add(new MindMapLinkViewModel(l));

            var vmAudit = vm.AuditGraphModularityAndCommunities();
            Assert.Equal(6, vmAudit.TotalNodes);
            Assert.Equal(6, vmAudit.TotalLinks);
            Assert.Equal(2, vmAudit.CommunityCount);
            Assert.Equal(staticAudit.ModularityQ, vmAudit.ModularityQ);
            Assert.Equal(staticAudit.Summary, MindMapStudioViewModel.FormatCommunitySummary(
                vmAudit.TotalNodes, vmAudit.TotalLinks, vmAudit.CommunityCount, vmAudit.ModularityQ,
                vmAudit.BridgingNodeCount, vmAudit.Category, vmAudit.IsSelectionScope));
        }

        [Fact]
        public void Run163_AuditGraphDiameterAndEccentricity_CalculatesHopsAndLongestPath()
        {
            // 1. Empty mindmap
            var emptyAudit = MindMapStudioViewModel.AuditGraphDiameterAndEccentricity(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.GraphDiameter);
            Assert.Equal("Empty Mindmap", emptyAudit.DiameterCategory);

            // 2. Single node
            var n1 = new MindMapNode { Id = "n1", Title = "Root Core" };
            var singleAudit = MindMapStudioViewModel.AuditGraphDiameterAndEccentricity(new[] { n1 }, null);
            Assert.Equal(1, singleAudit.TotalNodes);
            Assert.Equal(0, singleAudit.GraphDiameter);
            Assert.Equal(0, singleAudit.GraphRadius);
            Assert.Equal("Single Node Hub", singleAudit.DiameterCategory);

            // 3. Chain network: A - B - C - D (diameter = 3)
            var a = new MindMapNode { Id = "a", Title = "Alpha" };
            var b = new MindMapNode { Id = "b", Title = "Beta" };
            var c = new MindMapNode { Id = "c", Title = "Gamma" };
            var d = new MindMapNode { Id = "d", Title = "Delta" };
            var lab = new MindMapLink { Id = "lab", SourceNodeId = "a", TargetNodeId = "b" };
            var lbc = new MindMapLink { Id = "lbc", SourceNodeId = "b", TargetNodeId = "c" };
            var lcd = new MindMapLink { Id = "lcd", SourceNodeId = "c", TargetNodeId = "d" };

            var nodes = new[] { a, b, c, d };
            var links = new[] { lab, lbc, lcd };

            var chainAudit = MindMapStudioViewModel.AuditGraphDiameterAndEccentricity(nodes, links);
            Assert.Equal(4, chainAudit.TotalNodes);
            Assert.Equal(3, chainAudit.TotalLinks);
            Assert.Equal(1, chainAudit.ConnectedComponents);
            Assert.Equal(3, chainAudit.GraphDiameter);
            Assert.Equal(2, chainAudit.GraphRadius);
            Assert.Equal(2, chainAudit.PeripheralNodeCount); // a and d
            Assert.Equal(2, chainAudit.CentralNodeCount);    // b and c
            Assert.Equal(4, chainAudit.LongestConceptPath.Count);
            Assert.Equal("Alpha", chainAudit.LongestConceptPath[0]);
            Assert.Equal("Delta", chainAudit.LongestConceptPath[3]);
            Assert.Contains("Balanced Semantic Network", chainAudit.DiameterCategory);

            // 4. Format summary consistency
            string formatted = MindMapStudioViewModel.FormatDiameterEccentricitySummary(
                chainAudit.TotalNodes, chainAudit.TotalLinks, chainAudit.ConnectedComponents,
                chainAudit.GraphDiameter, chainAudit.GraphRadius, chainAudit.PeripheralNodeCount,
                chainAudit.CentralNodeCount, chainAudit.LongestConceptPath, chainAudit.DiameterCategory, chainAudit.IsSelectionScope);
            Assert.Equal(chainAudit.Summary, formatted);
        }

        [Fact]
        public void Run164_AuditClosenessCentralityAndHarmonicDistance_CalculatesFlowAndDisseminationHubs()
        {
            // 1. Empty mindmap
            var emptyAudit = MindMapStudioViewModel.AuditClosenessCentralityAndHarmonicDistance(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0.0, emptyAudit.MeanHarmonicCentrality);
            Assert.Equal("Empty MindMap", emptyAudit.EfficiencyCategory);

            // 2. Single node
            var n1 = new MindMapNode { Id = "n1", Title = "Root Core" };
            var singleAudit = MindMapStudioViewModel.AuditClosenessCentralityAndHarmonicDistance(new[] { n1 }, null);
            Assert.Equal(1, singleAudit.TotalNodes);
            Assert.Equal(1.0, singleAudit.MeanHarmonicCentrality);
            Assert.Equal(100.0, singleAudit.GraphEfficiency);
            Assert.Equal("Single Isolated Node", singleAudit.EfficiencyCategory);

            // 3. Star network (hub in center): Center - Leaf1, Center - Leaf2, Center - Leaf3
            var center = new MindMapNode { Id = "c", Title = "Central Hub" };
            var l1 = new MindMapNode { Id = "l1", Title = "Leaf 1" };
            var l2 = new MindMapNode { Id = "l2", Title = "Leaf 2" };
            var l3 = new MindMapNode { Id = "l3", Title = "Leaf 3" };

            var e1 = new MindMapLink { Id = "e1", SourceNodeId = "c", TargetNodeId = "l1" };
            var e2 = new MindMapLink { Id = "e2", SourceNodeId = "c", TargetNodeId = "l2" };
            var e3 = new MindMapLink { Id = "e3", SourceNodeId = "c", TargetNodeId = "l3" };

            var starAudit = MindMapStudioViewModel.AuditClosenessCentralityAndHarmonicDistance(
                new[] { center, l1, l2, l3 }, new[] { e1, e2, e3 });

            Assert.Equal(4, starAudit.TotalNodes);
            Assert.Equal(3, starAudit.TotalLinks);
            Assert.Equal(1, starAudit.ConnectedComponents);
            Assert.Contains("Central Hub", starAudit.OptimalHubTitles);

            var centerMetric = starAudit.NodeMetrics.First(m => m.Title == "Central Hub");
            Assert.Equal(1.0, centerMetric.ClosenessCentrality);
            Assert.Equal(1.0, centerMetric.HarmonicCentrality);
            Assert.Equal(1.0, centerMetric.MeanDistance);
            Assert.True(centerMetric.IsOptimalDisseminationHub);

            var leafMetric = starAudit.NodeMetrics.First(m => m.Title == "Leaf 1");
            Assert.True(leafMetric.HarmonicCentrality < centerMetric.HarmonicCentrality);
            Assert.False(leafMetric.IsOptimalDisseminationHub);

            // 4. Formatter test
            string formatted = MindMapStudioViewModel.FormatClosenessCentralitySummary(
                starAudit.TotalNodes, starAudit.TotalLinks, starAudit.ConnectedComponents,
                starAudit.MeanHarmonicCentrality, starAudit.GraphEfficiency,
                starAudit.OptimalHubTitles, starAudit.PeripheralTitles.Count, starAudit.EfficiencyCategory, starAudit.IsSelectionScope);
            Assert.Equal(starAudit.Summary, formatted);
        }

        [Fact]
        public void Run165_AuditBetweennessCentralityAndBridges_CalculatesBrandesCentralityAndDetectsBridges()
        {
            // 1. Empty graph
            var emptyAudit = MindMapStudioViewModel.AuditBetweennessCentralityAndBridges(Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0.0, emptyAudit.MeanBetweenness);
            Assert.Equal("Empty Graph", emptyAudit.BottleneckCategory);

            // 2. Linear 3-node path: A - B - C
            var nodeA = new MindMapNode { Id = "a", Title = "Node A" };
            var nodeB = new MindMapNode { Id = "b", Title = "Bridge Node B" };
            var nodeC = new MindMapNode { Id = "c", Title = "Node C" };

            var linkAB = new MindMapLink { Id = "e1", SourceNodeId = "a", TargetNodeId = "b" };
            var linkBC = new MindMapLink { Id = "e2", SourceNodeId = "b", TargetNodeId = "c" };

            var pathAudit = MindMapStudioViewModel.AuditBetweennessCentralityAndBridges(
                new[] { nodeA, nodeB, nodeC }, new[] { linkAB, linkBC });

            Assert.Equal(3, pathAudit.TotalNodes);
            Assert.Equal(2, pathAudit.TotalLinks);
            Assert.Equal(1, pathAudit.ConnectedComponents);
            Assert.Equal(1, pathAudit.BottleneckNodeCount);
            Assert.Contains("Bridge Node B", pathAudit.BottleneckNodeTitles);

            var metricB = pathAudit.NodeMetrics.First(m => m.Title == "Bridge Node B");
            Assert.Equal(1.0, metricB.RawBetweenness);
            Assert.Equal(1.0, metricB.NormalizedBetweenness);
            Assert.True(metricB.IsCriticalBottleneck);

            var metricA = pathAudit.NodeMetrics.First(m => m.Title == "Node A");
            Assert.Equal(0.0, metricA.NormalizedBetweenness);
            Assert.False(metricA.IsCriticalBottleneck);

            // 3. Formatter test
            string formatted = MindMapStudioViewModel.FormatBetweennessCentralitySummary(
                pathAudit.TotalNodes, pathAudit.TotalLinks, pathAudit.ConnectedComponents,
                pathAudit.MaxBetweenness, pathAudit.MeanBetweenness,
                pathAudit.BottleneckNodeTitles, pathAudit.BottleneckNodeCount, pathAudit.BottleneckCategory,
                pathAudit.IsSelectionScope);
            Assert.Equal(pathAudit.Summary, formatted);
        }

        [Fact]
        public void Run166_AuditEigenvectorCentrality_CalculatesPowerIterationAndInfluence()
        {
            // 1. Empty graph
            var emptyAudit = MindMapStudioViewModel.AuditEigenvectorCentrality(Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0.0, emptyAudit.PrincipalEigenvalueEstimate);
            Assert.Equal("Empty Mindmap", emptyAudit.NetworkStructureCategory);

            // 2. Star graph with hub A and leaves B, C, D
            var nodeA = new MindMapNode { Id = "hub", Title = "Central Hub A" };
            var nodeB = new MindMapNode { Id = "b", Title = "Leaf B" };
            var nodeC = new MindMapNode { Id = "c", Title = "Leaf C" };
            var nodeD = new MindMapNode { Id = "d", Title = "Leaf D" };

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "hub", TargetNodeId = "b" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "hub", TargetNodeId = "c" };
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "hub", TargetNodeId = "d" };

            var starAudit = MindMapStudioViewModel.AuditEigenvectorCentrality(
                new[] { nodeA, nodeB, nodeC, nodeD }, new[] { l1, l2, l3 });

            Assert.Equal(4, starAudit.TotalNodes);
            Assert.Equal(3, starAudit.TotalLinks);
            Assert.Equal(1, starAudit.ConnectedComponents);
            Assert.True(starAudit.HasConverged);
            Assert.True(starAudit.MaxInfluencePercent > 25.0);
            Assert.Contains("Central Hub A", starAudit.TopInfluencerTitles);

            var hubMetric = starAudit.NodeMetrics.First(m => m.Title == "Central Hub A");
            Assert.Equal(3, hubMetric.Degree);
            Assert.True(hubMetric.NormalizedInfluencePercent > 35.0);
            Assert.Equal("Dominant Hub", hubMetric.InfluenceTier);

            // 3. Formatter test
            string formatted = MindMapStudioViewModel.FormatEigenvectorCentralitySummary(
                starAudit.TotalNodes, starAudit.TotalLinks, starAudit.ConnectedComponents,
                starAudit.PrincipalEigenvalueEstimate, starAudit.MaxInfluencePercent,
                starAudit.TopInfluencerTitles, starAudit.TopInfluencerTitles.Count,
                starAudit.NetworkStructureCategory, starAudit.IsSelectionScope);
            Assert.Equal(starAudit.Summary, formatted);
        }

        [Fact]
        public void Run167_AuditRadialLayoutAngularSpacing_EvaluatesEquidistantSpacingAndRings()
        {
            // 1. Empty mindmap
            var emptyAudit = MindMapStudioViewModel.AuditRadialLayoutAngularSpacing(
                Array.Empty<MindMapNode>(), Array.Empty<MindMapLink>());
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.RingCount);
            Assert.Equal(100.0, emptyAudit.OverallLayoutFidelityScore);
            Assert.Equal("Empty Galaxy", emptyAudit.LayoutCategory);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Symmetric 4-leaf radial ring around root at (0,0)
            var root = new MindMapNode { Id = "root", Title = "Root Node", X = 0, Y = 0 };
            var n1 = new MindMapNode { Id = "n1", Title = "East Node", X = 100, Y = 0 };
            var n2 = new MindMapNode { Id = "n2", Title = "North Node", X = 0, Y = 100 };
            var n3 = new MindMapNode { Id = "n3", Title = "West Node", X = -100, Y = 0 };
            var n4 = new MindMapNode { Id = "n4", Title = "South Node", X = 0, Y = -100 };

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "root", TargetNodeId = "n1" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "root", TargetNodeId = "n2" };
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "root", TargetNodeId = "n3" };
            var l4 = new MindMapLink { Id = "l4", SourceNodeId = "root", TargetNodeId = "n4" };

            var radialAudit = MindMapStudioViewModel.AuditRadialLayoutAngularSpacing(
                new[] { root, n1, n2, n3, n4 }, new[] { l1, l2, l3, l4 });

            Assert.Equal(5, radialAudit.TotalNodes);
            Assert.Equal(4, radialAudit.TotalLinks);
            Assert.Equal(2, radialAudit.RingCount); // Depth 0 (root) and Depth 1
            Assert.Equal(0.0, radialAudit.MeanRadiusJitter);
            Assert.True(radialAudit.OverallAngularUniformityScore >= 95.0);
            Assert.True(radialAudit.OverallLayoutFidelityScore >= 85.0);
            Assert.Equal("Harmonic Equidistant Radial Layout", radialAudit.LayoutCategory);
            Assert.Empty(radialAudit.CrowdedNodeTitles);

            // Ring 1 check
            var ring1 = radialAudit.Rings.First(r => r.DepthRing == 1);
            Assert.Equal(4, ring1.NodeCount);
            Assert.Equal(100.0, ring1.MeanRadius);
            Assert.Equal(0.0, ring1.RadiusJitterStdDev);
            Assert.Equal(90.0, ring1.MeanAngularSpacingDegrees);

            // 3. Formatter test
            string formatted = MindMapStudioViewModel.FormatRadialLayoutSummary(
                radialAudit.TotalNodes, radialAudit.RingCount, radialAudit.OverallAngularUniformityScore,
                radialAudit.MeanRadiusJitter, radialAudit.OverallLayoutFidelityScore,
                radialAudit.LayoutCategory, radialAudit.CrowdedNodeTitles.Count, radialAudit.IsSelectionScope);
            Assert.Equal(radialAudit.Summary, formatted);
        }

        [Fact]
        public void GraphClusteringAndTransitivity_Audits_Properly()
        {
            // 1. Empty graph
            var emptyAudit = MindMapStudioViewModel.AuditClusteringCoefficientAndTransitivity(
                Array.Empty<MindMapNode>(), Array.Empty<MindMapLink>());
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalLinks);
            Assert.Equal(100.0, emptyAudit.ClusteringFidelityScore);
            Assert.Equal("Empty Galaxy", emptyAudit.TopologyCategory);

            // 2. Triangle clique (n1 - n2 - n3 - n1) -> 1 triangle, 3 nodes, 3 links, 100% transitivity
            var n1 = new MindMapNode { Id = "n1", Title = "Node 1" };
            var n2 = new MindMapNode { Id = "n2", Title = "Node 2" };
            var n3 = new MindMapNode { Id = "n3", Title = "Node 3" };

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "n1", TargetNodeId = "n2" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "n2", TargetNodeId = "n3" };
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "n3", TargetNodeId = "n1" };

            var triangleAudit = MindMapStudioViewModel.AuditClusteringCoefficientAndTransitivity(
                new[] { n1, n2, n3 }, new[] { l1, l2, l3 });

            Assert.Equal(3, triangleAudit.TotalNodes);
            Assert.Equal(3, triangleAudit.TotalLinks);
            Assert.Equal(1, triangleAudit.TotalTriangles);
            Assert.Equal(100.0, triangleAudit.GlobalTransitivity);
            Assert.Equal(100.0, triangleAudit.MeanClusteringCoefficient);
            Assert.Equal("Dense Triadic Mesh (High Semantic Clustering)", triangleAudit.TopologyCategory);

            // 3. Tree topology (n1 - n2 and n1 - n3, but no edge n2 - n3) -> 0 triangles, 1 open triplet
            var treeAudit = MindMapStudioViewModel.AuditClusteringCoefficientAndTransitivity(
                new[] { n1, n2, n3 }, new[] { l1, l2 });

            Assert.Equal(3, treeAudit.TotalNodes);
            Assert.Equal(2, treeAudit.TotalLinks);
            Assert.Equal(0, treeAudit.TotalTriangles);
            Assert.Equal(1, treeAudit.TotalOpenTriplets);
            Assert.Equal(0.0, treeAudit.GlobalTransitivity);
            Assert.Equal("Acyclic Tree Hierarchy (0 Triangles)", treeAudit.TopologyCategory);
            Assert.Equal(95.0, treeAudit.ClusteringFidelityScore);
        }

        [Fact]
        public void Run169_AuditSpectralBisectionAndAlgebraicConnectivity_ComputesFiedlerAndConductance()
        {
            // 1. Empty graph
            var emptyAudit = MindMapStudioViewModel.AuditSpectralBisectionAndAlgebraicConnectivity(
                Array.Empty<MindMapNode>(), Array.Empty<MindMapLink>());

            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalLinks);
            Assert.Equal(0.0, emptyAudit.AlgebraicConnectivity);
            Assert.Equal(100.0, emptyAudit.SpectralFidelityScore);
            Assert.Equal("Empty Graph", emptyAudit.SpectralRobustnessCategory);

            // 2. Disconnected graph (2 separate islands: n1-n2 and n3-n4)
            var n1 = new MindMapNode { Id = "n1", Title = "Node 1" };
            var n2 = new MindMapNode { Id = "n2", Title = "Node 2" };
            var n3 = new MindMapNode { Id = "n3", Title = "Node 3" };
            var n4 = new MindMapNode { Id = "n4", Title = "Node 4" };

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "n1", TargetNodeId = "n2" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "n3", TargetNodeId = "n4" };

            var disconAudit = MindMapStudioViewModel.AuditSpectralBisectionAndAlgebraicConnectivity(
                new[] { n1, n2, n3, n4 }, new[] { l1, l2 });

            Assert.Equal(4, disconAudit.TotalNodes);
            Assert.Equal(2, disconAudit.TotalLinks);
            Assert.Equal(2, disconAudit.ConnectedComponents);
            Assert.Equal(0.0, disconAudit.AlgebraicConnectivity);
            Assert.Contains("Disconnected Topology", disconAudit.SpectralRobustnessCategory);
            Assert.Equal(0, disconAudit.CutEdgesCount);

            // 3. Connected graph: path (n1 - n2 - n3 - n4)
            var bridgeLink = new MindMapLink { Id = "l_bridge", SourceNodeId = "n2", TargetNodeId = "n3" };
            var connectedAudit = MindMapStudioViewModel.AuditSpectralBisectionAndAlgebraicConnectivity(
                new[] { n1, n2, n3, n4 }, new[] { l1, l2, bridgeLink });

            Assert.Equal(4, connectedAudit.TotalNodes);
            Assert.Equal(3, connectedAudit.TotalLinks);
            Assert.Equal(1, connectedAudit.ConnectedComponents);
            Assert.True(connectedAudit.AlgebraicConnectivity > 0.0);
            Assert.True(connectedAudit.PartitionASize > 0);
            Assert.True(connectedAudit.PartitionBSize > 0);
            Assert.True(connectedAudit.CutEdgesCount >= 1);
            Assert.True(connectedAudit.Conductance > 0.0);
            Assert.NotEmpty(connectedAudit.BoundaryBridgeNodeTitles);
        }

        [Fact]
        public void Run170_AuditBiconnectedComponentsAndBridges_DecomposesBCCsAndDetectsCutVertices()
        {
            // 1. Empty graph
            var emptyAudit = MindMapStudioViewModel.AuditBiconnectedComponentsAndBridges(Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.BiconnectivityScore);
            Assert.Equal("Empty Graph", emptyAudit.TopologyCategory);

            // 2. Cycle graph (Triangle): A - B - C - A (2-connected, 0 cut-vertices, 0 bridges)
            var nA = new MindMapNode { Id = "a", Title = "Node A" };
            var nB = new MindMapNode { Id = "b", Title = "Node B" };
            var nC = new MindMapNode { Id = "c", Title = "Node C" };
            var lAB = new MindMapLink { Id = "l1", SourceNodeId = "a", TargetNodeId = "b" };
            var lBC = new MindMapLink { Id = "l2", SourceNodeId = "b", TargetNodeId = "c" };
            var lCA = new MindMapLink { Id = "l3", SourceNodeId = "c", TargetNodeId = "a" };

            var cycleAudit = MindMapStudioViewModel.AuditBiconnectedComponentsAndBridges(
                new[] { nA, nB, nC }, new[] { lAB, lBC, lCA });

            Assert.Equal(3, cycleAudit.TotalNodes);
            Assert.Equal(3, cycleAudit.TotalLinks);
            Assert.Equal(1, cycleAudit.ConnectedComponents);
            Assert.Equal(0, cycleAudit.ArticulationPointCount);
            Assert.Equal(0, cycleAudit.BridgeEdgeCount);
            Assert.Equal(1, cycleAudit.BiconnectedBlockCount);
            Assert.Equal("Robust 2-Connected Mesh (Zero Bottlenecks)", cycleAudit.TopologyCategory);
            Assert.Equal(100.0, cycleAudit.BiconnectivityScore);

            // 3. Tree graph: A - B - C (B is cut-vertex, l1 and l2 are bridges)
            var treeAudit = MindMapStudioViewModel.AuditBiconnectedComponentsAndBridges(
                new[] { nA, nB, nC }, new[] { lAB, lBC });

            Assert.Equal(3, treeAudit.TotalNodes);
            Assert.Equal(2, treeAudit.TotalLinks);
            Assert.Equal(1, treeAudit.ArticulationPointCount);
            Assert.Contains(treeAudit.ArticulationNodes, a => a.Title == "Node B");
            Assert.Equal(2, treeAudit.BridgeEdgeCount);
            Assert.Equal(2, treeAudit.BiconnectedBlockCount);
            Assert.Contains("Acyclic Tree Hierarchy (All Edges are Bridges)", treeAudit.TopologyCategory);

            // 4. Formatter test
            string formatted = MindMapStudioViewModel.FormatBiconnectivitySummary(
                treeAudit.TotalNodes, treeAudit.TotalLinks, treeAudit.ConnectedComponents,
                treeAudit.ArticulationPointCount, treeAudit.BridgeEdgeCount, treeAudit.BiconnectedBlockCount,
                treeAudit.BiconnectivityScore, treeAudit.TopologyCategory, treeAudit.IsSelectionScope);
            Assert.Equal(treeAudit.Summary, formatted);
        }

        [Fact]
        public void Run171_AuditKCoreDecompositionAndShells_ValidatesDegeneracyAndNucleus()
        {
            // 1. Empty graph
            var empty = MindMapStudioViewModel.AuditKCoreDecompositionAndShells(Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, empty.TotalNodes);
            Assert.Equal(0, empty.MaxCoreness);
            Assert.Equal("Empty Graph", empty.CoreStructureCategory);

            // 2. Triangle mesh (A-B-C-A) plus a pendant leaf D connected to C
            var nA = new MindMapNode { Id = "a", Title = "Nucleus Alpha" };
            var nB = new MindMapNode { Id = "b", Title = "Nucleus Beta" };
            var nC = new MindMapNode { Id = "c", Title = "Nucleus Gamma" };
            var nD = new MindMapNode { Id = "d", Title = "Leaf Delta" };

            var lAB = new MindMapLink { Id = "l1", SourceNodeId = "a", TargetNodeId = "b" };
            var lBC = new MindMapLink { Id = "l2", SourceNodeId = "b", TargetNodeId = "c" };
            var lCA = new MindMapLink { Id = "l3", SourceNodeId = "c", TargetNodeId = "a" };
            var lCD = new MindMapLink { Id = "l4", SourceNodeId = "c", TargetNodeId = "d" };

            var audit = MindMapStudioViewModel.AuditKCoreDecompositionAndShells(
                new[] { nA, nB, nC, nD },
                new[] { lAB, lBC, lCA, lCD });

            Assert.Equal(4, audit.TotalNodes);
            Assert.Equal(4, audit.TotalEdges);
            Assert.Equal(2, audit.MaxCoreness);
            Assert.Equal(3, audit.NucleusNodeCount);
            Assert.Equal(1, audit.PeripheralNodeCount);
            Assert.Equal(1.75, audit.MeanCoreness);
            Assert.Equal("Hierarchical Mesh / Ring", audit.CoreStructureCategory);

            var dInfo = audit.Nodes.First(n => n.NodeId == "d");
            Assert.Equal(1, dInfo.CorenessShell);
            Assert.True(dInfo.IsPeripheral);
            Assert.False(dInfo.IsInNucleus);

            var aInfo = audit.Nodes.First(n => n.NodeId == "a");
            Assert.Equal(2, aInfo.CorenessShell);
            Assert.True(aInfo.IsInNucleus);

            // 3. Formatter test
            string formatted = MindMapStudioViewModel.FormatKCoreSummary(
                audit.TotalNodes, audit.TotalEdges, audit.MaxCoreness, audit.NucleusNodeCount,
                audit.PeripheralNodeCount, audit.CorePeripheryRatio, audit.CoreStructureCategory,
                audit.IsSelectionScope, audit.Nodes);
            Assert.Equal(audit.Summary, formatted);
        }

        [Fact]
        public void Run172_AuditGraphAssortativityAndMixing_ValidatesHubCorrelationAndResilience()
        {
            var empty = MindMapStudioViewModel.AuditGraphAssortativityAndMixing(Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, empty.TotalNodes);
            Assert.Equal(0, empty.TotalEdges);
            Assert.Equal(100.0, empty.NetworkResilienceScore);
            Assert.Equal("Empty Graph", empty.NetworkMixingCategory);

            var nHub = new MindMapNode { Id = "hub", Title = "Central Topic" };
            var nL1 = new MindMapNode { Id = "l1", Title = "Leaf 1" };
            var nL2 = new MindMapNode { Id = "l2", Title = "Leaf 2" };
            var nL3 = new MindMapNode { Id = "l3", Title = "Leaf 3" };
            var nL4 = new MindMapNode { Id = "l4", Title = "Leaf 4" };

            var l1 = new MindMapLink { Id = "e1", SourceNodeId = "hub", TargetNodeId = "l1" };
            var l2 = new MindMapLink { Id = "e2", SourceNodeId = "hub", TargetNodeId = "l2" };
            var l3 = new MindMapLink { Id = "e3", SourceNodeId = "hub", TargetNodeId = "l3" };
            var l4 = new MindMapLink { Id = "e4", SourceNodeId = "hub", TargetNodeId = "l4" };

            var starAudit = MindMapStudioViewModel.AuditGraphAssortativityAndMixing(
                new[] { nHub, nL1, nL2, nL3, nL4 },
                new[] { l1, l2, l3, l4 });

            Assert.Equal(5, starAudit.TotalNodes);
            Assert.Equal(4, starAudit.TotalEdges);
            Assert.Equal(-1.0, starAudit.PearsonAssortativityCoefficient);
            Assert.Equal("Strongly Disassortative (Radial Star Hubs)", starAudit.NetworkMixingCategory);
            Assert.Equal(4, starAudit.HubToLeafEdges);
            Assert.Equal(0, starAudit.HubToHubEdges);

            var nH1 = new MindMapNode { Id = "h1", Title = "Hub 1" };
            var nH2 = new MindMapNode { Id = "h2", Title = "Hub 2" };
            var nLeaf1 = new MindMapNode { Id = "lf1", Title = "Leaf 1" };
            var nLeaf2 = new MindMapNode { Id = "lf2", Title = "Leaf 2" };

            var eCore = new MindMapLink { Id = "ec", SourceNodeId = "h1", TargetNodeId = "h2" };
            var eL1 = new MindMapLink { Id = "el1", SourceNodeId = "h1", TargetNodeId = "lf1" };
            var eL2 = new MindMapLink { Id = "el2", SourceNodeId = "h2", TargetNodeId = "lf2" };

            var coreAudit = MindMapStudioViewModel.AuditGraphAssortativityAndMixing(
                new[] { nH1, nH2, nLeaf1, nLeaf2 },
                new[] { eCore, eL1, eL2 });

            Assert.Equal(4, coreAudit.TotalNodes);
            Assert.Equal(3, coreAudit.TotalEdges);
            Assert.True(coreAudit.PearsonAssortativityCoefficient > starAudit.PearsonAssortativityCoefficient);

            string formatted = MindMapStudioViewModel.FormatAssortativitySummary(
                starAudit.TotalNodes, starAudit.TotalEdges, starAudit.PearsonAssortativityCoefficient,
                starAudit.HubToHubEdges, starAudit.HubToLeafEdges, starAudit.LeafToLeafEdges,
                starAudit.MeanDegree, starAudit.NetworkResilienceScore, starAudit.NetworkMixingCategory,
                starAudit.IsSelectionScope, starAudit.Nodes);
            Assert.Equal(starAudit.Summary, formatted);
        }

        [Fact]
        public void Run173_AuditClusteringCoefficientAndTriangles_EvaluatesWattsStrogatzClustering()
        {
            var emptyAudit = MindMapStudioViewModel.AuditClusteringCoefficientAndTriangles(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalLinks);
            Assert.Equal(0, emptyAudit.TotalTriangles);

            // Triangle graph: A - B - C - A
            var nA = new MindMapNode { Id = "A", Title = "Concept A" };
            var nB = new MindMapNode { Id = "B", Title = "Concept B" };
            var nC = new MindMapNode { Id = "C", Title = "Concept C" };

            var e1 = new MindMapLink { Id = "e1", SourceNodeId = "A", TargetNodeId = "B" };
            var e2 = new MindMapLink { Id = "e2", SourceNodeId = "B", TargetNodeId = "C" };
            var e3 = new MindMapLink { Id = "e3", SourceNodeId = "C", TargetNodeId = "A" };

            var triAudit = MindMapStudioViewModel.AuditClusteringCoefficientAndTriangles(
                new[] { nA, nB, nC },
                new[] { e1, e2, e3 });

            Assert.Equal(3, triAudit.TotalNodes);
            Assert.Equal(3, triAudit.TotalLinks);
            Assert.Equal(1, triAudit.TotalTriangles);
            Assert.Equal(100.0, triAudit.GlobalTransitivity);
            Assert.Equal(100.0, triAudit.MeanClusteringCoefficient);
        }

        [Fact]
        public void Run174_AuditCommunityModularityAndClusters_EvaluatesNewmanGirvanModularity()
        {
            var emptyAudit = MindMapStudioViewModel.AuditCommunityModularityAndClusters(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.Equal(0.0, emptyAudit.NewmanGirvanModularityQ);
            Assert.Equal(100.0, emptyAudit.ModularityHealthScore);
            Assert.Equal("Empty Galaxy", emptyAudit.ModularityCategory);

            // Two distinct communities with colors #FF0000 and #0000FF
            var a1 = new MindMapNode { Id = "a1", Title = "Cluster A1", ColorHex = "#FF0000" };
            var a2 = new MindMapNode { Id = "a2", Title = "Cluster A2", ColorHex = "#FF0000" };
            var b1 = new MindMapNode { Id = "b1", Title = "Cluster B1", ColorHex = "#0000FF" };
            var b2 = new MindMapNode { Id = "b2", Title = "Cluster B2", ColorHex = "#0000FF" };

            var eA = new MindMapLink { Id = "ea", SourceNodeId = "a1", TargetNodeId = "a2" };
            var eB = new MindMapLink { Id = "eb", SourceNodeId = "b1", TargetNodeId = "b2" };
            var eBridge = new MindMapLink { Id = "ebridge", SourceNodeId = "a1", TargetNodeId = "b1" };

            var audit = MindMapStudioViewModel.AuditCommunityModularityAndClusters(
                new[] { a1, a2, b1, b2 },
                new[] { eA, eB, eBridge });

            Assert.Equal(4, audit.TotalNodes);
            Assert.Equal(3, audit.TotalEdges);
            Assert.Equal(2, audit.CommunityCount);
            Assert.Equal(2, audit.IntraCommunityEdges);
            Assert.Equal(1, audit.InterCommunityBridgeEdges);
            Assert.True(audit.NewmanGirvanModularityQ > 0.0);
            Assert.Contains("intra-cluster edges", audit.Summary);

            string formatted = MindMapStudioViewModel.FormatModularitySummary(
                audit.TotalNodes, audit.TotalEdges, audit.CommunityCount, audit.NewmanGirvanModularityQ,
                audit.IntraCommunityEdges, audit.InterCommunityBridgeEdges, audit.ModularityHealthScore,
                audit.ModularityCategory, audit.IsSelectionScope);
            Assert.Equal(audit.Summary, formatted);
        }

        [Fact]
        public void Run175_AuditGraphCyclesAndFeedbackLoops_EvaluatesTarjanSCC()
        {
            var emptyAudit = MindMapStudioViewModel.AuditGraphCyclesAndFeedbackLoops(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.True(emptyAudit.IsPureDAG);
            Assert.Equal(0, emptyAudit.CyclicComponentCount);
            Assert.Equal(100.0, emptyAudit.AcyclicityScore);
            Assert.Equal("Empty Galaxy", emptyAudit.TopologyCategory);

            // Acyclic DAG: n1 -> n2 -> n3
            var n1 = new MindMapNode { Id = "n1", Title = "Root Conception" };
            var n2 = new MindMapNode { Id = "n2", Title = "Architecture Spec" };
            var n3 = new MindMapNode { Id = "n3", Title = "Implementation Task" };
            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "n1", TargetNodeId = "n2" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "n2", TargetNodeId = "n3" };

            var dagAudit = MindMapStudioViewModel.AuditGraphCyclesAndFeedbackLoops(
                new[] { n1, n2, n3 },
                new[] { l1, l2 });
            Assert.Equal(3, dagAudit.TotalNodes);
            Assert.Equal(2, dagAudit.TotalEdges);
            Assert.True(dagAudit.IsPureDAG);
            Assert.Equal(0, dagAudit.CyclicComponentCount);
            Assert.Equal(100.0, dagAudit.AcyclicityScore);
            Assert.Equal("Pure Directed Acyclic Graph (DAG)", dagAudit.TopologyCategory);

            // Add feedback loop: n3 -> n1
            var lCycle = new MindMapLink { Id = "lcycle", SourceNodeId = "n3", TargetNodeId = "n1" };
            var cyclicAudit = MindMapStudioViewModel.AuditGraphCyclesAndFeedbackLoops(
                new[] { n1, n2, n3 },
                new[] { l1, l2, lCycle });

            Assert.Equal(3, cyclicAudit.TotalNodes);
            Assert.Equal(3, cyclicAudit.TotalEdges);
            Assert.False(cyclicAudit.IsPureDAG);
            Assert.Equal(1, cyclicAudit.CyclicComponentCount);
            Assert.Equal(3, cyclicAudit.TotalNodesInCycles);
            Assert.Equal(3, cyclicAudit.LongestCycleNodeCount);
            Assert.True(cyclicAudit.AcyclicityScore < 100.0);
            Assert.Contains("3-node directed cycle", cyclicAudit.CyclicComponents[0].CycleDescription);

            string formatted = MindMapStudioViewModel.FormatCycleSummary(
                dagAudit.TotalNodes, dagAudit.TotalEdges, dagAudit.TotalSCCCount, dagAudit.CyclicComponentCount,
                dagAudit.TotalNodesInCycles, dagAudit.LongestCycleNodeCount, dagAudit.AcyclicityScore,
                dagAudit.TopologyCategory, dagAudit.IsSelectionScope);
            Assert.Equal(dagAudit.Summary, formatted);
        }

        [Fact]
        public void Run176_AuditGraphDiameterAndSmallWorld_Comprehensive()
        {
            var emptyAudit = MindMapStudioViewModel.AuditGraphDiameterAndSmallWorld(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.Equal(0, emptyAudit.Diameter);
            Assert.Equal(100.0, emptyAudit.SmallWorldIndexScore);
            Assert.Equal("Empty Map", emptyAudit.Category);

            // Linear chain: n1 - n2 - n3 - n4
            var n1 = new MindMapNode { Id = "n1", Title = "Origin Hub" };
            var n2 = new MindMapNode { Id = "n2", Title = "Bridge Node" };
            var n3 = new MindMapNode { Id = "n3", Title = "Relay Node" };
            var n4 = new MindMapNode { Id = "n4", Title = "Terminal Node" };

            var l1 = new MindMapLink { Id = "l1", SourceNodeId = "n1", TargetNodeId = "n2" };
            var l2 = new MindMapLink { Id = "l2", SourceNodeId = "n2", TargetNodeId = "n3" };
            var l3 = new MindMapLink { Id = "l3", SourceNodeId = "n3", TargetNodeId = "n4" };

            var chainAudit = MindMapStudioViewModel.AuditGraphDiameterAndSmallWorld(
                new[] { n1, n2, n3, n4 },
                new[] { l1, l2, l3 });

            Assert.Equal(4, chainAudit.TotalNodes);
            Assert.Equal(3, chainAudit.TotalEdges);
            Assert.Equal(6, chainAudit.ConnectedPairs);
            Assert.Equal(3, chainAudit.Diameter);
            Assert.Equal(1, chainAudit.ComponentCount);
            Assert.True(chainAudit.CharacteristicPathLength > 1.0);
            Assert.True(chainAudit.GlobalEfficiency > 0.0);
            Assert.Equal(4, chainAudit.NodeMetrics.Count);

            // Add shortcut between n1 and n4 (makes it a 4-cycle) -> diameter drops to 2
            var lShortcut = new MindMapLink { Id = "lshort", SourceNodeId = "n1", TargetNodeId = "n4" };
            var meshAudit = MindMapStudioViewModel.AuditGraphDiameterAndSmallWorld(
                new[] { n1, n2, n3, n4 },
                new[] { l1, l2, l3, lShortcut });

            Assert.Equal(4, meshAudit.TotalNodes);
            Assert.Equal(4, meshAudit.TotalEdges);
            Assert.Equal(2, meshAudit.Diameter);
            Assert.True(meshAudit.GlobalEfficiency > chainAudit.GlobalEfficiency);

            string formatted = MindMapStudioViewModel.FormatSmallWorldSummary(
                chainAudit.TotalNodes, chainAudit.TotalEdges, chainAudit.ConnectedPairs, chainAudit.Diameter,
                chainAudit.CharacteristicPathLength, chainAudit.GlobalEfficiency, chainAudit.ComponentCount,
                chainAudit.SmallWorldIndexScore, chainAudit.Category, chainAudit.IsSelectionScope);
            Assert.Equal(chainAudit.Summary, formatted);
        }

        [Fact]
        public void Run177_AuditDegreeDistributionAndHubs_And_FormatDegreeDistributionSummary()
        {
            var emptyAudit = MindMapStudioViewModel.AuditDegreeDistributionAndHubs(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.Equal(100.0, emptyAudit.ScaleFreeFitScore);
            Assert.Equal("Empty Map", emptyAudit.Category);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // Star topology: 1 central super-hub connected to 5 leaf nodes
            var hub = new MindMapNode { Id = "hub", Title = "Central Super Hub" };
            var l1 = new MindMapNode { Id = "l1", Title = "Leaf 1" };
            var l2 = new MindMapNode { Id = "l2", Title = "Leaf 2" };
            var l3 = new MindMapNode { Id = "l3", Title = "Leaf 3" };
            var l4 = new MindMapNode { Id = "l4", Title = "Leaf 4" };
            var l5 = new MindMapNode { Id = "l5", Title = "Leaf 5" };

            var e1 = new MindMapLink { Id = "e1", SourceNodeId = "hub", TargetNodeId = "l1" };
            var e2 = new MindMapLink { Id = "e2", SourceNodeId = "hub", TargetNodeId = "l2" };
            var e3 = new MindMapLink { Id = "e3", SourceNodeId = "hub", TargetNodeId = "l3" };
            var e4 = new MindMapLink { Id = "e4", SourceNodeId = "hub", TargetNodeId = "l4" };
            var e5 = new MindMapLink { Id = "e5", SourceNodeId = "hub", TargetNodeId = "l5" };

            var starAudit = MindMapStudioViewModel.AuditDegreeDistributionAndHubs(
                new[] { hub, l1, l2, l3, l4, l5 },
                new[] { e1, e2, e3, e4, e5 });

            Assert.Equal(6, starAudit.TotalNodes);
            Assert.Equal(5, starAudit.TotalEdges);
            Assert.Equal(5, starAudit.MaxDegree);
            Assert.Equal(5, starAudit.LeafCount);
            Assert.Equal(0, starAudit.IsolatedCount);
            Assert.True(starAudit.DegreeSkewness > 0.5);
            Assert.Contains("Scale-Free", starAudit.Category);

            // Isolated node test
            var iso = new MindMapNode { Id = "iso", Title = "Disconnected Node" };
            var fragAudit = MindMapStudioViewModel.AuditDegreeDistributionAndHubs(
                new[] { hub, l1, l2, iso },
                new[] { e1, e2 });

            Assert.Equal(4, fragAudit.TotalNodes);
            Assert.Equal(1, fragAudit.IsolatedCount);
            Assert.Contains("Fragmented", fragAudit.Category);

            string formatted = MindMapStudioViewModel.FormatDegreeDistributionSummary(
                starAudit.TotalNodes, starAudit.TotalEdges, starAudit.MeanDegree, starAudit.DegreeStdDev,
                starAudit.DegreeSkewness, starAudit.MaxDegree, starAudit.SuperHubCount, starAudit.LeafCount,
                starAudit.IsolatedCount, starAudit.ScaleFreeFitScore, starAudit.Category, starAudit.IsSelectionScope);
            Assert.Equal(starAudit.Summary, formatted);
        }

        [Fact]
        public void Run178_AuditArticulationPointsAndBiconnectivity()
        {
            var emptyAudit = MindMapStudioViewModel.AuditArticulationPointsAndBiconnectivity(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.FaultToleranceScore);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2-connected cycle graph: n1-n2-n3-n1
            var n1 = new MindMapNode { Id = "n1", Title = "Alpha" };
            var n2 = new MindMapNode { Id = "n2", Title = "Beta" };
            var n3 = new MindMapNode { Id = "n3", Title = "Gamma" };
            var e1 = new MindMapLink { SourceNodeId = "n1", TargetNodeId = "n2" };
            var e2 = new MindMapLink { SourceNodeId = "n2", TargetNodeId = "n3" };
            var e3 = new MindMapLink { SourceNodeId = "n3", TargetNodeId = "n1" };

            var cycleAudit = MindMapStudioViewModel.AuditArticulationPointsAndBiconnectivity(
                new[] { n1, n2, n3 },
                new[] { e1, e2, e3 });

            Assert.Equal(3, cycleAudit.TotalNodes);
            Assert.Equal(3, cycleAudit.TotalEdges);
            Assert.Equal(0, cycleAudit.CutVertexCount);
            Assert.Equal(0, cycleAudit.BridgeCount);
            Assert.Equal(100.0, cycleAudit.FaultToleranceScore);
            Assert.Contains("2-Connected Fault Tolerant", cycleAudit.Category);

            // Tree / Chain: n1-n2-n3 (n2 is articulation point, both e1 & e2 are bridges)
            var chainAudit = MindMapStudioViewModel.AuditArticulationPointsAndBiconnectivity(
                new[] { n1, n2, n3 },
                new[] { e1, e2 });

            Assert.Equal(3, chainAudit.TotalNodes);
            Assert.Equal(2, chainAudit.TotalEdges);
            Assert.Equal(1, chainAudit.CutVertexCount);
            Assert.Equal(2, chainAudit.BridgeCount);
            Assert.Equal("n2", chainAudit.CutVertices[0].NodeId);
            Assert.True(chainAudit.FaultToleranceScore < 100.0);

            string formatted = MindMapStudioViewModel.FormatArticulationPointsSummary(
                cycleAudit.TotalNodes, cycleAudit.TotalEdges, cycleAudit.ConnectedComponents,
                cycleAudit.CutVertexCount, cycleAudit.BridgeCount, cycleAudit.BiconnectedBlockCount,
                cycleAudit.FaultToleranceScore, cycleAudit.Category, cycleAudit.IsSelectionScope);
            Assert.Equal(cycleAudit.Summary, formatted);
        }

        [Fact]
        public void Run179_AuditClosenessAndHarmonicCentrality()
        {
            var emptyAudit = MindMapStudioViewModel.AuditClosenessAndHarmonicCentrality(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.BroadcastEfficiencyScore);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            var n1 = new MindMapNode { Id = "n1", Title = "Center" };
            var singleAudit = MindMapStudioViewModel.AuditClosenessAndHarmonicCentrality(new[] { n1 }, null);
            Assert.Equal(1, singleAudit.TotalNodes);
            Assert.Equal(100.0, singleAudit.BroadcastEfficiencyScore);
            Assert.Equal("n1", singleAudit.TopHubNodeId);

            var n2 = new MindMapNode { Id = "n2", Title = "Leaf1" };
            var n3 = new MindMapNode { Id = "n3", Title = "Leaf2" };
            var n4 = new MindMapNode { Id = "n4", Title = "Leaf3" };
            var e1 = new MindMapLink { SourceNodeId = "n1", TargetNodeId = "n2" };
            var e2 = new MindMapLink { SourceNodeId = "n1", TargetNodeId = "n3" };
            var e3 = new MindMapLink { SourceNodeId = "n1", TargetNodeId = "n4" };

            var starAudit = MindMapStudioViewModel.AuditClosenessAndHarmonicCentrality(
                new[] { n1, n2, n3, n4 },
                new[] { e1, e2, e3 });

            Assert.Equal(4, starAudit.TotalNodes);
            Assert.Equal(3, starAudit.TotalEdges);
            Assert.Equal(1, starAudit.ConnectedComponents);
            Assert.Equal("n1", starAudit.TopHubNodeId);
            Assert.Equal("Center", starAudit.TopHubNodeTitle);
            Assert.Equal(1.0, starAudit.TopHubHarmonicScore);
            Assert.True(starAudit.BroadcastEfficiencyScore >= 75.0);

            string formatted = MindMapStudioViewModel.FormatClosenessCentralitySummary(
                starAudit.TotalNodes, starAudit.TotalEdges, starAudit.ConnectedComponents,
                starAudit.AverageHarmonicCentrality, starAudit.BroadcastEfficiencyScore,
                starAudit.TopHubNodeTitle, starAudit.TopHubHarmonicScore, starAudit.Category, starAudit.IsSelectionScope);
            Assert.Equal(starAudit.Summary, formatted);
        }

        [Fact]
        public void Run180_AuditBetweennessCentralityAndGateways_IdentifiesBottlenecksAndDecentralizationScore()
        {
            var emptyAudit = MindMapStudioViewModel.AuditBetweennessCentralityAndGateways(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.DecentralizationScore);
            Assert.Equal("Empty", emptyAudit.Category);

            var a = new MindMapNode { Id = "a", Title = "Node A" };
            var b = new MindMapNode { Id = "b", Title = "Node B" };
            var c = new MindMapNode { Id = "c", Title = "Node C (Bridge)" };
            var d = new MindMapNode { Id = "d", Title = "Node D" };
            var e = new MindMapNode { Id = "e", Title = "Node E" };

            var e1 = new MindMapLink { SourceNodeId = "a", TargetNodeId = "b" };
            var e2 = new MindMapLink { SourceNodeId = "b", TargetNodeId = "c" };
            var e3 = new MindMapLink { SourceNodeId = "c", TargetNodeId = "d" };
            var e4 = new MindMapLink { SourceNodeId = "d", TargetNodeId = "e" };

            var audit = MindMapStudioViewModel.AuditBetweennessCentralityAndGateways(
                new[] { a, b, c, d, e },
                new[] { e1, e2, e3, e4 });

            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal(4, audit.TotalEdges);
            Assert.Equal(1, audit.ConnectedComponents);
            Assert.Equal("c", audit.TopBottleneckNodeId);
            Assert.Equal("Node C (Bridge)", audit.TopBottleneckTitle);
            Assert.True(audit.TopBottleneckScore > 0);
            Assert.True(audit.GatewayBottleneckCount >= 1);

            string formatted = MindMapStudioViewModel.FormatBetweennessCentralitySummary(
                audit.TotalNodes, audit.TotalEdges, audit.ConnectedComponents,
                audit.GatewayBottleneckCount, audit.AverageBetweenness, audit.DecentralizationScore,
                audit.TopBottleneckTitle, audit.TopBottleneckScore, audit.Category, audit.IsSelectionScope);
            Assert.Equal(audit.Summary, formatted);
        }

        [Fact]
        public void Run181_AuditDegreeDistributionAndHubDominance_EvaluatesConnectivityAndTopologyScore()
        {
            var emptyAudit = MindMapStudioViewModel.AuditDegreeDistributionAndHubDominance(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.TopologyBalanceScore);
            Assert.Equal("Empty Map", emptyAudit.Category);

            var hub = new MindMapNode { Id = "hub", Title = "Central Hub" };
            var n1 = new MindMapNode { Id = "n1", Title = "Leaf 1" };
            var n2 = new MindMapNode { Id = "n2", Title = "Leaf 2" };
            var n3 = new MindMapNode { Id = "n3", Title = "Leaf 3" };
            var iso = new MindMapNode { Id = "iso", Title = "Isolated" };

            var l1 = new MindMapLink { SourceNodeId = "hub", TargetNodeId = "n1" };
            var l2 = new MindMapLink { SourceNodeId = "hub", TargetNodeId = "n2" };
            var l3 = new MindMapLink { SourceNodeId = "hub", TargetNodeId = "n3" };

            var audit = MindMapStudioViewModel.AuditDegreeDistributionAndHubDominance(
                new[] { hub, n1, n2, n3, iso },
                new[] { l1, l2, l3 });

            Assert.Equal(5, audit.TotalNodes);
            Assert.Equal(3, audit.TotalEdges);
            Assert.Equal(3, audit.MaxDegree);
            Assert.Equal("hub", audit.TopHubNodeId);
            Assert.Equal(1, audit.IsolatedCount);
            Assert.Equal(3, audit.LeafCount);
            Assert.True(audit.HubCount >= 1);
            Assert.True(audit.TopologyBalanceScore < 100.0);

            string formatted = MindMapStudioViewModel.FormatDegreeDistributionSummary(
                audit.TotalNodes, audit.TotalEdges, audit.AverageDegree, audit.MaxDegree,
                audit.HubCount, audit.LeafCount, audit.IsolatedCount, audit.HubDominanceRatio,
                audit.TopologyBalanceScore, audit.TopHubTitle, audit.Category, audit.IsSelectionScope);
            Assert.Equal(audit.Summary, formatted);
        }

        [Fact]
        public void Run182_AuditTopologicalHierarchyAndCycles_EvaluatesAcyclicityAndStrata()
        {
            // 1. Empty / null input
            var emptyAudit = MindMapStudioViewModel.AuditTopologicalHierarchyAndCycles(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.Equal(100.0, emptyAudit.AcyclicityScore);
            Assert.Equal("Empty Map", emptyAudit.HierarchyClassification);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Directed Acyclic Graph (DAG) with clean strata
            var root = new MindMapNode { Id = "root", Title = "Root Project" };
            var arch = new MindMapNode { Id = "arch", Title = "Architecture" };
            var ui = new MindMapNode { Id = "ui", Title = "UI Layer" };
            var db = new MindMapNode { Id = "db", Title = "Data Layer" };

            var l1 = new MindMapLink { SourceNodeId = "root", TargetNodeId = "arch" };
            var l2 = new MindMapLink { SourceNodeId = "arch", TargetNodeId = "ui" };
            var l3 = new MindMapLink { SourceNodeId = "arch", TargetNodeId = "db" };

            var dagAudit = MindMapStudioViewModel.AuditTopologicalHierarchyAndCycles(
                new[] { root, arch, ui, db },
                new[] { l1, l2, l3 });

            Assert.Equal(4, dagAudit.TotalNodes);
            Assert.Equal(3, dagAudit.TotalEdges);
            Assert.Equal(0, dagAudit.CycleCount);
            Assert.Equal(0, dagAudit.CycleNodeCount);
            Assert.True(dagAudit.StratumLayerCount >= 3);
            Assert.True(dagAudit.AcyclicityScore >= 90.0);
            Assert.Equal("Pure DAG (Acyclic Layered)", dagAudit.HierarchyClassification);

            string formatted = MindMapStudioViewModel.FormatTopologicalHierarchySummary(
                dagAudit.TotalNodes, dagAudit.TotalEdges, dagAudit.StratumLayerCount, dagAudit.MaxStratumWidth,
                dagAudit.CycleNodeCount, dagAudit.CycleCount, dagAudit.AcyclicityScore, dagAudit.HierarchyClassification, dagAudit.IsSelectionScope);
            Assert.Equal(dagAudit.Summary, formatted);

            // 3. Cyclic graph with loop
            var c1 = new MindMapNode { Id = "c1", Title = "Cycle Node 1" };
            var c2 = new MindMapNode { Id = "c2", Title = "Cycle Node 2" };
            var c3 = new MindMapNode { Id = "c3", Title = "Cycle Node 3" };

            var cl1 = new MindMapLink { SourceNodeId = "c1", TargetNodeId = "c2" };
            var cl2 = new MindMapLink { SourceNodeId = "c2", TargetNodeId = "c3" };
            var cl3 = new MindMapLink { SourceNodeId = "c3", TargetNodeId = "c1" };

            var cycleAudit = MindMapStudioViewModel.AuditTopologicalHierarchyAndCycles(
                new[] { c1, c2, c3 },
                new[] { cl1, cl2, cl3 });

            Assert.Equal(3, cycleAudit.TotalNodes);
            Assert.Equal(3, cycleAudit.TotalEdges);
            Assert.True(cycleAudit.CycleCount >= 1);
            Assert.True(cycleAudit.CycleNodeCount >= 3);
            Assert.True(cycleAudit.AcyclicityScore < 100.0);
        }

        [Fact]
        public void Run183_AuditEdgeCrossingsAndPlanarity_EvaluatesClutterAndPlanarity()
        {
            // 1. Empty / null input
            var emptyAudit = MindMapStudioViewModel.AuditEdgeCrossingsAndPlanarity(null, null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(0, emptyAudit.TotalEdges);
            Assert.Equal(100.0, emptyAudit.PlanarityScore);
            Assert.Equal("Empty Map", emptyAudit.Category);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Pure planar graph (triangle or non-crossing grid)
            // Node positions:
            // n1: (0, 0), n2: (100, 0), n3: (50, 100)
            var n1 = new MindMapNode { Id = "n1", Title = "Top-Left", X = 0, Y = 0, Width = 50, Height = 30 };
            var n2 = new MindMapNode { Id = "n2", Title = "Top-Right", X = 200, Y = 0, Width = 50, Height = 30 };
            var n3 = new MindMapNode { Id = "n3", Title = "Bottom", X = 100, Y = 200, Width = 50, Height = 30 };

            var e1 = new MindMapLink { SourceNodeId = "n1", TargetNodeId = "n2" };
            var e2 = new MindMapLink { SourceNodeId = "n2", TargetNodeId = "n3" };

            var planarAudit = MindMapStudioViewModel.AuditEdgeCrossingsAndPlanarity(
                new[] { n1, n2, n3 },
                new[] { e1, e2 });

            Assert.Equal(3, planarAudit.TotalNodes);
            Assert.Equal(2, planarAudit.TotalEdges);
            Assert.Equal(0, planarAudit.CrossingCount);
            Assert.Equal(100.0, planarAudit.PlanarityScore);
            Assert.Equal("Planar (Zero Crossings)", planarAudit.Category);

            string formatted = MindMapStudioViewModel.FormatEdgeCrossingSummary(
                planarAudit.TotalNodes, planarAudit.TotalEdges, planarAudit.TotalEdgePairsChecked,
                planarAudit.CrossingCount, planarAudit.AcuteAngleCrossingCount, planarAudit.CrossingDensity,
                planarAudit.PlanarityScore, planarAudit.Category, planarAudit.IsSelectionScope);
            Assert.Equal(planarAudit.Summary, formatted);

            // 3. Crossing edges (X-shaped graph: (0,0)->(200,200) and (200,0)->(0,200))
            var p1 = new MindMapNode { Id = "p1", Title = "TL", X = 0, Y = 0, Width = 20, Height = 20 };
            var p2 = new MindMapNode { Id = "p2", Title = "BR", X = 200, Y = 200, Width = 20, Height = 20 };
            var p3 = new MindMapNode { Id = "p3", Title = "TR", X = 200, Y = 0, Width = 20, Height = 20 };
            var p4 = new MindMapNode { Id = "p4", Title = "BL", X = 0, Y = 200, Width = 20, Height = 20 };

            var cx1 = new MindMapLink { SourceNodeId = "p1", TargetNodeId = "p2" };
            var cx2 = new MindMapLink { SourceNodeId = "p3", TargetNodeId = "p4" };

            var crossAudit = MindMapStudioViewModel.AuditEdgeCrossingsAndPlanarity(
                new[] { p1, p2, p3, p4 },
                new[] { cx1, cx2 });

            Assert.Equal(4, crossAudit.TotalNodes);
            Assert.Equal(2, crossAudit.TotalEdges);
            Assert.Equal(1, crossAudit.CrossingCount);
            Assert.True(crossAudit.PlanarityScore < 100.0);
            Assert.NotEmpty(crossAudit.Crossings);
        }

        [Fact]
        public void Run184_AuditVisualMassCenterAndSpatialBalance_CalculatesCoGAndQuadrants()
        {
            // 1. Empty graph
            var emptyAudit = MindMapStudioViewModel.AuditVisualMassCenterAndSpatialBalance(Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.SpatialBalanceScore);
            Assert.Equal("Empty Map", emptyAudit.Category);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Single node
            var singleNode = new MindMapNode { Id = "s1", Title = "Root", X = 150, Y = 150 };
            var singleAudit = MindMapStudioViewModel.AuditVisualMassCenterAndSpatialBalance(new[] { singleNode }, null);
            Assert.Equal(1, singleAudit.TotalNodes);
            Assert.Equal(100.0, singleAudit.SpatialBalanceScore);
            Assert.Equal("Balanced Single Node", singleAudit.Category);

            // 3. Balanced 4-quadrant diamond graph
            var q1 = new MindMapNode { Id = "q1", Title = "Node", X = 300, Y = 100 };
            var q2 = new MindMapNode { Id = "q2", Title = "Node", X = 100, Y = 100 };
            var q3 = new MindMapNode { Id = "q3", Title = "Node", X = 100, Y = 300 };
            var q4 = new MindMapNode { Id = "q4", Title = "Node", X = 300, Y = 300 };

            var balancedAudit = MindMapStudioViewModel.AuditVisualMassCenterAndSpatialBalance(
                new[] { q1, q2, q3, q4 }, null);

            Assert.Equal(4, balancedAudit.TotalNodes);
            Assert.Equal(4, balancedAudit.Quadrants.Count);
            Assert.Equal(100.0, balancedAudit.SpatialBalanceScore);
            Assert.Equal("Optimal Spatial Balance (≥90%)", balancedAudit.Category);

            string formatted = MindMapStudioViewModel.FormatSpatialBalanceSummary(
                balancedAudit.TotalNodes, balancedAudit.CenterOfGravityX, balancedAudit.CenterOfGravityY,
                balancedAudit.CanvasMidpointX, balancedAudit.CanvasMidpointY, balancedAudit.MidpointOffsetDistance,
                balancedAudit.MassVariance, balancedAudit.SpatialBalanceScore, balancedAudit.Category, balancedAudit.IsSelectionScope);
            Assert.Equal(balancedAudit.Summary, formatted);

            // 4. Heavily lopsided graph (all nodes in Top-Left)
            var lop1 = new MindMapNode { Id = "l1", Title = "L1", X = 10, Y = 10 };
            var lop2 = new MindMapNode { Id = "l2", Title = "L2", X = 20, Y = 20 };
            var lop3 = new MindMapNode { Id = "l3", Title = "L3", X = 30, Y = 30 };
            var lop4 = new MindMapNode { Id = "l4", Title = "L4", X = 400, Y = 400 };

            var lopsidedAudit = MindMapStudioViewModel.AuditVisualMassCenterAndSpatialBalance(
                new[] { lop1, lop2, lop3, lop4 }, null);

            Assert.Equal(4, lopsidedAudit.TotalNodes);
            Assert.True(lopsidedAudit.SpatialBalanceScore < 100.0);
            Assert.True(lopsidedAudit.MassVariance > 0.0);
        }

        [Fact]
        public void Run185_AuditNodeProximityAndOverlaps_EvaluatesCollisionsAndClearance()
        {
            // 1. Empty map
            var emptyAudit = MindMapStudioViewModel.AuditNodeProximityAndOverlaps(
                Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.BreathingRoomScore);
            Assert.Equal("Empty Map", emptyAudit.Category);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Single node
            var single = new MindMapNode { Id = "s", Title = "Sole Node", X = 200, Y = 200 };
            var singleAudit = MindMapStudioViewModel.AuditNodeProximityAndOverlaps(
                new[] { single }, null);
            Assert.Equal(1, singleAudit.TotalNodes);
            Assert.Equal(100.0, singleAudit.BreathingRoomScore);
            Assert.Equal("Single Node · Full Clearance", singleAudit.Category);

            // 3. Two well-spaced nodes with ample clearance
            var n1 = new MindMapNode { Id = "n1", Title = "Node 1", X = 100, Y = 100 };
            var n2 = new MindMapNode { Id = "n2", Title = "Node 2", X = 400, Y = 400 };

            var wellSpacedAudit = MindMapStudioViewModel.AuditNodeProximityAndOverlaps(
                new[] { n1, n2 }, null);

            Assert.Equal(2, wellSpacedAudit.TotalNodes);
            Assert.Equal(1, wellSpacedAudit.TotalPairsEvaluated);
            Assert.Equal(0, wellSpacedAudit.CollisionOverlapCount);
            Assert.Equal(0, wellSpacedAudit.CrowdedPairCount);
            Assert.Equal(100.0, wellSpacedAudit.BreathingRoomScore);
            Assert.Equal("Optimal Spatial Breathing Room (≥90%)", wellSpacedAudit.Category);

            string formatted = MindMapStudioViewModel.FormatNodeProximitySummary(
                wellSpacedAudit.TotalNodes, wellSpacedAudit.TotalPairsEvaluated,
                wellSpacedAudit.CollisionOverlapCount, wellSpacedAudit.CrowdedPairCount,
                wellSpacedAudit.MinClearanceDistance, wellSpacedAudit.AverageClearanceDistance,
                wellSpacedAudit.BreathingRoomScore, wellSpacedAudit.Category, wellSpacedAudit.IsSelectionScope);
            Assert.Equal(wellSpacedAudit.Summary, formatted);

            // 4. Overlapping colliding nodes
            var c1 = new MindMapNode { Id = "c1", Title = "Overlap 1", X = 200, Y = 200 };
            var c2 = new MindMapNode { Id = "c2", Title = "Overlap 2", X = 220, Y = 210 };

            var collisionAudit = MindMapStudioViewModel.AuditNodeProximityAndOverlaps(
                new[] { c1, c2 }, null);

            Assert.Equal(2, collisionAudit.TotalNodes);
            Assert.Equal(1, collisionAudit.CollisionOverlapCount);
            Assert.True(collisionAudit.BreathingRoomScore < 100.0);
            Assert.NotEmpty(collisionAudit.ProximalPairs);
            Assert.True(collisionAudit.ProximalPairs[0].IsOverlapping);
        }

        [Fact]
        public void Run186_AuditSubtreeDepthAndBranching_EvaluatesDepthAndSprawl()
        {
            // 1. Empty map
            var emptyAudit = MindMapStudioViewModel.AuditSubtreeDepthAndBranching(
                Array.Empty<MindMapNode>(), null);
            Assert.Equal(0, emptyAudit.TotalNodes);
            Assert.Equal(100.0, emptyAudit.CognitiveBalanceScore);
            Assert.Equal("Empty Map", emptyAudit.Category);
            Assert.Contains("0 nodes", emptyAudit.Summary);

            // 2. Single root node
            var root = new MindMapNode { Id = "r", Title = "Root Node" };
            var singleAudit = MindMapStudioViewModel.AuditSubtreeDepthAndBranching(
                new[] { root }, null);
            Assert.Equal(1, singleAudit.TotalNodes);
            Assert.Equal(1, singleAudit.MaxDepth);
            Assert.Equal(1, singleAudit.LeafNodeCount);
            Assert.Equal(100.0, singleAudit.CognitiveBalanceScore);
            Assert.Equal("Single Node · Uniform Depth", singleAudit.Category);

            // 3. Balanced 2-level hierarchy (root + 3 children)
            var c1 = new MindMapNode { Id = "c1", ParentId = "r", Title = "Child 1" };
            var c2 = new MindMapNode { Id = "c2", ParentId = "r", Title = "Child 2" };
            var c3 = new MindMapNode { Id = "c3", ParentId = "r", Title = "Child 3" };

            var balancedAudit = MindMapStudioViewModel.AuditSubtreeDepthAndBranching(
                new[] { root, c1, c2, c3 }, null);

            Assert.Equal(4, balancedAudit.TotalNodes);
            Assert.Equal(2, balancedAudit.MaxDepth);
            Assert.Equal(3, balancedAudit.LeafNodeCount);
            Assert.Equal(0, balancedAudit.DeepSprawlCount);
            Assert.Equal(0, balancedAudit.ExcessiveBranchingCount);
            Assert.Equal(100.0, balancedAudit.CognitiveBalanceScore);
            Assert.Equal("Optimal Subtree Balance & Depth (≥90%)", balancedAudit.Category);

            string formatted = MindMapStudioViewModel.FormatSubtreeDepthSummary(
                balancedAudit.TotalNodes, balancedAudit.MaxDepth, balancedAudit.AverageDepth,
                balancedAudit.LeafNodeCount, balancedAudit.LeafRatio, balancedAudit.AverageBranchingFactor,
                balancedAudit.DeepSprawlCount, balancedAudit.ExcessiveBranchingCount,
                balancedAudit.CognitiveBalanceScore, balancedAudit.Category, balancedAudit.IsSelectionScope);
            Assert.Equal(balancedAudit.Summary, formatted);

            // 4. Deep sprawl hierarchy (> 6 levels)
            var deepNodes = new List<MindMapNode> { root };
            string lastId = "r";
            for (int i = 1; i <= 8; i++)
            {
                string id = $"level_{i}";
                deepNodes.Add(new MindMapNode { Id = id, ParentId = lastId, Title = $"Sprawl Node {i}" });
                lastId = id;
            }

            var deepAudit = MindMapStudioViewModel.AuditSubtreeDepthAndBranching(deepNodes, null);
            Assert.Equal(9, deepAudit.TotalNodes);
            Assert.Equal(9, deepAudit.MaxDepth);
            Assert.True(deepAudit.DeepSprawlCount >= 1);
            Assert.True(deepAudit.CognitiveBalanceScore < 100.0);
        }
    }
}



