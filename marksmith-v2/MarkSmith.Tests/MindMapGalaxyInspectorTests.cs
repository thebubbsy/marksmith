using System;
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
    /// Document Galaxy, routine run #43: edits made in the inspector are real edits (they redraw,
    /// mark the map unsaved and undo as one step), the theme is saved with the map, the preview
    /// card acts on the node it shows, and the commands behind the reworked menus and dialogs.
    /// </summary>
    public class MindMapGalaxyInspectorTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"marksmith_galaxy_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A fresh map whose undo history and dirty flag start clean.</summary>
        private static MindMapStudioViewModel CleanStudio()
        {
            var vm = new MindMapStudioViewModel();
            vm.LoadDocument(MindMapStorageService.CreateTutorialGalaxy());
            vm.IsDirty = false;
            return vm;
        }

        // ---- Inspector edits ----

        [Fact]
        public void TypingATitleMarksTheMapUnsavedAndRedrawsTheCanvas()
        {
            var vm = CleanStudio();
            int redraws = 0;
            vm.CanvasRedrawRequested += (_, _) => redraws++;
            var node = vm.Nodes.First();

            node.Title = "Renamed";

            Assert.True(vm.IsDirty);
            Assert.True(redraws > 0);
            Assert.Equal("Renamed", node.Model.Title);
        }

        [Fact]
        public void TypingATitleIsOneUndoStepNotOnePerKeystroke()
        {
            var vm = CleanStudio();
            var node = vm.Nodes.First();
            string original = node.Title;

            node.Title = "N";
            node.Title = "Ne";
            node.Title = "New name";
            Assert.True(vm.CanUndo);
            Assert.Contains("Rename node", vm.UndoToolTip);

            vm.Undo();

            Assert.Equal(original, vm.Nodes.First(n => n.Id == node.Id).Title);
            Assert.False(vm.CanUndo);
        }

        [Fact]
        public void EditingTwoDifferentFieldsMakesTwoUndoSteps()
        {
            var vm = CleanStudio();
            var node = vm.Nodes.First();
            string title = node.Title;
            int progress = node.Progress;

            node.Title = "Changed";
            node.Progress = progress == 50 ? 55 : 50;

            vm.Undo();
            var restored = vm.Nodes.First(n => n.Id == node.Id);
            Assert.Equal(progress, restored.Progress);
            Assert.Equal("Changed", restored.Title);

            vm.Undo();
            Assert.Equal(title, vm.Nodes.First(n => n.Id == node.Id).Title);
        }

        [Fact]
        public void EditingTheTagLineUpdatesTheTagBarAndUndoes()
        {
            var vm = CleanStudio();
            var node = vm.Nodes.First();
            string before = node.TagsText;

            node.TagsText = "alpha, #beta  gamma";

            Assert.Equal(3, node.Tags.Count);
            Assert.Contains(vm.DistinctTags, t => t.Contains("alpha", StringComparison.OrdinalIgnoreCase));
            Assert.True(vm.IsDirty);

            vm.Undo();
            Assert.Equal(before, vm.Nodes.First(n => n.Id == node.Id).TagsText);
        }

        [Fact]
        public void SettingTheSameTagsAgainIsNotAnEdit()
        {
            var vm = CleanStudio();
            var node = vm.Nodes.First(n => n.Tags.Count > 0);

            node.TagsText = node.TagsText;

            Assert.False(vm.IsDirty);
            Assert.False(vm.CanUndo);
        }

        [Fact]
        public void RenamingALinkMarksTheMapUnsaved()
        {
            var vm = CleanStudio();
            var link = vm.Links.First();

            link.Label = "evidence for";

            Assert.True(vm.IsDirty);
            Assert.Equal("evidence for", link.Model.Label);
            vm.Undo();
            Assert.NotEqual("evidence for", vm.Links.First(l => l.Id == link.Id).Label);
        }

        [Fact]
        public void UndoingBackToTheSavedMapClearsUnsaved()
        {
            var vm = CleanStudio();
            vm.Nodes.First().Title = "Edited";
            Assert.True(vm.IsDirty);

            vm.Undo();
            Assert.False(vm.IsDirty);

            vm.Redo();
            Assert.True(vm.IsDirty);
        }

        [Fact]
        public async Task UnsavedFollowsTheStateThatWasSavedThroughUndoAndRedo()
        {
            string dir = NewTempDir();
            try
            {
                var vm = CleanStudio();
                vm.Nodes.First().Title = "Saved title";
                await vm.SaveAsync(Path.Combine(dir, "map.msmap"));
                Assert.False(vm.IsDirty);

                vm.Undo();
                Assert.True(vm.IsDirty);
                vm.Redo();
                Assert.False(vm.IsDirty);

                // Typing again after a save is a new step, not a continuation of the saved one.
                vm.Nodes.First().Title = "After the save";
                Assert.True(vm.IsDirty);
                vm.Undo();
                Assert.False(vm.IsDirty);
                Assert.Equal("Saved title", vm.Nodes.First().Title);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- Theme ----

        [Fact]
        public async Task TheThemeIsSavedWithTheMapAndRestoredWhenItOpens()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "map.msmap");
                var vm = CleanStudio();
                vm.SelectedThemeName = "Clean White";
                Assert.True(vm.IsDirty);
                await vm.SaveAsync(path);

                var reopened = new MindMapStudioViewModel();
                await reopened.InitializeAsync(path);

                Assert.Equal("Clean White", reopened.SelectedThemeName);
                Assert.False(reopened.IsDirty);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ChangingTheThemeCanBeUndone()
        {
            var vm = CleanStudio();
            vm.SelectedThemeName = "Nordic Slate";

            vm.Undo();

            Assert.Equal(MindMapStudioViewModel.DefaultThemeName, vm.SelectedThemeName);
        }

        [Fact]
        public void AnUnknownSavedThemeFallsBackToTheDefault()
        {
            var doc = MindMapStorageService.CreateTutorialGalaxy();
            doc.Theme.Name = "Something Else";
            var vm = new MindMapStudioViewModel();

            vm.LoadDocument(doc);

            Assert.Equal(MindMapStudioViewModel.DefaultThemeName, vm.SelectedThemeName);
        }

        // ---- Saving a copy ----

        [Fact]
        public async Task SavingACopyKeepsTheUnsavedMarker()
        {
            string dir = NewTempDir();
            try
            {
                var vm = CleanStudio();
                vm.Nodes.First().Title = "Edited";
                string copy = Path.Combine(dir, "copy.msmap");

                await vm.SaveCopyAsync(copy);

                Assert.True(File.Exists(copy));
                Assert.True(vm.IsDirty);
                Assert.Contains("copy.msmap", vm.StatusMessage);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---- Commands behind the menus and dialogs ----

        [Fact]
        public void MoveUnderNeverOffersTheNodesOwnBranch()
        {
            var vm = CleanStudio();
            var root = vm.Nodes.First(n => n.Id == vm.Document.RootNodeId);
            vm.SelectedNode = root;
            vm.AddChildNode();
            var child = vm.SelectedNode!;
            vm.AddChildNode();
            var grandchild = vm.SelectedNode!;

            var candidates = vm.ReparentCandidates(child);

            Assert.DoesNotContain(child, candidates);
            Assert.DoesNotContain(grandchild, candidates);
            Assert.Contains(root, candidates);
            Assert.Equal(candidates.OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase).ToList(), candidates.ToList());
        }

        [Fact]
        public void ReversingALinkMarksTheMapUnsavedAndUndoes()
        {
            var vm = CleanStudio();
            var link = vm.Links.First(l => l.Direction == MindMapLinkDirection.SourceToTarget);

            vm.ReverseLink(link);

            Assert.Equal(MindMapLinkDirection.TargetToSource, link.Direction);
            Assert.True(vm.IsDirty);
            vm.Undo();
            Assert.Equal(MindMapLinkDirection.SourceToTarget, vm.Links.First(l => l.Id == link.Id).Direction);
        }

        [Fact]
        public void AttachingAFileNamesAPlaceholderNodeInOneUndoStep()
        {
            var vm = CleanStudio();
            vm.AddRootNode();
            var node = vm.SelectedNode!;
            Assert.Equal(MindMapStudioViewModel.NewNodeTitle, node.Title);
            string path = Path.Combine(Path.GetTempPath(), "Quarterly plan.md");

            vm.AttachFile(node, path);

            Assert.Equal("Quarterly plan", node.Title);
            Assert.Equal(path, node.FilePath);
            Assert.Contains("Attach file", vm.UndoToolTip);
            vm.Undo();
            Assert.Null(vm.Nodes.First(n => n.Id == node.Id).FilePath);
        }

        [Fact]
        public void NewNodesAskToBeNamedButDocumentsPushedFromTheEditorDoNot()
        {
            var vm = CleanStudio();
            int created = 0;
            vm.NodeCreated += (_, _) => created++;

            vm.AddChildNode();
            vm.AddSiblingNode();
            vm.AddRootNode();
            Assert.Equal(3, created);

            vm.EnsureDocumentNode(Path.Combine(Path.GetTempPath(), "pushed.md"));
            Assert.Equal(3, created);
            Assert.Contains("Add node", vm.UndoToolTip); // one step, not a second "Rename node"
        }

        // ---- Preview card ----

        [Fact]
        public void ThePreviewCardActsOnTheNodeItShowsNotTheSelection()
        {
            var vm = CleanStudio();
            var selected = vm.Nodes[0];
            var hovered = vm.Nodes[1];
            vm.SelectedNode = selected;

            vm.ShowPreviewCard(hovered);

            Assert.Same(hovered, vm.PreviewNode);
            Assert.Equal(hovered.Title, vm.PreviewTitle);
            vm.HidePreviewCard();
            Assert.Null(vm.PreviewNode);
        }

        [Fact]
        public void ANodeWithoutAFileHasNothingToOpenOrShowHistoryFor()
        {
            var vm = CleanStudio();
            vm.AddRootNode();
            var node = vm.SelectedNode!;

            vm.ShowPreviewCard(node);

            Assert.False(vm.PreviewHasFile);
            Assert.Equal("No file attached", vm.PreviewFilePath);
        }

        // ---- Words ----

        [Fact]
        public void SearchResultsAreCountedInWords()
        {
            var vm = CleanStudio();
            vm.SearchQuery = "zzzz-no-such-node";
            Assert.Equal("No matches", vm.SearchMatchText);

            vm.SearchQuery = vm.Nodes.First().Title;
            Assert.EndsWith(vm.SearchMatchCount == 1 ? "1 match" : "matches", vm.SearchMatchText);
        }

        [Fact]
        public void LayoutStatusNamesTheLayoutNotTheEnum()
        {
            var vm = CleanStudio();
            vm.ApplyLayout("radial");
            Assert.DoesNotContain("RadialGalaxy", vm.StatusMessage);
            Assert.Contains("radial", vm.StatusMessage);
        }

        [Fact]
        public void TheOverviewIsWrittenInPlainWords()
        {
            var doc = MindMapStorageService.CreateTutorialGalaxy();
            string summary = MindMapGraph.Analyze(doc).HeadlineSummary();

            Assert.DoesNotContain("density", summary);
            Assert.StartsWith($"{doc.Nodes.Count} documents and ", summary);
            Assert.Equal("The map is empty. Add a node or import a folder.",
                MindMapGraph.Analyze(new MindMapDocument()).HeadlineSummary());
        }

        [Fact]
        public void FocusModeSaysWhatItIsShowing()
        {
            var vm = CleanStudio();
            vm.SelectedNode = vm.Nodes.First();

            vm.IsFocusModeEnabled = true;
            Assert.Contains(vm.SelectedNode!.Title, vm.StatusMessage);

            vm.IsFocusModeEnabled = false;
            Assert.Contains("whole map", vm.StatusMessage);
        }
    }
}
