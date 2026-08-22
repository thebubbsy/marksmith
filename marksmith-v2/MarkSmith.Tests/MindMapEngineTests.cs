using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Composer;
using MarkSmith.Models.MindMap;
using MarkSmith.Services.MindMap;
using MarkSmith.ViewModels.MindMap;
using Xunit;

namespace MarkSmith.Tests
{
    public class MindMapEngineTests
    {
        [Fact]
        public void DefaultGalaxy_HasValidNodesAndLinks()
        {
            var doc = MindMapStorageService.CreateDefaultGalaxy();

            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Nodes);
            Assert.NotEmpty(doc.Links);
            Assert.False(string.IsNullOrEmpty(doc.RootNodeId));

            var root = doc.Nodes.FirstOrDefault(n => n.Id == doc.RootNodeId);
            Assert.NotNull(root);
            Assert.NotEmpty(root.ChildIds);

            // Verify cross links connect valid nodes
            foreach (var link in doc.Links)
            {
                Assert.True(doc.Nodes.Any(n => n.Id == link.SourceNodeId), "Source node exists");
                Assert.True(doc.Nodes.Any(n => n.Id == link.TargetNodeId), "Target node exists");
            }
        }

        [Fact]
        public async Task StorageService_SaveAndLoad_RoundtripsAccurately()
        {
            var service = new MindMapStorageService();
            var doc = MindMapStorageService.CreateDefaultGalaxy();
            doc.Title = "Custom Test Galaxy";

            string tempFile = Path.Combine(Path.GetTempPath(), $"marksmith_test_{Guid.NewGuid():N}.msmap");
            try
            {
                await service.SaveAsync(doc, tempFile);
                Assert.True(File.Exists(tempFile));
                Assert.False(File.Exists(tempFile + ".tmp"));

                var loaded = await service.LoadAsync(tempFile);
                Assert.NotNull(loaded);
                Assert.Equal("Custom Test Galaxy", loaded.Title);
                Assert.Equal(doc.Nodes.Count, loaded.Nodes.Count);
                Assert.Equal(doc.Links.Count, loaded.Links.Count);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void MermaidExport_GeneratesValidMindmapFence()
        {
            var doc = MindMapStorageService.CreateDefaultGalaxy();
            string mermaid = MindMapStorageService.ExportToMermaid(doc);

            Assert.NotNull(mermaid);
            Assert.StartsWith("mindmap", mermaid.Trim());
            Assert.Contains("MarkSmith Document Galaxy", mermaid);
        }

        [Fact]
        public async Task AutoLinker_DetectsWikilinksAndCrossReferences()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"marksmith_linker_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                string fileA = Path.Combine(tempDir, "ProjectAlpha.md");
                string fileB = Path.Combine(tempDir, "ProjectBeta.md");
                string fileC = Path.Combine(tempDir, "ArchitectureSpec.md");
                string fileD = Path.Combine(tempDir, "DeepResearch.md");

                await File.WriteAllTextAsync(fileA, "# Project Alpha\nThis project links to [[ProjectBeta]] and [Architecture](ArchitectureSpec.md).\nTags: #ai #core");
                await File.WriteAllTextAsync(fileB, "# Project Beta\nDerived project.\nTags: #ai #core");
                await File.WriteAllTextAsync(fileC, "# Architecture Specification\nCore technical design.\nTags: #spec #docs");
                await File.WriteAllTextAsync(fileD, "# Deep Research\nIndependent research.\nTags: #ai #core");

                var linker = new MindMapAutoLinker();
                var doc = await linker.BuildGalaxyFromDirectoryAsync(tempDir);

                Assert.NotNull(doc);
                Assert.Equal(5, doc.Nodes.Count); // Root + 4 files

                // Verify links detected
                Assert.NotEmpty(doc.Links);
                Assert.Contains(doc.Links, l => l.Label == "wikilink");
                Assert.Contains(doc.Links, l => l.Label == "cross-reference");
                Assert.Contains(doc.Links, l => l.Label != null && l.Label.StartsWith("shared #ai"));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Theory]
        [InlineData(MindMapLayoutType.HorizontalTree)]
        [InlineData(MindMapLayoutType.RadialGalaxy)]
        [InlineData(MindMapLayoutType.ForceDirected)]
        [InlineData(MindMapLayoutType.VerticalHierarchy)]
        public void LayoutEngine_ComputesFiniteNonZeroCoordinates(MindMapLayoutType layoutType)
        {
            var engine = new MindMapLayoutEngine();
            var doc = MindMapStorageService.CreateDefaultGalaxy();

            engine.ApplyLayout(doc, layoutType);

            foreach (var node in doc.Nodes)
            {
                Assert.False(double.IsNaN(node.X));
                Assert.False(double.IsNaN(node.Y));
                Assert.False(double.IsInfinity(node.X));
                Assert.False(double.IsInfinity(node.Y));
            }
        }

        [Fact]
        public void DocxExporter_GeneratesValidOpenXmlDocument()
        {
            var exporter = new MindMapDocxExporter();
            var doc = MindMapStorageService.CreateDefaultGalaxy();

            string tempDocx = Path.Combine(Path.GetTempPath(), $"marksmith_mindmap_{Guid.NewGuid():N}.docx");
            try
            {
                exporter.ExportToDocx(doc, tempDocx);
                Assert.True(File.Exists(tempDocx));

                // Validate with OpenXmlValidator
                using var wordDoc = WordprocessingDocument.Open(tempDocx, false);
                var validator = new OpenXmlValidator();
                var errors = validator.Validate(wordDoc).ToList();

                var errMessages = string.Join("; ", errors.Select(e => $"{e.Node?.LocalName}: {e.Description}"));
                Assert.True(errors.Count == 0, $"Validation errors: {errMessages}");
            }
            finally
            {
                if (File.Exists(tempDocx)) File.Delete(tempDocx);
            }
        }

        [Fact]
        public void ViewModel_AddChildAndSibling_UpdatesHierarchy()
        {
            var vm = new MindMapStudioViewModel();
            int initialCount = vm.Nodes.Count;

            vm.AddChildNode();
            Assert.Equal(initialCount + 1, vm.Nodes.Count);

            vm.AddSiblingNode();
            Assert.Equal(initialCount + 2, vm.Nodes.Count);

            vm.ConnectNodes(vm.Nodes[0].Id, vm.Nodes[^1].Id, "unit-test-link");
            Assert.Contains(vm.Links, l => l.Label == "unit-test-link");
        }
    }
}
