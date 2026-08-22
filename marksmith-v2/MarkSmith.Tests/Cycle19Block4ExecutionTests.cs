using System;
using MarkSmith.Services.Electronics;
using MarkSmith.Services.Games;
using MarkSmith.Services.Genetics;
using MarkSmith.Services.Geometry;
using MarkSmith.Services.MathDiagrams;
using MarkSmith.Services.Physics;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle19Block4ExecutionTests
{
    [Fact]
    public void OrigamiCreasePatternService_ParsesAndRendersSvgCreases()
    {
        string ogMd = """
            :::origami "Waterbomb Base Step 2"
            valley (0, 0) -> (100, 100)
            mountain (0, 100) -> (100, 0)
            fold-arrow (50, 0) -> (50, 50)
            :::
            """;

        var model = OrigamiCreasePatternService.ParseOrigami(ogMd);
        Assert.Equal("Waterbomb Base Step 2", model.Title);
        Assert.Equal(3, model.Elements.Count);
        Assert.Equal(CreaseKind.Valley, model.Elements[0].Kind);
        Assert.Equal(CreaseKind.Mountain, model.Elements[1].Kind);

        string svg = OrigamiCreasePatternService.RenderOrigamiSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Waterbomb Base Step 2", svg);
        Assert.Contains("og-valley", svg);
        Assert.Contains("og-mountain", svg);
    }

    [Fact]
    public void ChessFenBoardRendererService_ParsesFenAndRendersSvgBoard()
    {
        string chessMd = """
            :::chess "Sicilian Defense"
            FEN: "rnbqkbnr/pp1ppppp/8/2p5/4P3/8/PPPP1PPP/RNBQKBNR w KQkq c6 0 2"
            focus: "c5"
            :::
            """;

        var model = ChessFenBoardRendererService.ParseChess(chessMd);
        Assert.Equal("Sicilian Defense", model.Title);
        Assert.Equal("c5", model.FocusSquare);
        Assert.Equal('p', model.Board[3, 2]); // c5 has black pawn

        string svg = ChessFenBoardRendererService.RenderChessSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Sicilian Defense", svg);
        Assert.Contains("ch-focus", svg);
        Assert.Contains("ch-piece", svg);
    }

    [Fact]
    public void OpticalLensRayTracingService_ParsesOpticsAndRendersSvg()
    {
        string opticsMd = """
            :::optics "Simple Magnifier"
            lens convex pos=50 f=100
            ray (0, 20) -> (50, 20)
            ray (0, -20) -> (50, -20)
            :::
            """;

        var model = OpticalLensRayTracingService.ParseOptics(opticsMd);
        Assert.Equal("Simple Magnifier", model.Title);
        Assert.Single(model.Lenses);
        Assert.Equal(2, model.Rays.Count);

        string svg = OpticalLensRayTracingService.RenderOpticsSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Simple Magnifier", svg);
        Assert.Contains("op-lens", svg);
        Assert.Contains("op-ray", svg);
    }

    [Fact]
    public void SorobanAbacusRendererService_ParsesAndRendersSvgAbacus()
    {
        string abacusMd = """
            :::abacus "Math Counter"
            value = 2026
            :::
            """;

        var model = SorobanAbacusRendererService.ParseAbacus(abacusMd);
        Assert.Equal("Math Counter", model.Title);
        Assert.Equal(2026, model.Value);

        string svg = SorobanAbacusRendererService.RenderAbacusSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Math Counter", svg);
        Assert.Contains("ab-bead", svg);
        Assert.Contains("ab-beam", svg);
    }

    [Fact]
    public void DnaCodonTranslationService_TranslatesAndRendersSvgStrand()
    {
        string dnaMd = """
            :::dna "Target Exon"
            seq = "ATGGCCTGA"
            :::
            """;

        var model = DnaCodonTranslationService.ParseDna(dnaMd);
        Assert.Equal("Target Exon", model.Title);
        Assert.Equal("ATGGCCTGA", model.SenseStrand);
        Assert.Equal("TACCGGACT", model.AntiSenseStrand);
        Assert.Equal(3, model.AminoAcids.Count);
        Assert.Equal("Met", model.AminoAcids[0]);
        Assert.Equal("Ala", model.AminoAcids[1]);
        Assert.Equal("STOP", model.AminoAcids[2]);

        string svg = DnaCodonTranslationService.RenderDnaSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Target Exon", svg);
        Assert.Contains("dna-base-a", svg);
        Assert.Contains("dna-aa", svg);
    }

    [Fact]
    public void SmithChartRendererService_ParsesAndRendersSvgChart()
    {
        string smithMd = """
            :::smith-chart "Antenna Tuning"
            point Z1 = 0.5 + 0.8 j [label: "Load"]
            point Z2 = 1.0 + 0.0 j [label: "Match"]
            :::
            """;

        var model = SmithChartRendererService.ParseSmithChart(smithMd);
        Assert.Equal("Antenna Tuning", model.Title);
        Assert.Equal(2, model.Points.Count);
        Assert.Equal("Load", model.Points[0].Label);
        Assert.Equal(0.5, model.Points[0].R);
        Assert.Equal(0.8, model.Points[0].X);

        string svg = SmithChartRendererService.RenderSmithChartSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Antenna Tuning", svg);
        Assert.Contains("sm-outer", svg);
        Assert.Contains("sm-pt", svg);
    }
}
