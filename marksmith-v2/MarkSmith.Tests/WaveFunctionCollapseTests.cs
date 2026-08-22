using System;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Quantum;
using Xunit;

namespace MarkSmith.Tests;

public class WaveFunctionCollapseTests
{
    [Fact]
    public void QuantumWaveFunction_ParsesSuperpositionAndCollapse()
    {
        string markdown = """
            :::wavefunction "Quantum Superposition"
            states: |0⟩: 0.6, |1⟩: 0.8
            collapse_to: |1⟩
            :::
            """;

        var model = WaveFunctionCollapseService.ParseWaveFunction(markdown);
        Assert.Equal("Quantum Superposition", model.Title);
        Assert.Equal("quantum", model.Mode);
        Assert.Equal(2, model.States.Count);
        Assert.Equal("|0⟩", model.States[0].Label);
        Assert.Equal(0.6, model.States[0].Amplitude, precision: 2);
        Assert.Equal("|1⟩", model.States[1].Label);
        Assert.Equal(0.8, model.States[1].Amplitude, precision: 2);
        Assert.Equal("|1⟩", model.CollapsedStateLabel);

        string svg = WaveFunctionCollapseService.RenderWaveFunctionSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Quantum Superposition", svg);
        Assert.Contains("wf-psi", svg);
        Assert.Contains("wf-prob", svg);
        Assert.Contains("wf-collapsed", svg);
        Assert.Contains("Collapsed → |1⟩", svg);
    }

    [Fact]
    public void ProceduralWfcGrid_ParsesAndRendersEntropyMatrix()
    {
        string markdown = """
            :::wfc "Procedural Dungeon Map"
            size: 6x6
            tiles: Grass, Road, Water, Wall
            collapse: auto
            :::
            """;

        var model = WaveFunctionCollapseService.ParseWaveFunction(markdown);
        Assert.Equal("Procedural Dungeon Map", model.Title);
        Assert.Equal("wfc", model.Mode);
        Assert.Equal(6, model.GridWidth);
        Assert.Equal(6, model.GridHeight);
        Assert.Equal(36, model.GridCells.Count);

        string svg = WaveFunctionCollapseService.RenderWaveFunctionSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Procedural Dungeon Map", svg);
        Assert.Contains("wfc-cell", svg);
        Assert.Contains("Shannon Entropy", svg);
    }

    [Fact]
    public void InsertSnippetBuilder_GeneratesWfcAndQuantumSnippets()
    {
        string wfcSnippet = InsertSnippetBuilder.WaveFunctionCollapse("My Grid", 4, 4);
        Assert.Contains(":::wfc \"My Grid\"", wfcSnippet);
        Assert.Contains("size: 4x4", wfcSnippet);

        string quantumSnippet = InsertSnippetBuilder.QuantumWaveFunction("Qubit Collapse", "|0⟩: 0.707, |1⟩: 0.707", "|0⟩");
        Assert.Contains(":::wavefunction \"Qubit Collapse\"", quantumSnippet);
        Assert.Contains("states: |0⟩: 0.707, |1⟩: 0.707", quantumSnippet);
        Assert.Contains("collapse_to: |0⟩", quantumSnippet);
    }

    [Fact]
    public void MarkdownHtmlService_RendersWaveFunctionBlocksInHtmlPreview()
    {
        string markdown = """
            # Quantum Mechanics Note

            :::wavefunction "Quantum Superposition"
            states: |0⟩: 0.6, |1⟩: 0.8
            collapse_to: |1⟩
            :::
            """;

        var theme = new ThemeDefinition("Default", "#FFFFFF", "#111827", "#111827", "#F3F4F6", "#E5E7EB", "#2563EB", "#F9FAFB", "#E5E7EB");
        string html = new MarkdownHtmlService().Render(markdown, new AppSettings(), theme);
        Assert.Contains("wavefunction-diagram", html);
        Assert.Contains("<svg", html);
        Assert.Contains("Quantum Superposition", html);
    }
}
