using System;
using MarkSmith.Services.Crystallography;
using MarkSmith.Services.Games;
using MarkSmith.Services.Geology;
using MarkSmith.Services.MachineLearning;
using MarkSmith.Services.MathDiagrams;
using MarkSmith.Services.Telephony;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle18Block4ExecutionTests
{
    [Fact]
    public void MorseCodeTelegraphService_EncodesAndRendersSvg()
    {
        string morseMd = """
            :::morse "SOS MARKSMITH" [wpm: 25]
            :::
            """;

        var model = MorseCodeTelegraphService.ParseMorse(morseMd);
        Assert.Equal("SOS MARKSMITH", model.PlainText);
        Assert.Equal(25, model.Wpm);
        Assert.Contains("... --- ...", model.MorseSequence);

        string svg = MorseCodeTelegraphService.RenderMorseSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("SOS MARKSMITH", svg);
        Assert.Contains("... --- ...", svg);
        Assert.Contains("class=\"mo-led\"", svg);
    }

    [Fact]
    public void NeuralTopologyRendererService_ParsesAndRendersSvgTopology()
    {
        string nnMd = """
            :::nn Classifier Network
            layer "Input" nodes=4
            layer "Hidden 1" nodes=6 act=relu
            layer "Output" nodes=2 act=softmax
            :::
            """;

        var model = NeuralTopologyRendererService.ParseTopology(nnMd);
        Assert.Equal("Classifier Network", model.Title);
        Assert.Equal(3, model.Layers.Count);
        Assert.Equal(4, model.Layers[0].NodeCount);
        Assert.Equal("relu", model.Layers[1].Activation);

        string svg = NeuralTopologyRendererService.RenderTopologySvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Classifier Network", svg);
        Assert.Contains("class=\"nn-synapse\"", svg);
        Assert.Contains("class=\"nn-node\"", svg);
    }

    [Fact]
    public void StratigraphicColumnService_ParsesLayersAndRendersSvgColumn()
    {
        string stratMd = """
            :::stratigraphy Well Log A-1
            layer 0-20m "Sandstone Unit" [lithology: sandstone, color: #fef08a]
            layer 20-50m "Lower Limestone" [lithology: limestone, color: #cbd5e1]
            :::
            """;

        var model = StratigraphicColumnService.ParseStratigraphy(stratMd);
        Assert.Equal("Well Log A-1", model.Title);
        Assert.Equal(2, model.Layers.Count);
        Assert.Equal(0, model.Layers[0].FromDepth);
        Assert.Equal(20, model.Layers[0].ToDepth);

        string svg = StratigraphicColumnService.RenderStratigraphySvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Well Log A-1", svg);
        Assert.Contains("Sandstone Unit", svg);
        Assert.Contains("pat-sandstone", svg);
    }

    [Fact]
    public void SudokuGridRendererService_ParsesGridAndRendersSvgBoard()
    {
        string sudokuMd = """
            :::sudoku "Daily Sudoku"
            5 3 . . 7 . . . .
            6 . . 1 9 5 . . .
            . 9 8 . . . . 6 .
            8 . . . 6 . . . 3
            4 . . 8 . 3 . . 1
            7 . . . 2 . . . 6
            . 6 . . . . 2 8 .
            . . . 4 1 9 . . 5
            . . . . 8 . . 7 9
            :::
            """;

        var model = SudokuGridRendererService.ParseSudoku(sudokuMd);
        Assert.Equal("Daily Sudoku", model.Title);
        Assert.Equal(5, model.Grid[0, 0]);
        Assert.True(model.IsGiven[0, 0]);

        string svg = SudokuGridRendererService.RenderSudokuSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Daily Sudoku", svg);
        Assert.Contains("class=\"su-box-line\"", svg);
        Assert.Contains("class=\"su-num-given\"", svg);
    }

    [Fact]
    public void CrystalLatticeRendererService_ParsesAndRendersSvgLattice()
    {
        string xtalMd = """
            :::crystal "FCC Gold" type=FCC a=4.08
            atom Au (0, 0, 0)
            atom Au (0.5, 0.5, 0)
            atom Au (0.5, 0, 0.5)
            atom Au (0, 0.5, 0.5)
            :::
            """;

        var model = CrystalLatticeRendererService.ParseCrystal(xtalMd);
        Assert.Equal("FCC Gold", model.Title);
        Assert.Equal("FCC", model.LatticeType);
        Assert.Equal(4, model.Atoms.Count);

        string svg = CrystalLatticeRendererService.RenderCrystalSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("FCC Gold", svg);
        Assert.Contains("class=\"xtal-wire\"", svg);
        Assert.Contains("class=\"xtal-atom\"", svg);
    }

    [Fact]
    public void VennDiagramRendererService_ParsesAndRendersSvgVenn()
    {
        string vennMd = """
            :::venn Developer Skillset
            set A: "Frontend" (40)
            set B: "Backend" (50)
            intersection A&B: "Full Stack" (20)
            :::
            """;

        var model = VennDiagramRendererService.ParseVenn(vennMd);
        Assert.Equal("Developer Skillset", model.Title);
        Assert.Equal("Frontend", model.LabelA);
        Assert.Equal(40, model.CountA);
        Assert.Equal("Backend", model.LabelB);
        Assert.Equal("Full Stack", model.LabelIntersection);

        string svg = VennDiagramRendererService.RenderVennSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Developer Skillset", svg);
        Assert.Contains("Frontend", svg);
        Assert.Contains("Backend", svg);
        Assert.Contains("Full Stack", svg);
        Assert.Contains("class=\"circle-a\"", svg);
    }
}
