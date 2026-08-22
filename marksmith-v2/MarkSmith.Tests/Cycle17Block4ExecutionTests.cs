using System;
using MarkSmith.Services.Astronomy;
using MarkSmith.Services.Audio;
using MarkSmith.Services.Chemistry;
using MarkSmith.Services.Education;
using MarkSmith.Services.Project;
using Xunit;

namespace MarkSmith.Tests;

public class Cycle17Block4ExecutionTests
{
    [Fact]
    public void FlashcardDeckRendererService_ParsesAndRendersHtmlDeck()
    {
        string deckMd = """
            :::flashcards Computer Architecture
            [Q] What is a CPU cache?
            [A] Fast hardware memory near the processor.
            ---
            [Q] What is pipelining?
            [A] Overlapping execution of multiple instructions.
            :::
            """;

        var model = FlashcardDeckRendererService.ParseDeck(deckMd, "Architecture");
        Assert.Equal(2, model.Cards.Count);
        Assert.Contains("CPU cache", model.Cards[0].Question);

        string html = FlashcardDeckRendererService.RenderDeckHtml(model);
        Assert.Contains("class=\"ms-flashcard-deck\"", html);
        Assert.Contains("Architecture", html);
        Assert.Contains("CPU cache", html);
        Assert.Contains("pipelining", html);
    }

    [Fact]
    public void CelestialSkyMapService_ParsesStarsAndRendersSvgMap()
    {
        string skyMd = """
            :::skymap Winter Triangle
            star "Betelgeuse" [RA: 05.9 h, Dec: 07.4°] mag=0.4
            star "Sirius" [RA: 06.7 h, Dec: -16.7°] mag=-1.4
            star "Procyon" [RA: 07.6 h, Dec: 05.2°] mag=0.3
            line "Betelgeuse" -> "Sirius"
            line "Sirius" -> "Procyon"
            line "Procyon" -> "Betelgeuse"
            :::
            """;

        var model = CelestialSkyMapService.ParseSkyMap(skyMd);
        Assert.Equal("Winter Triangle", model.Title);
        Assert.Equal(3, model.Stars.Count);
        Assert.Equal(3, model.Lines.Count);

        string svg = CelestialSkyMapService.RenderSkyMapSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Winter Triangle", svg);
        Assert.Contains("Betelgeuse", svg);
        Assert.Contains("Sirius", svg);
        Assert.Contains("class=\"star-glow\"", svg);
    }

    [Fact]
    public void GanttScheduleRendererService_ParsesTasksAndRendersSvgGantt()
    {
        string ganttMd = """
            :::gantt Product Launch
            task "Alpha Build" 2026-02-01 -> 2026-02-15 [progress: 100%]
            task "Beta Polish" 2026-02-16 -> 2026-02-28 [progress: 50%]
            milestone "Launch Day" 2026-03-01
            :::
            """;

        var model = GanttScheduleRendererService.ParseGantt(ganttMd);
        Assert.Equal("Product Launch", model.Title);
        Assert.Equal(2, model.Tasks.Count);
        Assert.Single(model.Milestones);

        string svg = GanttScheduleRendererService.RenderGanttSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Product Launch", svg);
        Assert.Contains("Alpha Build", svg);
        Assert.Contains("class=\"gt-bar-fill\"", svg);
        Assert.Contains("class=\"gt-ms-diamond\"", svg);
    }

    [Fact]
    public void MarkdownAudioToneService_ParsesTonesAndRendersOscillogramSvg()
    {
        string toneMd = """
            :::audio-tone "Concert A" freq=440 type=sine duration=2.0
            :::
            """;

        var model = MarkdownAudioToneService.ParseTone(toneMd);
        Assert.Equal("Concert A", model.Name);
        Assert.Equal(440, model.FrequencyHz);
        Assert.Equal("sine", model.WaveformType);

        string svg = MarkdownAudioToneService.RenderToneSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Concert A", svg);
        Assert.Contains("440 Hz", svg);
        Assert.Contains("class=\"at-wave\"", svg);
    }

    [Fact]
    public void MolecularBallAndStickRendererService_ParsesAndRendersMoleculeSvg()
    {
        string molMd = """
            :::molecule Ethanol
            atom C1 (0, 0, 0)
            atom C2 (1.5, 0, 0)
            atom O (2.2, 1.2, 0)
            bond C1-C2 single
            bond C2-O single
            :::
            """;

        var model = MolecularBallAndStickRendererService.ParseMolecule(molMd);
        Assert.Equal("Ethanol", model.Name);
        Assert.Equal(3, model.Atoms.Count);
        Assert.Equal(2, model.Bonds.Count);

        string svg = MolecularBallAndStickRendererService.RenderMoleculeSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Ethanol", svg);
        Assert.Contains("class=\"mol-bond\"", svg);
        Assert.Contains("url(#cpk-O)", svg);
    }

    [Fact]
    public void PeriodicTableExplorerService_ParsesAndRendersGridSvg()
    {
        string ptMd = """
            :::periodic-table "Noble Gases" highlight: "noble-gas" focus: "He"
            :::
            """;

        var model = PeriodicTableExplorerService.ParsePeriodicTable(ptMd);
        Assert.Equal("Noble Gases", model.Title);
        Assert.Equal("noble-gas", model.HighlightCategory);
        Assert.Equal("HE", model.FocusSymbol);
        Assert.True(model.Elements.Count > 10);

        string svg = PeriodicTableExplorerService.RenderPeriodicTableSvg(model);
        Assert.Contains("<svg", svg);
        Assert.Contains("Noble Gases", svg);
        Assert.Contains("class=\"elem-box cat-noble-gas elem-focus\"", svg);
    }
}
