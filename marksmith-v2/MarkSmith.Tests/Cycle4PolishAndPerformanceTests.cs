using System.Collections.Generic;
using System.Linq;
using MarkSmith.Core.Composer;
using MarkSmith.Models.MindMap;
using MarkSmith.Services;
using MarkSmith.ViewModels.MindMap;
using Xunit;

namespace MarkSmith.Tests
{
    public class Cycle4PolishAndPerformanceTests
    {
        [Fact]
        public void FastMathTokenizer_DetectsMhchemFormulas()
        {
            const string chem1 = @"Water is \ce{H2O} and glucose is \ce{C6H12O6}.";
            const string chem2 = @"Energy release is \pu{12.5 kJ/mol}.";
            const string regularMath = @"Euler formula is $e^{i\pi} + 1 = 0$.";

            Assert.True(FastMathTokenizer.HasChemistryFormulas(chem1));
            Assert.True(FastMathTokenizer.HasChemistryFormulas(chem2));
            Assert.False(FastMathTokenizer.HasChemistryFormulas(regularMath));
        }

        [Fact]
        public void ShapeMarkdownCodec_SimplifiesCollinearStrokes()
        {
            var points = new List<(double X, double Y)>
            {
                (0, 0),
                (1, 1),
                (2, 2),
                (3, 3),
                (4, 4),
                (5, 5)
            };

            var simplified = ShapeMarkdownCodec.SimplifyPolyline(points, epsilon: 0.1);
            Assert.Equal(2, simplified.Count);
            Assert.Equal(0, simplified[0].X);
            Assert.Equal(5, simplified[1].X);
        }

        [Fact]
        public void MindMapStudio_FiltersNodesByTagAndSearch()
        {
            var vm = new MindMapStudioViewModel();
            var doc = new MindMapDocument
            {
                Title = "Galaxy Test",
                Nodes = new List<MindMapNode>
                {
                    new() { Id = "1", Title = "Backend API", Tags = new List<string> { "csharp", "api" } },
                    new() { Id = "2", Title = "Frontend UI", Tags = new List<string> { "react", "ui" } },
                    new() { Id = "3", Title = "Database Schema", Tags = new List<string> { "sql", "api" } }
                }
            };

            vm.LoadDocument(doc);
            Assert.Contains("api", vm.DistinctTags);
            Assert.Contains("csharp", vm.DistinctTags);

            // Filter by tag "api"
            vm.SelectedTagFilter = "api";
            Assert.False(vm.Nodes.First(n => n.Id == "1").IsDimmed);
            Assert.True(vm.Nodes.First(n => n.Id == "2").IsDimmed);
            Assert.False(vm.Nodes.First(n => n.Id == "3").IsDimmed);

            // Search query "Backend"
            vm.SearchQuery = "Backend";
            Assert.False(vm.Nodes.First(n => n.Id == "1").IsDimmed);
            Assert.True(vm.Nodes.First(n => n.Id == "3").IsDimmed);
        }
    }
}
